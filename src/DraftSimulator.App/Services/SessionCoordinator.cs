using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using DraftSimulator.Core;
using DraftSimulator.Infrastructure;
using DraftSimulator.Protocol;
using CoreAssetHash = DraftSimulator.Core.AssetHash;
using CoreCardInstanceId = DraftSimulator.Core.CardInstanceId;
using CorePlayerId = DraftSimulator.Core.PlayerId;
using ProtocolCardInstanceId = DraftSimulator.Protocol.CardInstanceId;
using ProtocolPlayerId = DraftSimulator.Protocol.PlayerId;
using ProtocolSessionId = DraftSimulator.Protocol.SessionId;

namespace DraftSimulator.App.Services;

public enum ApplicationScreen { Start, Lobby, StartingCountdown, PreparingSession, Drafting, Complete }
public enum SteamStatus { Initializing, Ready, Unavailable }
public enum SessionRole { None, Host, Client }

public sealed record HostDraftConfiguration(
    DraftSettings Settings,
    IReadOnlyList<CardDefinition> Definitions,
    HostConfiguration Limits);

public sealed record SessionPlayerSnapshot(
    ProtocolPlayerId PlayerId,
    string Name,
    int Order,
    bool IsHost,
    bool IsReady,
    bool IsLocal,
    ConnectionStatus ConnectionStatus,
    PreparationStatus PreparationStatus = PreparationStatus.Waiting,
    bool IsDraftLocked = false,
    int CurrentPackCardCount = 0);

public sealed record SessionCardSnapshot(
    ProtocolCardInstanceId InstanceId,
    Protocol.AssetHash AssetHash,
    string AssetPath,
    string? ExportName = null);

public sealed record SessionSnapshot(
    ApplicationScreen Screen,
    SteamStatus SteamStatus,
    SessionRole Role,
    string? RoomCode,
    IReadOnlyList<SessionPlayerSnapshot> Players,
    ProtocolPlayerId? LocalPlayerId,
    ErrorCode? ErrorCode,
    string? StatusMessage,
    long CountdownGeneration)
{
    public long BytesReady { get; init; }
    public long BytesTotal { get; init; }
    public long DraftRevision { get; init; }
    public long PickRevision { get; init; }
    public IReadOnlyList<SessionCardSnapshot> CurrentPack { get; init; } = [];
    public IReadOnlyList<ProtocolCardInstanceId> SelectedInstanceIds { get; init; } = [];
    public IReadOnlyList<SessionCardSnapshot> Collection { get; init; } = [];
    public int RequiredSelectionCount { get; init; }
    public bool CanLock { get; init; }
    public bool CanUnlock { get; init; }
    public bool CanForceReady { get; init; }
    public DraftDirection Direction { get; init; } = DraftDirection.Clockwise;
    public int PackRound { get; init; }
    public int TotalPackRounds { get; init; }
    public bool LobbyAvailable { get; init; }
    public bool SteamInviteAvailable { get; init; }

    public static SessionSnapshot Initial { get; } = new(
        ApplicationScreen.Start, SteamStatus.Initializing, SessionRole.None, null, [], null, null,
        "Connecting to Steam...", 0);
}

public sealed record SessionCoordinatorOptions
{
    public TimeSpan CountdownDuration { get; init; } = TimeSpan.FromSeconds(3);
    public LocalAppDataPaths Paths { get; init; } = new();
    public HostConfiguration ClientConfiguration { get; init; } = HostConfiguration.Defaults;
    public IAssetPreparer AssetPreparer { get; init; } = new AssetPreparer();
    public ILocalLogger Logger { get; init; } = NullLocalLogger.Instance;
    public ClientStateStore? ClientStateStore { get; init; }
    public Func<(int Seed, IRandomSource Source)> RandomSourceFactory { get; init; } = static () => SeededRandomSource.CreateCryptographic();
    public Func<Guid> SessionIdFactory { get; init; } = Guid.NewGuid;
}

public sealed class SessionCoordinator : IAsyncDisposable
{
    private sealed class PlayerState
    {
        public PlayerState(ProtocolPlayerId id, SteamPeerId peerId, string name, int order, bool isHost) =>
            (Id, PeerId, Name, Order, IsHost) = (id, peerId, name, order, isHost);
        public ProtocolPlayerId Id { get; }
        public SteamPeerId PeerId { get; }
        public string Name { get; set; }
        public int Order { get; set; }
        public bool IsHost { get; }
        public bool IsReady { get; set; }
        public bool IsConnected { get; set; } = true;
        public PreparationStatus PreparationStatus { get; set; } = PreparationStatus.Waiting;
        public bool SubmittedAssetNeed { get; set; }
        public bool IsReconnectAssetSync { get; set; }
    }

    private sealed class AssetNeedPages
    {
        private SnapshotId? _id;
        private int _count;
        private readonly Dictionary<int, Protocol.AssetHash[]> _pages = [];
        public Protocol.AssetHash[]? Add(AssetNeedDto page)
        {
            if (_id is null) { _id = page.Page.SnapshotId; _count = page.Page.PageCount; }
            if (_id != page.Page.SnapshotId || _count != page.Page.PageCount || page.Page.PageIndex < 0 || page.Page.PageIndex >= _count)
                throw new AssetTransferException("Conflicting AssetNeed pages.");
            if (_pages.TryGetValue(page.Page.PageIndex, out var old) && !old.SequenceEqual(page.Hashes))
                throw new AssetTransferException("Conflicting duplicate AssetNeed page.");
            _pages[page.Page.PageIndex] = page.Hashes;
            return _pages.Count == _count ? _pages.OrderBy(x => x.Key).SelectMany(x => x.Value).ToArray() : null;
        }
    }

    private sealed class DraftPages
    {
        private SnapshotId? _id;
        private long _revision = -1;
        private int _count;
        private readonly Dictionary<int, DraftSnapshotDto> _pages = [];
        public DraftSnapshotDto? Add(DraftSnapshotDto page)
        {
            if (page.Page.Revision > _revision)
            {
                _revision = page.Page.Revision; _id = page.Page.SnapshotId; _count = page.Page.PageCount; _pages.Clear();
            }
            if (page.Page.Revision < _revision) return null;
            if (_id != page.Page.SnapshotId || _count != page.Page.PageCount || page.Page.PageIndex < 0 || page.Page.PageIndex >= _count)
                throw new ProtocolException("Conflicting draft pages.");
            if (_pages.TryGetValue(page.Page.PageIndex, out var old) && !Equivalent(old, page))
                throw new ProtocolException("Conflicting duplicate draft page.");
            _pages[page.Page.PageIndex] = page;
            if (_pages.Count != _count) return null;
            var ordered = _pages.OrderBy(x => x.Key).Select(x => x.Value).ToArray();
            var first = ordered[0];
            if (ordered.Any(x => x.PickRevision != first.PickRevision || x.RequiredSelectionCount != first.RequiredSelectionCount ||
                                 x.CanLock != first.CanLock || x.CanUnlock != first.CanUnlock || x.Direction != first.Direction ||
                                 x.PackRound != first.PackRound || x.TotalPackRounds != first.TotalPackRounds))
                throw new ProtocolException("Draft page metadata does not agree.");
            return first with
            {
                Page = first.Page with { PageIndex = 0, PageCount = 1 },
                CurrentPack = ordered.SelectMany(x => x.CurrentPack).ToArray(),
                SelectedInstanceIds = ordered.SelectMany(x => x.SelectedInstanceIds).ToArray(),
                Collection = ordered.SelectMany(x => x.Collection).ToArray(),
                Players = ordered.SelectMany(x => x.Players).ToArray(),
            };
        }
        private static bool Equivalent(DraftSnapshotDto left, DraftSnapshotDto right) =>
            left.Page == right.Page && left.PickRevision == right.PickRevision &&
            left.CurrentPack.SequenceEqual(right.CurrentPack) && left.SelectedInstanceIds.SequenceEqual(right.SelectedInstanceIds) &&
            left.Collection.SequenceEqual(right.Collection) && left.RequiredSelectionCount == right.RequiredSelectionCount &&
            left.CanLock == right.CanLock && left.CanUnlock == right.CanUnlock && left.Players.SequenceEqual(right.Players) &&
            left.Direction == right.Direction && left.PackRound == right.PackRound && left.TotalPackRounds == right.TotalPackRounds;
        public void Reset() { _id = null; _revision = -1; _count = 0; _pages.Clear(); }
    }

    private sealed class CompletionPages
    {
        private SnapshotId? _id;
        private long _revision = -1;
        private int _count;
        private readonly Dictionary<int, CompletedCardDto[]> _pages = [];
        public CompletedCardDto[]? Add(DraftCompletedDto page)
        {
            if (page.Page.Revision > _revision) { _revision = page.Page.Revision; _id = page.Page.SnapshotId; _count = page.Page.PageCount; _pages.Clear(); }
            if (page.Page.Revision < _revision) return null;
            if (_id != page.Page.SnapshotId || _count != page.Page.PageCount || page.Page.PageIndex < 0 || page.Page.PageIndex >= _count)
                throw new ProtocolException("Conflicting completion pages.");
            if (_pages.TryGetValue(page.Page.PageIndex, out var old) && !old.SequenceEqual(page.Collection))
                throw new ProtocolException("Conflicting duplicate completion page.");
            _pages[page.Page.PageIndex] = page.Collection;
            return _pages.Count == _count ? _pages.OrderBy(x => x.Key).SelectMany(x => x.Value).ToArray() : null;
        }
        public void Reset() { _id = null; _revision = -1; _count = 0; _pages.Clear(); }
    }

    private readonly ISteamTransport _transport;
    private readonly SessionCoordinatorOptions _options;
    private readonly Channel<Func<Task>> _commands = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
    { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _runTask;
    private readonly Task _eventTask;
    private readonly List<PlayerState> _players = [];
    private readonly HashSet<SteamPeerId> _authenticatedPeers = [];
    private readonly HashSet<SteamPeerId> _roomCodeBypassPeers = [];
    private readonly Dictionary<SteamPeerId, AssetNeedPages> _assetNeedPages = [];
    private readonly Dictionary<SteamPeerId, PlayerState> _completedPlayers = [];
    private readonly AssetManifestAccumulator _manifestPages = new();
    private readonly DraftPages _draftPages = new();
    private readonly CompletionPages _completionPages = new();
    private SessionSnapshot _snapshot = SessionSnapshot.Initial;
    private SteamPeerId _localPeer;
    private SteamPeerId _hostPeer;
    private SteamLobbyId? _lobbyId;
    private ProtocolSessionId _sessionId;
    private ProtocolPlayerId? _localPlayerId;
    private string? _roomCode;
    private string? _pendingName;
    private SessionRole _role;
    private ProtocolPhase _phase = ProtocolPhase.Closed;
    private long _lobbyRevision;
    private long _countdownGeneration;
    private int _countdownRemainingSeconds;
    private long _manifestRevision;
    private bool _helloSent;
    private bool _reconnecting;
    private bool _waitingForLobbyClose;
    private int _disposed;
    private HostDraftConfiguration? _hostConfiguration;
    private GeneratedDraft? _generatedDraft;
    private DraftStateMachine? _draft;
    private IRandomSource? _draftRandom;
    private AssetPreparationResult? _preparedAssets;
    private IReadOnlyDictionary<CardDefinitionId, CoreAssetHash> _definitionAssets = new Dictionary<CardDefinitionId, CoreAssetHash>();
    private IReadOnlyDictionary<CardDefinitionId, string> _definitionNames = new Dictionary<CardDefinitionId, string>();
    private ClientAssetTransferEngine? _clientTransfer;
    private AssetTransferProgress _clientProgress = new(0, 0, 0, 0);
    private IReadOnlyList<CompletedCardDto> _clientCompletion = [];
    private long _transferProgressGeneration;
    private long _preparationGeneration;
    private CancellationTokenSource? _preparationCancellation;
    private string? _pendingReopenName;
    private SteamLobbyId? _pendingInviteLobby;
    private bool _configurationInvalid;

    public SessionCoordinator(ISteamTransport transport, SessionCoordinatorOptions? options = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? new SessionCoordinatorOptions();
        if (_options.CountdownDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        _runTask = RunAsync();
        _eventTask = PumpEventsAsync();
    }

    public SessionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public event Action<SessionSnapshot>? SnapshotChanged;
    public Task HostAsync(string playerName) => EnqueueAsync(() => HostCoreAsync(playerName));
    public Task JoinAsync(string playerName, string roomCode) => EnqueueAsync(() => JoinCoreAsync(playerName, roomCode));
    public Task JoinSteamInviteAsync(string playerName) => EnqueueAsync(() => JoinSteamInviteCoreAsync(playerName));
    public Task ReconnectAsync(ClientState state) => EnqueueAsync(() => ReconnectCoreAsync(state));
    public Task ConfigureHostAsync(HostDraftConfiguration configuration) => EnqueueAsync(() => ConfigureHostCoreAsync(configuration));
    public Task SetNameAsync(string name) => EnqueueAsync(() => SetNameCoreAsync(name));
    public Task SetReadyAsync(bool isReady) => EnqueueAsync(() => SetReadyCoreAsync(isReady));
    public Task SetSelectionAsync(IEnumerable<ProtocolCardInstanceId> selection) => EnqueueAsync(() => SetSelectionCoreAsync(selection));
    public Task ToggleSelectionAsync(ProtocolCardInstanceId instanceId) => EnqueueAsync(() => ToggleSelectionCoreAsync(instanceId));
    public Task SetPickLockedAsync(bool locked) => EnqueueAsync(() => SetPickLockedCoreAsync(locked));
    public Task ForceReadyAsync() => EnqueueAsync(ForceReadyCoreAsync);
    public Task KickAsync(ProtocolPlayerId playerId) => EnqueueAsync(() => KickCoreAsync(playerId));
    public Task ReopenLobbyAsync() => EnqueueAsync(ReopenLobbyCoreAsync);
    public Task JoinReopenedLobbyAsync(string name) => EnqueueAsync(() => JoinReopenedLobbyCoreAsync(name));
    public Task LeaveAsync() => EnqueueAsync(LeaveCoreAsync);
    public Task OpenSteamInviteOverlayAsync() => EnqueueAsync(() => SendAsync(new OpenLobbyInviteOverlayCommand()));

    public void ExportCardSet(Stream output)
    {
        if (_role != SessionRole.Host || _hostConfiguration is null) throw new InvalidOperationException("Only the host can export the active scanned card set.");
        new CockatriceXmlExporter().Export(output, _hostConfiguration.Definitions);
    }

    private async Task RunAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync())
        {
            try { await command(); }
            catch (Exception) { SetError(ErrorCode.InvalidActionForState); }
        }
    }

    private async Task PumpEventsAsync()
    {
        try
        {
            await foreach (var value in _transport.Events.ReadAllAsync(_stopping.Token))
                await EnqueueAsync(() => HandleTransportEventAsync(value));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private Task EnqueueAsync(Func<Task> operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_commands.Writer.TryWrite(async () =>
            {
                try { await operation(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
            })) completion.SetException(new ObjectDisposedException(nameof(SessionCoordinator)));
        return completion.Task;
    }

    private async Task HostCoreAsync(string playerName)
    {
        if (!CanStart() || !PlayerNameValidator.TryNormalize(playerName, [], out var normalized))
        { SetError(Snapshot.SteamStatus == SteamStatus.Ready ? ErrorCode.NameInvalid : ErrorCode.SteamUnavailable); return; }
        ResetSession();
        _role = SessionRole.Host; _pendingName = normalized; _roomCode = SteamRoomCode.Generate();
        ClearReconnectState();
        _options.Logger.Write("session.host_requested");
        new SessionCacheManager(_options.Paths.SessionsDirectory).DeleteAbandonedSessions();
        Publish(ApplicationScreen.Start, "Creating lobby...");
        await SendAsync(new CreateLobbyCommand(_roomCode));
    }

    private async Task JoinCoreAsync(string playerName, string roomCode)
    {
        if (!CanStart()) { SetError(ErrorCode.SteamUnavailable); return; }
        if (!PlayerNameValidator.TryNormalize(playerName, [], out var normalized)) { SetError(ErrorCode.NameInvalid); return; }
        if (!SteamRoomCode.TryNormalize(roomCode, out var code)) { SetError(ErrorCode.RoomCodeInvalid); return; }
        ResetSession();
        _role = SessionRole.Client; _pendingName = normalized; _roomCode = code;
        ClearReconnectState();
        _options.Logger.Write("session.room_join_requested");
        new SessionCacheManager(_options.Paths.SessionsDirectory).DeleteAbandonedSessions();
        Publish(ApplicationScreen.Start, "Searching for room...");
        await SendAsync(new FindLobbyCommand(code));
    }

    private async Task JoinSteamInviteCoreAsync(string playerName)
    {
        if (!CanStart() || _pendingInviteLobby is not { } lobby) { SetError(ErrorCode.InvalidActionForState); return; }
        if (!PlayerNameValidator.TryNormalize(playerName, [], out var normalized)) { SetError(ErrorCode.NameInvalid); return; }
        ResetSession();
        _role = SessionRole.Client; _pendingName = normalized;
        ClearReconnectState();
        _options.Logger.Write("session.invite_join_requested");
        new SessionCacheManager(_options.Paths.SessionsDirectory).DeleteAbandonedSessions();
        Publish(ApplicationScreen.Start, "Joining Steam friend...");
        await SendAsync(new JoinLobbyCommand(lobby));
    }

    private async Task ReconnectCoreAsync(ClientState state)
    {
        if (!CanStart() || state is not
            {
                PreviousSessionEndedNormally: false,
                LastHostSteamId: { } hostSteamId,
                LastApplicationSessionId: { } sessionId,
                LastAcceptedPlayerName: { } playerName,
            } || hostSteamId == 0 || sessionId == Guid.Empty ||
            !PlayerNameValidator.TryNormalize(playerName, [], out var normalized))
        {
            SetError(ErrorCode.SessionNoLongerAvailable);
            return;
        }

        ResetSession();
        _role = SessionRole.Client;
        _hostPeer = new SteamPeerId(hostSteamId);
        _lobbyId = state.LastSteamLobbyId is { } lobbyId ? new SteamLobbyId(lobbyId) : null;
        _sessionId = new ProtocolSessionId(sessionId);
        _roomCode = state.LastNormalizedRoomCode;
        _pendingName = normalized;
        _reconnecting = true;
        new SessionCacheManager(_options.Paths.SessionsDirectory).DeleteAbandonedSessions(sessionId);
        Publish(ApplicationScreen.Start, "Reconnecting to host...");
        await SendAsync(new ConnectPeerCommand(_hostPeer));
    }

    private Task ConfigureHostCoreAsync(HostDraftConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (_role != SessionRole.Host || _phase != ProtocolPhase.LobbyOpen || _players.Single(x => x.IsHost).IsReady)
            return Task.CompletedTask;
        var result = DraftSettingsValidator.Validate(configuration.Settings, _players.Count, configuration.Definitions,
            new DraftLimits(configuration.Limits.MaxPackSize, configuration.Limits.MaxDraftCardInstances));
        if (!result.IsValid) { _configurationInvalid = true; SetError(ErrorCode.DraftConfigurationInvalid); return Task.CompletedTask; }
        _hostConfiguration = new(configuration.Settings, configuration.Definitions.ToArray(), configuration.Limits);
        _configurationInvalid = false;
        SetError(null);
        return Task.CompletedTask;
    }

    private bool CanStart() => Snapshot.SteamStatus == SteamStatus.Ready && Snapshot.Screen == ApplicationScreen.Start;

    private async Task HandleTransportEventAsync(SteamTransportEvent value)
    {
        switch (value)
        {
            case SteamInitializedEvent e: _localPeer = e.LocalPeerId; _options.Logger.Write("session.steam_ready"); Publish(ApplicationScreen.Start, "Steam connected.", SteamStatus.Ready); break;
            case SteamInitializationFailedEvent: Publish(ApplicationScreen.Start, ErrorText(ErrorCode.SteamUnavailable), SteamStatus.Unavailable, ErrorCode.SteamUnavailable); break;
            case LobbyCreatedEvent e when _role == SessionRole.Host:
                _lobbyId = e.LobbyId; _roomCode = e.NormalizedRoomCode; _sessionId = new(_options.SessionIdFactory()); _localPlayerId = new(Guid.NewGuid());
                _players.Add(new(_localPlayerId.Value, _localPeer, _pendingName!, 0, true)); _phase = ProtocolPhase.LobbyOpen; _options.Logger.Write("session.lobby_created"); PublishLobby(); break;
            case LobbyCreationFailedEvent: SetError(ErrorCode.LobbyCreationFailed); break;
            case LobbySearchCompletedEvent e when _role == SessionRole.Client:
                if (e.LobbyIds.Count == 0) SetError(ErrorCode.LobbyNotFound); else await SendAsync(new JoinLobbyCommand(e.LobbyIds[0])); break;
            case LobbyEnteredEvent e when _role == SessionRole.Client:
                _lobbyId = e.LobbyId; _hostPeer = e.OwnerId;
                if (_pendingReopenName is not null)
                {
                    var reopenName = _pendingReopenName; _pendingReopenName = null;
                    await SendClientAsync(_hostPeer, ClientMessageCode.ReopenLobbyJoin, new ReopenLobbyJoinDto(reopenName));
                }
                else Publish(ApplicationScreen.Start, "Connecting to host...");
                break;
            case LobbyJoinFailedEvent: SetError(ErrorCode.RoomClosed); break;
            case ConnectionRequestedEvent e when _role == SessionRole.Host:
                if (e.IsLobbyMember || IsReconnectablePeer(e.PeerId))
                {
                    if (e.IsImmediateFriend || e.WasExplicitlyInvited) _roomCodeBypassPeers.Add(e.PeerId);
                    await SendAsync(new AcceptConnectionCommand(e.ConnectionId));
                }
                else await SendAsync(new RejectConnectionCommand(e.ConnectionId, "Not a lobby member.")); break;
            case PeerConnectedEvent e when _role == SessionRole.Client && e.PeerId == _hostPeer && !_helloSent:
                _helloSent = true; _phase = ProtocolPhase.Handshake;
                await SendClientAsync(_hostPeer, ClientMessageCode.Hello, new HelloDto(ProtocolConstants.Version,
                    _lobbyId is { } lobby ? new LobbyId(lobby.Value) : null, _roomCode,
                    _reconnecting ? _sessionId : null, _pendingName!)); break;
            case MessageReceivedEvent e: await HandleMessageAsync(e); break;
            case PeerDisconnectedEvent e: await HandleDisconnectAsync(e.PeerId); break;
            case HelloTimedOutEvent e when _role == SessionRole.Host: await RemovePeerAsync(e.PeerId, CountdownCancellationReason.PlayerDisconnected); break;
            case LobbyOpenChangedEvent e when _role == SessionRole.Host && !e.IsOpen && _waitingForLobbyClose:
                _waitingForLobbyClose = false; await BeginHostPreparationAsync(); break;
            case LobbyInvitationReceivedEvent e when Snapshot.Screen == ApplicationScreen.Start:
                _pendingInviteLobby = e.LobbyId; Publish(ApplicationScreen.Start, "Steam lobby invitation received. Join it to continue."); break;
            case LobbyJoinRequestedEvent e when Snapshot.Screen == ApplicationScreen.Start:
                _pendingInviteLobby = e.LobbyId; Publish(ApplicationScreen.Start, "Steam friend lobby selected. Join it to continue."); break;
            case TransportOperationFailedEvent e:
                if (_phase == ProtocolPhase.PreparingAssets) await RollbackPreparationAsync(null, ErrorCode.HostAssetPreparationFailed);
                else SetError(e.Operation == nameof(CreateLobbyCommand) ? ErrorCode.LobbyCreationFailed : ErrorCode.InvalidActionForState); break;
            case SteamShutdownEvent: ResetSession(); _options.Logger.Write("session.steam_shutdown"); Publish(ApplicationScreen.Start, ErrorText(ErrorCode.SteamUnavailable), SteamStatus.Unavailable, ErrorCode.SteamUnavailable); break;
        }
    }

    private async Task HandleMessageAsync(MessageReceivedEvent received)
    {
        if (_role == SessionRole.Client && received.Payload.Span.StartsWith("DSAS"u8))
        { ReceiveAssetChunk(received.Payload); return; }
        try
        {
            var maxBytes = _hostConfiguration?.Limits.ControlMessageMaxBytes ?? _options.ClientConfiguration.ControlMessageMaxBytes;
            if (_role == SessionRole.Host) await HandleClientMessageAsync(received, ControlMessageCodec.DeserializeClient(received.Payload.Span, maxBytes));
            else if (_role == SessionRole.Client && received.PeerId == _hostPeer) await HandleHostMessageAsync(ControlMessageCodec.DeserializeHost(received.Payload.Span, maxBytes));
        }
        catch (Exception exception) when (exception is ProtocolException or AssetTransferException)
        {
            if (_role == SessionRole.Host) { await SendErrorAsync(received.PeerId, ErrorCode.MalformedMessage, true); await SendAsync(new ClosePeerCommand(received.PeerId, "Malformed message.")); }
            else SetError(ErrorCode.MalformedMessage);
        }
    }

    private async Task HandleClientMessageAsync(MessageReceivedEvent received, ControlMessage message)
    {
        if (!_authenticatedPeers.Contains(received.PeerId))
        {
            if (message.Payload is HelloDto hello) await AuthenticateAsync(received, hello);
            else { await SendErrorAsync(received.PeerId, ErrorCode.InvalidActionForState, true); await SendAsync(new ClosePeerCommand(received.PeerId, "Hello required.")); }
            return;
        }
        try { MessagePhaseValidator.ValidateClient((ClientMessageCode)message.Code, _phase); }
        catch (ProtocolException) { await SendErrorAsync(received.PeerId, ErrorCode.InvalidActionForState, false); return; }
        if (_phase == ProtocolPhase.Complete &&
            (message.Code is (int)ClientMessageCode.AssetNeed or (int)ClientMessageCode.PreparationReady or (int)ClientMessageCode.PreparationFailed) &&
            !_players.Single(x => x.PeerId == received.PeerId).IsReconnectAssetSync)
        {
            await SendErrorAsync(received.PeerId, ErrorCode.InvalidActionForState, false);
            return;
        }
        switch (message.Payload)
        {
            case SetNameDto x: await SetRemoteNameAsync(received.PeerId, x.Name); break;
            case SetLobbyReadyDto x: await SetRemoteReadyAsync(received.PeerId, x.IsReady); break;
            case AssetNeedDto x: await ReceiveAssetNeedAsync(received.PeerId, x); break;
            case PreparationReadyDto x: await ReceivePreparationReadyAsync(received.PeerId, x); break;
            case PreparationFailedDto x: await ReceivePreparationFailedAsync(received.PeerId, x); break;
            case SetSelectionDto x: await ApplySelectionAsync(PlayerForPeer(received.PeerId).Id, x.PickRevision, x.InstanceIds); break;
            case SetPickLockedDto x: await ApplyLockAsync(PlayerForPeer(received.PeerId).Id, x.PickRevision, x.IsLocked); break;
            case ReopenLobbyJoinDto x: await AddCompletedPlayerAsync(received.PeerId, x.RequestedName); break;
        }
    }

    private async Task AuthenticateAsync(MessageReceivedEvent received, HelloDto hello)
    {
        if (hello.ReconnectSessionId is not null)
        {
            await AuthenticateReconnectAsync(received, hello);
            return;
        }
        var error = ValidateHello(received.PeerId, hello, out var name);
        if (error is not null) { await SendErrorAsync(received.PeerId, error.Value, true); await SendAsync(new ClosePeerCommand(received.PeerId, "Handshake rejected.")); return; }
        if (_phase == ProtocolPhase.StartingCountdown) await CancelCountdownAsync(CountdownCancellationReason.PlayerJoined);
        var player = new PlayerState(new(Guid.NewGuid()), received.PeerId, name, _players.Count, false);
        _players.Add(player); _authenticatedPeers.Add(received.PeerId); _lobbyRevision++;
        await RevalidateHostConfigurationAsync();
        await SendAsync(new MarkHelloReceivedCommand(received.ConnectionId));
        await SendHostAsync(received.PeerId, HostMessageCode.Welcome, new WelcomeDto(player.Id, _sessionId, ReconnectResult.NewSession));
        await BroadcastLobbyAsync(); PublishLobby();
    }

    private async Task AuthenticateReconnectAsync(MessageReceivedEvent received, HelloDto hello)
    {
        var player = _players.FirstOrDefault(x => x.PeerId == received.PeerId);
        if (hello.ProtocolVersion != ProtocolConstants.Version || hello.ReconnectSessionId != _sessionId ||
            _phase is not (ProtocolPhase.Drafting or ProtocolPhase.Complete) || player is null || player.IsConnected ||
            _preparedAssets is null || _draft is null)
        {
            await SendErrorAsync(received.PeerId, ErrorCode.SessionNoLongerAvailable, true);
            await SendAsync(new ClosePeerCommand(received.PeerId, "Reconnect rejected."));
            return;
        }

        _authenticatedPeers.Add(received.PeerId);
        player.PreparationStatus = PreparationStatus.Waiting;
        player.SubmittedAssetNeed = false;
        player.IsReconnectAssetSync = true;
        _assetNeedPages[received.PeerId] = new();
        await SendAsync(new MarkHelloReceivedCommand(received.ConnectionId));
        await SendHostAsync(received.PeerId, HostMessageCode.Welcome, new WelcomeDto(player.Id, _sessionId, ReconnectResult.Reconnected));
        foreach (var page in AssetManifestPager.CreatePages(_preparedAssets.Manifest, ++_manifestRevision))
            await SendHostAsync(received.PeerId, HostMessageCode.AssetManifest, page);
    }

    private bool IsReconnectablePeer(SteamPeerId peerId) =>
        _phase is ProtocolPhase.Drafting or ProtocolPhase.Complete &&
        _players.Any(x => !x.IsHost && x.PeerId == peerId && !x.IsConnected);

    private ErrorCode? ValidateHello(SteamPeerId peerId, HelloDto hello, out string name)
    {
        name = string.Empty;
        if (hello.ProtocolVersion != ProtocolConstants.Version) return ErrorCode.ProtocolVersionMismatch;
        if (_phase is not (ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown) || _waitingForLobbyClose) return ErrorCode.RoomClosed;
        if (hello.LobbyId?.Value != _lobbyId?.Value) return ErrorCode.RoomCodeInvalid;
        if (!_roomCodeBypassPeers.Contains(peerId) && (hello.RoomCode is null || !SteamRoomCode.TryNormalize(hello.RoomCode, out var supplied) || !SteamRoomCode.HashMatches(supplied, SteamRoomCode.Hash(_roomCode!)))) return ErrorCode.RoomCodeInvalid;
        if (_players.Count >= SteamLobbyMetadata.MemberLimit) return ErrorCode.LobbyFull;
        return ValidateName(hello.RequestedName, null, out name);
    }

    private ErrorCode? ValidateName(string? requested, PlayerState? current, out string normalized)
    {
        normalized = requested?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 16 || normalized.Any(c => c is < ' ' or > '~')) return ErrorCode.NameInvalid;
        var candidate = normalized;
        return _players.Any(x => x != current && StringComparer.OrdinalIgnoreCase.Equals(x.Name, candidate)) ? ErrorCode.NameDuplicate : null;
    }

    private async Task HandleHostMessageAsync(ControlMessage message)
    {
        var code = (HostMessageCode)message.Code;
        if (!IsExpectedHostMessage(code)) { SetError(ErrorCode.InvalidActionForState); return; }
        switch (message.Payload)
        {
            case WelcomeDto x:
                _localPlayerId = x.PlayerId; _sessionId = x.SessionId;
                PersistActiveClientState();
                if (x.ReconnectResult == ReconnectResult.Reconnected)
                {
                    _reconnecting = true;
                    BeginClientPreparationAttempt();
                    _phase = ProtocolPhase.PreparingAssets;
                    Publish(ApplicationScreen.PreparingSession, "Checking session assets...");
                }
                else
                {
                    _reconnecting = false;
                    _phase = ProtocolPhase.LobbyOpen;
                }
                break;
            case ErrorDto x:
                if (_phase == ProtocolPhase.PreparingAssets && IsPreparationError(x.ErrorCode))
                {
                    _phase = ProtocolPhase.LobbyOpen; _preparationGeneration++; CancelPreparationWork(); ResetClientPreparationState();
                }
                if (x.Fatal && x.ErrorCode == ErrorCode.SessionNoLongerAvailable)
                {
                    ClearReconnectState();
                    ResetSession();
                    Publish(ApplicationScreen.Start, ErrorText(x.ErrorCode), error: x.ErrorCode);
                }
                else SetError(x.ErrorCode);
                if (x.Fatal && _lobbyId is not null) await SendAsync(new LeaveLobbyCommand());
                break;
            case LobbySnapshotDto x: _lobbyRevision = x.Page.Revision; _localPlayerId = x.RecipientPlayerId; PublishFromLobbyDto(x); break;
            case CountdownStartedDto x:
                _countdownGeneration = x.Generation;
                _countdownRemainingSeconds = CountdownSeconds(TimeSpan.FromMilliseconds(x.DurationMilliseconds));
                _phase = ProtocolPhase.StartingCountdown;
                BeginClientPreparationAttempt();
                Publish(ApplicationScreen.StartingCountdown, CountdownStatus(_countdownRemainingSeconds));
                _ = ClientCountdownAsync(x.Generation, TimeSpan.FromMilliseconds(x.DurationMilliseconds));
                break;
            case CountdownCancelledDto x when x.Generation == _countdownGeneration:
                _phase = ProtocolPhase.LobbyOpen;
                _countdownRemainingSeconds = 0;
                Publish(ApplicationScreen.Lobby, "Countdown cancelled.");
                break;
            case AssetManifestDto x: await ReceiveManifestPageAsync(x); break;
            case PreparationSnapshotDto x: PublishPreparationDto(x); break;
            case DraftSnapshotDto x:
                var draft = _draftPages.Add(x); if (draft is not null) { _phase = ProtocolPhase.Drafting; PublishDraftDto(draft); } break;
            case DraftCompletedDto x:
                var cards = _completionPages.Add(x); if (cards is not null) { _clientCompletion = cards; _phase = ProtocolPhase.Complete; PublishCompletion(cards); await SendAsync(new LeaveLobbyCommand()); } break;
            case LobbyAvailabilityDto x: SetSnapshot(Snapshot with { LobbyAvailable = x.IsOpen }); break;
            case KickedDto: SetError(ErrorCode.Kicked); await SendAsync(new LeaveLobbyCommand()); break;
            case HostClosedDto:
                if (_lobbyId is not null) await SendAsync(new LeaveLobbyCommand());
                ResetSession();
                Publish(ApplicationScreen.Start, ErrorText(ErrorCode.HostDisconnected), error: ErrorCode.HostDisconnected);
                break;
        }
    }

    private async Task BeginHostPreparationAsync()
    {
        if (!ValidateHostConfiguration()) { await RollbackPreparationAsync(null, ErrorCode.DraftConfigurationInvalid); return; }
        CancelPreparationWork();
        var generation = ++_preparationGeneration;
        _preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        var cancellationToken = _preparationCancellation.Token;
        _phase = ProtocolPhase.PreparingAssets;
        _options.Logger.Write("session.preparation_started");
        foreach (var player in _players) { player.PreparationStatus = player.IsHost ? PreparationStatus.Transferring : PreparationStatus.Waiting; player.SubmittedAssetNeed = false; }
        PublishPreparation();
        try
        {
            var configuration = _hostConfiguration!;
            var random = _options.RandomSourceFactory();
            _draftRandom = random.Source;
            _options.Logger.Write("session.reproduction_seed", random.Seed);
            var players = _players.Select(x => new DraftPlayer(new CorePlayerId(x.Id.Value), x.PeerId.Value, x.Name, x.IsConnected)).ToArray();
            _generatedDraft = new PackGenerator(_draftRandom).Generate(configuration.Settings, players, configuration.Definitions,
                new DraftLimits(configuration.Limits.MaxPackSize, configuration.Limits.MaxDraftCardInstances));
            var usedIds = _generatedDraft.Instances.Values.Select(x => x.DefinitionId).ToHashSet();
            var used = configuration.Definitions.Where(x => usedIds.Contains(x.Id)).ToArray();
            _definitionNames = configuration.Definitions.ToDictionary(x => x.Id, x => ExportNameSanitizer.Sanitize(x.ExportName));
            var sessionDirectory = _options.Paths.GetSessionDirectory(_sessionId.Value);
            _ = Task.Run(() => _options.AssetPreparer.Prepare(_sessionId.Value, used, sessionDirectory, configuration.Limits, cancellationToken), cancellationToken)
                .ContinueWith(async task => await EnqueueAsync(() => task.IsCompletedSuccessfully
                    ? HostPreparationCompletedAsync(generation, task.Result)
                    : task.IsCanceled || generation != _preparationGeneration
                        ? Task.CompletedTask
                        : RollbackPreparationAsync(null, ErrorCode.HostAssetPreparationFailed)), TaskScheduler.Default).Unwrap();
        }
        catch
        {
            await RollbackPreparationAsync(null, ErrorCode.HostAssetPreparationFailed);
        }
    }

    private async Task HostPreparationCompletedAsync(long generation, AssetPreparationResult result)
    {
        if (_phase != ProtocolPhase.PreparingAssets || generation != _preparationGeneration) return;
        _preparedAssets = result;
        _definitionAssets = result.Definitions.ToDictionary(x => x.DefinitionId, x => x.AssetHash);
        var host = _players.Single(x => x.IsHost); host.PreparationStatus = PreparationStatus.Ready;
        foreach (var player in _players.Where(x => !x.IsHost))
        {
            _assetNeedPages[player.PeerId] = new();
            foreach (var page in AssetManifestPager.CreatePages(result.Manifest, ++_manifestRevision))
                await SendHostAsync(player.PeerId, HostMessageCode.AssetManifest, page);
        }
        await BroadcastPreparationAsync(); PublishPreparation();
        await TryStartDraftAsync();
    }

    private Task ReceiveManifestPageAsync(AssetManifestDto page)
    {
        var result = _manifestPages.Add(page);
        if (!result.IsComplete) return Task.CompletedTask;
        var manifest = result.Manifest!;
        _phase = ProtocolPhase.PreparingAssets;
        Publish(ApplicationScreen.PreparingSession, "Checking session assets...");
        var generation = _preparationGeneration;
        var sessionId = manifest.SessionId;
        _ = Task.Run(() =>
            {
                var engine = new ClientAssetTransferEngine(manifest, _options.Paths.GetSessionDirectory(manifest.SessionId));
                return (Engine: engine, Plan: engine.Prepare(result.Revision));
            }).ContinueWith(async task => await EnqueueAsync(async () =>
            {
                if (generation != _preparationGeneration || _sessionId.Value != sessionId || _phase != ProtocolPhase.PreparingAssets) return;
                if (!task.IsCompletedSuccessfully)
                { await ReportPreparationFailureAsync(task.Exception?.GetBaseException() is InsufficientAssetDiskSpaceException ? ErrorCode.ClientDiskSpaceInsufficient : ErrorCode.ClientAssetVerificationFailed); return; }
                _clientTransfer = task.Result.Engine; _clientProgress = task.Result.Plan.Progress; PublishClientPreparation();
                foreach (var need in task.Result.Plan.NeedPages) await SendClientAsync(_hostPeer, ClientMessageCode.AssetNeed, need);
                if (_clientProgress.IsComplete) await SendClientAsync(_hostPeer, ClientMessageCode.PreparationReady, new PreparationReadyDto(_sessionId));
                else ScheduleTransferStallTimeout();
            }), TaskScheduler.Default).Unwrap();
        return Task.CompletedTask;
    }

    private void ReceiveAssetChunk(ReadOnlyMemory<byte> payload)
    {
        if (_clientTransfer is null) { _ = EnqueueAsync(() => ReportPreparationFailureAsync(ErrorCode.ClientAssetVerificationFailed)); return; }
        var transfer = _clientTransfer;
        var generation = _preparationGeneration;
        _ = Task.Run(() => transfer.Receive(payload.Span)).ContinueWith(async task => await EnqueueAsync(async () =>
        {
            if (generation != _preparationGeneration || !ReferenceEquals(transfer, _clientTransfer) || _phase != ProtocolPhase.PreparingAssets) return;
            if (!task.IsCompletedSuccessfully) { await ReportPreparationFailureAsync(ErrorCode.ClientAssetVerificationFailed); return; }
            _clientProgress = task.Result.Progress; PublishClientPreparation();
            if (_clientProgress.IsComplete) await SendClientAsync(_hostPeer, ClientMessageCode.PreparationReady, new PreparationReadyDto(_sessionId));
            else ScheduleTransferStallTimeout();
        }), TaskScheduler.Default).Unwrap();
    }

    private void ScheduleTransferStallTimeout()
    {
        var generation = ++_transferProgressGeneration;
        _ = Task.Delay(TimeSpan.FromSeconds(_options.ClientConfiguration.TransferStallSeconds), _stopping.Token).ContinueWith(async task =>
        {
            if (task.IsCanceled) return;
            await EnqueueAsync(async () =>
            {
                if (_role == SessionRole.Client && _phase == ProtocolPhase.PreparingAssets && !_clientProgress.IsComplete && generation == _transferProgressGeneration)
                    await ReportPreparationFailureAsync(ErrorCode.ClientAssetTransferStalled);
            });
        }, TaskScheduler.Default).Unwrap();
    }

    private async Task ReceiveAssetNeedAsync(SteamPeerId peerId, AssetNeedDto page)
    {
        if (_preparedAssets is null || page.SessionId != _sessionId) { await ReceivePreparationFailedAsync(peerId, new(page.SessionId, ErrorCode.ClientAssetVerificationFailed)); return; }
        var player = PlayerForPeer(peerId);
        if (_phase is ProtocolPhase.Drafting or ProtocolPhase.Complete && !player.IsReconnectAssetSync)
        { await SendErrorAsync(peerId, ErrorCode.InvalidActionForState, false); return; }
        var requested = _assetNeedPages.GetValueOrDefault(peerId)?.Add(page);
        if (requested is null) return;
        player.SubmittedAssetNeed = true; player.PreparationStatus = requested.Length == 0 ? PreparationStatus.Waiting : PreparationStatus.Transferring;
        if (_phase == ProtocolPhase.PreparingAssets) { await BroadcastPreparationAsync(); PublishPreparation(); }
        var hashes = requested.Select(x => new CoreAssetHash(x.Value)).ToArray();
        var generation = _preparationGeneration;
        var preparedAssets = _preparedAssets;
        var sessionId = _sessionId;
        var chunkBytes = preparedAssets.Manifest.TransferChunkBytes;
        _ = Task.Run(async () =>
        {
            try
            {
                var source = new HostAssetChunkSource();
                foreach (var frame in source.EnumerateFrames(preparedAssets.Manifest, _options.Paths.GetSessionDirectory(sessionId.Value), hashes, chunkBytes))
                {
                    if (generation != Volatile.Read(ref _preparationGeneration)) return;
                    await _transport.Commands.WriteAsync(new SendMessageCommand(peerId, frame), _stopping.Token);
                }
            }
            catch
            {
                await EnqueueAsync(() => generation != _preparationGeneration
                    ? Task.CompletedTask
                    : player.IsReconnectAssetSync && (_phase is ProtocolPhase.Drafting or ProtocolPhase.Complete)
                        ? FailReconnectAsync(player, ErrorCode.ClientAssetVerificationFailed)
                        : RollbackPreparationAsync(player, ErrorCode.HostAssetPreparationFailed));
            }
        });
    }

    private async Task ReceivePreparationReadyAsync(SteamPeerId peerId, PreparationReadyDto ready)
    {
        if (ready.SessionId != _sessionId) { await SendErrorAsync(peerId, ErrorCode.InvalidActionForState, false); return; }
        var player = PlayerForPeer(peerId);
        if (!player.SubmittedAssetNeed) { await SendErrorAsync(peerId, ErrorCode.InvalidActionForState, false); return; }
        if (_phase is ProtocolPhase.Drafting or ProtocolPhase.Complete && !player.IsReconnectAssetSync)
        { await SendErrorAsync(peerId, ErrorCode.InvalidActionForState, false); return; }
        player.PreparationStatus = PreparationStatus.Ready;
        if (_phase is ProtocolPhase.Drafting or ProtocolPhase.Complete)
        {
            var result = _draft!.Reconnect(new CorePlayerId(player.Id.Value), peerId.Value);
            if (!result.Succeeded) { await SendErrorAsync(peerId, ErrorCode.SessionNoLongerAvailable, true); return; }
            player.IsConnected = true;
            player.IsReconnectAssetSync = false;
            if (_phase == ProtocolPhase.Drafting)
                await BroadcastDraftAsync();
            else
                await SendCompletionAsync(player);
            PublishHostDraftOrCompletion();
            return;
        }
        await BroadcastPreparationAsync(); PublishPreparation(); await TryStartDraftAsync();
    }

    private async Task ReceivePreparationFailedAsync(SteamPeerId peerId, PreparationFailedDto failed)
    {
        if (failed.SessionId != _sessionId || !IsPreparationError(failed.ErrorCode)) { await SendErrorAsync(peerId, ErrorCode.InvalidActionForState, false); return; }
        if (_phase is ProtocolPhase.Drafting or ProtocolPhase.Complete)
        {
            var player = PlayerForPeer(peerId);
            if (!player.IsReconnectAssetSync) { await SendErrorAsync(peerId, ErrorCode.InvalidActionForState, false); return; }
            await FailReconnectAsync(player, failed.ErrorCode);
            return;
        }
        await RollbackPreparationAsync(PlayerForPeer(peerId), failed.ErrorCode);
    }

    private async Task FailReconnectAsync(PlayerState player, ErrorCode error)
    {
        player.PreparationStatus = PreparationStatus.Failed;
        player.IsReconnectAssetSync = false;
        _authenticatedPeers.Remove(player.PeerId);
        await SendErrorAsync(player.PeerId, error, true);
        await SendAsync(new ClosePeerCommand(player.PeerId, "Reconnect asset verification failed."));
    }

    private Task ReportPreparationFailureAsync(ErrorCode code) => SendClientAsync(_hostPeer, ClientMessageCode.PreparationFailed, new PreparationFailedDto(_sessionId, code));

    private async Task RollbackPreparationAsync(PlayerState? failingPlayer, ErrorCode code)
    {
        if (_role != SessionRole.Host || _phase != ProtocolPhase.PreparingAssets) return;
        CancelPreparationWork();
        _preparationGeneration++;
        (failingPlayer ?? _players.Single(x => x.IsHost)).IsReady = false;
        foreach (var player in _players) player.PreparationStatus = player == failingPlayer ? PreparationStatus.Failed : PreparationStatus.Waiting;
        _phase = ProtocolPhase.LobbyOpen; ResetRoundState(); _lobbyRevision++;
        await SendAsync(new SetLobbyOpenCommand(true));
        await BroadcastHostAsync(HostMessageCode.Error, new ErrorDto(code, false));
        await BroadcastLobbyAsync(); Publish(ApplicationScreen.Lobby, failingPlayer is null ? ErrorText(code) : $"{failingPlayer.Name}: {ErrorText(code)}", error: code);
    }

    private async Task TryStartDraftAsync()
    {
        if (_role != SessionRole.Host || _phase != ProtocolPhase.PreparingAssets || _preparedAssets is null || _players.Any(x => x.PreparationStatus != PreparationStatus.Ready)) return;
        _draft = new DraftStateMachine(_hostConfiguration!.Settings, _generatedDraft!, new CorePlayerId(_players.Single(x => x.IsHost).Id.Value), _draftRandom!);
        _phase = ProtocolPhase.Drafting;
        _options.Logger.Write("session.draft_started");
        await BroadcastDraftAsync(); PublishHostDraft();
        if (_draft.Phase == DraftPhase.Complete) await CompleteDraftAsync();
    }

    private async Task SetSelectionCoreAsync(IEnumerable<ProtocolCardInstanceId> selection)
    {
        var values = selection.ToArray();
        if (_phase != ProtocolPhase.Drafting || _localPlayerId is null) return;
        if (_role == SessionRole.Client)
        {
            SetSnapshot(Snapshot with
            {
                SelectedInstanceIds = values,
                CanLock = values.Length == Snapshot.RequiredSelectionCount,
            });
            await SendClientAsync(_hostPeer, ClientMessageCode.SetSelection, new SetSelectionDto(Snapshot.PickRevision, values));
        }
        else await ApplySelectionAsync(_localPlayerId.Value, Snapshot.PickRevision, values);
    }

    private Task ToggleSelectionCoreAsync(ProtocolCardInstanceId instanceId)
    {
        var selection = Snapshot.SelectedInstanceIds.ToHashSet();
        if (!selection.Add(instanceId)) selection.Remove(instanceId);
        return SetSelectionCoreAsync(selection);
    }

    private async Task SetPickLockedCoreAsync(bool locked)
    {
        if (_phase != ProtocolPhase.Drafting || _localPlayerId is null) return;
        if (_role == SessionRole.Client) await SendClientAsync(_hostPeer, ClientMessageCode.SetPickLocked, new SetPickLockedDto(Snapshot.PickRevision, locked));
        else await ApplyLockAsync(_localPlayerId.Value, Snapshot.PickRevision, locked);
    }

    private async Task ApplySelectionAsync(ProtocolPlayerId playerId, long revision, IEnumerable<ProtocolCardInstanceId> selection)
    {
        var result = _draft!.SetSelection(new CorePlayerId(playerId.Value), revision, selection.Select(x => new CoreCardInstanceId(x.Value)));
        if (!result.Succeeded) { await SendDraftErrorAsync(playerId, result.Error); return; }
        await BroadcastDraftAsync(); PublishHostDraft();
    }

    private async Task ApplyLockAsync(ProtocolPlayerId playerId, long revision, bool locked)
    {
        var result = _draft!.SetPickLocked(new CorePlayerId(playerId.Value), revision, locked);
        if (!result.Succeeded) { await SendDraftErrorAsync(playerId, result.Error); return; }
        if (_draft.Phase == DraftPhase.Complete) { await CompleteDraftAsync(); return; }
        await BroadcastDraftAsync(); PublishHostDraft();
    }

    private async Task ForceReadyCoreAsync()
    {
        if (_role != SessionRole.Host || _draft is null || _localPlayerId is null) return;
        var result = _draft.ForceReady(new CorePlayerId(_localPlayerId.Value.Value));
        if (!result.Succeeded) { SetError(ErrorCode.InvalidActionForState); return; }
        if (_draft.Phase == DraftPhase.Complete) await CompleteDraftAsync(); else { await BroadcastDraftAsync(); PublishHostDraft(); }
    }

    private async Task SendDraftErrorAsync(ProtocolPlayerId playerId, DraftActionError error)
    {
        var code = error == DraftActionError.StaleRevision ? ErrorCode.StaleRevision : ErrorCode.InvalidActionForState;
        var player = _players.Single(x => x.Id == playerId);
        if (player.IsHost) SetError(code);
        else
        {
            await SendErrorAsync(player.PeerId, code, false);
            foreach (var page in CreateDraftPages(player.Id))
                await SendHostAsync(player.PeerId, HostMessageCode.DraftSnapshot, page);
        }
    }

    private async Task BroadcastDraftAsync()
    {
        foreach (var player in _players.Where(x => !x.IsHost && x.IsConnected))
            foreach (var page in CreateDraftPages(player.Id)) await SendHostAsync(player.PeerId, HostMessageCode.DraftSnapshot, page);
    }

    private IReadOnlyList<DraftSnapshotDto> CreateDraftPages(ProtocolPlayerId recipient)
    {
        var dto = CreateDraftDto(recipient, new PageMetadata(new(Guid.NewGuid()), _draft!.DraftRevision, 0, 1));
        var groups = new List<(CardAssetDto[] Pack, ProtocolCardInstanceId[] Selected, CardAssetDto[] Collection, DraftPlayerDto[] Players)>();
        groups.AddRange(dto.CurrentPack.Chunk(ProtocolConstants.MaxPageEntries).Select(x => (x, Array.Empty<ProtocolCardInstanceId>(), Array.Empty<CardAssetDto>(), Array.Empty<DraftPlayerDto>())));
        groups.AddRange(dto.SelectedInstanceIds.Chunk(ProtocolConstants.MaxPageEntries).Select(x => (Array.Empty<CardAssetDto>(), x, Array.Empty<CardAssetDto>(), Array.Empty<DraftPlayerDto>())));
        groups.AddRange(dto.Collection.Chunk(ProtocolConstants.MaxPageEntries).Select(x => (Array.Empty<CardAssetDto>(), Array.Empty<ProtocolCardInstanceId>(), x, Array.Empty<DraftPlayerDto>())));
        groups.AddRange(dto.Players.Chunk(ProtocolConstants.MaxPageEntries).Select(x => (Array.Empty<CardAssetDto>(), Array.Empty<ProtocolCardInstanceId>(), Array.Empty<CardAssetDto>(), x)));
        if (groups.Count == 0) groups.Add(([], [], [], []));
        var id = new SnapshotId(Guid.NewGuid());
        return groups.Select((x, index) => dto with
        {
            Page = new(id, _draft.DraftRevision, index, groups.Count), CurrentPack = x.Pack,
            SelectedInstanceIds = x.Selected, Collection = x.Collection, Players = x.Players,
        }).ToArray();
    }

    private DraftSnapshotDto CreateDraftDto(ProtocolPlayerId recipient, PageMetadata page)
    {
        var coreId = new CorePlayerId(recipient.Value);
        var player = _draft!.Players.Single(x => x.Id == coreId);
        var pack = player.CurrentPackId is null ? [] : _draft.Packs[player.CurrentPackId.Value].Cards.Select(CardAsset).ToArray();
        var selected = player.Selection.Select(x => new ProtocolCardInstanceId(x.Value)).ToArray();
        var collection = player.Collection.Select(CardAsset).ToArray();
        var players = _draft.Players.Select(x => new DraftPlayerDto(new(x.Id.Value), x.Name, x.Order,
            x.IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected, x.IsDraftLocked,
            x.CurrentPackId is null ? 0 : _draft.Packs[x.CurrentPackId.Value].Cards.Count)).ToArray();
        return new(page, _draft.PickRevision, pack, selected, collection, _hostConfiguration!.Settings.CardsPerPick,
            !player.IsDraftLocked && selected.Length == _hostConfiguration.Settings.CardsPerPick,
            player.IsDraftLocked && _draft.Players.Any(x => !x.IsDraftLocked), players,
            _draft.ActiveDirection == 1 ? DraftDirection.Clockwise : DraftDirection.Counterclockwise, _draft.PackRound, _draft.TotalPackRounds);
    }

    private CardAssetDto CardAsset(CoreCardInstanceId id)
    {
        var instance = _draft!.Instances[id];
        return new(new(id.Value), new(_definitionAssets[instance.DefinitionId].Value));
    }

    private async Task CompleteDraftAsync()
    {
        _phase = ProtocolPhase.Complete;
        _options.Logger.Write("session.draft_completed");
        foreach (var player in _players.Where(x => !x.IsHost && x.IsConnected))
        {
            var cards = CompletedCards(player.Id);
            var chunks = cards.Chunk(ProtocolConstants.MaxPageEntries).ToArray();
            if (chunks.Length == 0) chunks = [[]];
            var id = new SnapshotId(Guid.NewGuid());
            for (var index = 0; index < chunks.Length; index++)
                await SendHostAsync(player.PeerId, HostMessageCode.DraftCompleted, new DraftCompletedDto(new(id, _draft!.DraftRevision, index, chunks.Length), chunks[index]));
        }
        PublishCompletion(CompletedCards(_localPlayerId!.Value));
    }

    private async Task SendCompletionAsync(PlayerState player)
    {
        var cards = CompletedCards(player.Id);
        var chunks = cards.Chunk(ProtocolConstants.MaxPageEntries).ToArray();
        if (chunks.Length == 0) chunks = [[]];
        var id = new SnapshotId(Guid.NewGuid());
        for (var index = 0; index < chunks.Length; index++)
            await SendHostAsync(player.PeerId, HostMessageCode.DraftCompleted,
                new DraftCompletedDto(new(id, _draft!.DraftRevision, index, chunks.Length), chunks[index]));
    }

    private CompletedCardDto[] CompletedCards(ProtocolPlayerId recipient)
    {
        var player = _draft!.Players.Single(x => x.Id.Value == recipient.Value);
        return player.Collection.Select(id =>
        {
            var instance = _draft.Instances[id];
            return new CompletedCardDto(new(id.Value), new(_definitionAssets[instance.DefinitionId].Value), _definitionNames[instance.DefinitionId]);
        }).ToArray();
    }

    private async Task ReopenLobbyCoreAsync()
    {
        if (_role != SessionRole.Host || _phase != ProtocolPhase.Complete) return;
        if (_players.Any(x => x.IsReconnectAssetSync)) { SetError(ErrorCode.InvalidActionForState); return; }
        foreach (var player in _players.Where(x => !x.IsHost)) { _completedPlayers[player.PeerId] = player; await SendHostAsync(player.PeerId, HostMessageCode.LobbyAvailability, new LobbyAvailabilityDto(true)); }
        var host = _players.Single(x => x.IsHost); host.IsReady = false; host.Order = 0; host.PreparationStatus = PreparationStatus.Waiting;
        _players.Clear(); _players.Add(host); ResetRoundState(); _phase = ProtocolPhase.LobbyOpen; _lobbyRevision++;
        await SendAsync(new SetLobbyOpenCommand(true));
        new SessionCacheManager(_options.Paths.SessionsDirectory).DeleteCompletedSession(_sessionId.Value);
        PublishLobby();
    }

    private async Task JoinReopenedLobbyCoreAsync(string name)
    {
        if (_role == SessionRole.Client && Snapshot.LobbyAvailable)
        {
            if (!PlayerNameValidator.TryNormalize(name, [], out var normalized)) { SetError(ErrorCode.NameInvalid); return; }
            _pendingReopenName = normalized;
            await SendAsync(new JoinLobbyCommand(_lobbyId!.Value));
        }
    }

    private async Task AddCompletedPlayerAsync(SteamPeerId peerId, string requestedName)
    {
        if (_role != SessionRole.Host || _phase != ProtocolPhase.LobbyOpen || !_completedPlayers.Remove(peerId, out var old)) return;
        var error = ValidateName(requestedName, null, out var name);
        if (error is not null) { await SendErrorAsync(peerId, error.Value, false); return; }
        var player = new PlayerState(old.Id, peerId, name, _players.Count, false);
        _players.Add(player); _lobbyRevision++; await BroadcastLobbyAsync(); PublishLobby();
    }

    private async Task SetNameCoreAsync(string name)
    {
        if (_phase is not (ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown)) return;
        if (_role == SessionRole.Client) { await SendClientAsync(_hostPeer, ClientMessageCode.SetName, new SetNameDto(name)); return; }
        var host = _players.Single(x => x.IsHost); var error = ValidateName(name, host, out var normalized);
        if (error is not null) { SetError(error.Value); return; }
        host.Name = normalized; _lobbyRevision++; await BroadcastLobbyAsync(); PublishLobby();
    }

    private async Task SetRemoteNameAsync(SteamPeerId peerId, string name)
    {
        var player = PlayerForPeer(peerId); var error = ValidateName(name, player, out var normalized);
        if (error is not null) { await SendErrorAsync(peerId, error.Value, false); return; }
        player.Name = normalized; _lobbyRevision++; await BroadcastLobbyAsync(); PublishLobby();
    }

    private async Task SetReadyCoreAsync(bool ready)
    {
        if (_phase is not (ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown)) return;
        if (_role == SessionRole.Client) { await SendClientAsync(_hostPeer, ClientMessageCode.SetLobbyReady, new SetLobbyReadyDto(ready)); return; }
        var host = _players.Single(x => x.IsHost);
        if (ready && !ValidateHostConfiguration()) { _configurationInvalid = true; SetError(ErrorCode.DraftConfigurationInvalid); return; }
        if (host.IsReady == ready) return; host.IsReady = ready; _lobbyRevision++;
        await ReadyStateChangedAsync(ready ? null : CountdownCancellationReason.PlayerUnready);
    }

    private async Task SetRemoteReadyAsync(SteamPeerId peerId, bool ready)
    {
        var player = PlayerForPeer(peerId); if (player.IsReady == ready) return;
        player.IsReady = ready; _lobbyRevision++; await ReadyStateChangedAsync(ready ? null : CountdownCancellationReason.PlayerUnready);
    }

    private async Task ReadyStateChangedAsync(CountdownCancellationReason? reason)
    {
        if (_players.Count != 0 && _players.All(x => x.IsReady)) { if (_phase == ProtocolPhase.LobbyOpen) await StartCountdownAsync(); }
        else if (_phase == ProtocolPhase.StartingCountdown) await CancelCountdownAsync(reason ?? CountdownCancellationReason.PlayerUnready);
        await BroadcastLobbyAsync(); PublishLobby();
    }

    private async Task StartCountdownAsync()
    {
        if (!ValidateHostConfiguration())
        {
            _configurationInvalid = true;
            _players.Single(x => x.IsHost).IsReady = false;
            SetError(ErrorCode.DraftConfigurationInvalid);
            return;
        }
        _phase = ProtocolPhase.StartingCountdown; var generation = ++_countdownGeneration;
        _countdownRemainingSeconds = CountdownSeconds(_options.CountdownDuration);
        await BroadcastHostAsync(HostMessageCode.CountdownStarted, new CountdownStartedDto((int)_options.CountdownDuration.TotalMilliseconds, generation));
        PublishLobby(); _ = CountdownAsync(generation);
    }

    private Task CountdownAsync(long generation) => RunCountdownClockAsync(generation, _options.CountdownDuration, isHost: true);

    private Task ClientCountdownAsync(long generation, TimeSpan duration) => RunCountdownClockAsync(generation, duration, isHost: false);

    private async Task RunCountdownClockAsync(long generation, TimeSpan duration, bool isHost)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var remaining = duration - stopwatch.Elapsed;
                var seconds = CountdownSeconds(remaining);
                var isActive = false;
                await EnqueueAsync(() =>
                {
                    if (_phase == ProtocolPhase.StartingCountdown && generation == _countdownGeneration)
                    {
                        isActive = true;
                        if (seconds > 0 && _countdownRemainingSeconds != seconds)
                        {
                            _countdownRemainingSeconds = seconds;
                            if (isHost) PublishLobby();
                            else Publish(ApplicationScreen.StartingCountdown, CountdownStatus(seconds));
                        }
                    }
                    return Task.CompletedTask;
                });
                if (!isActive) return;
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), _stopping.Token);
            }

            await EnqueueAsync(() => isHost ? CompleteCountdownAsync(generation) : FinishClientCountdownAsync(generation));
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
    }

    private Task FinishClientCountdownAsync(long generation)
    {
        if (_role == SessionRole.Client && _phase == ProtocolPhase.StartingCountdown && generation == _countdownGeneration)
        {
            _phase = ProtocolPhase.PreparingAssets;
            _countdownRemainingSeconds = 0;
            Publish(ApplicationScreen.PreparingSession, "Waiting for host preparation...");
        }
        return Task.CompletedTask;
    }

    private static int CountdownSeconds(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? 0 : Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));

    private static string CountdownStatus(int seconds) => $"Game starts in {seconds}...";

    private async Task CancelCountdownAsync(CountdownCancellationReason reason)
    {
        if (_phase != ProtocolPhase.StartingCountdown) return; _phase = ProtocolPhase.LobbyOpen; _countdownRemainingSeconds = 0;
        await BroadcastHostAsync(HostMessageCode.CountdownCancelled, new CountdownCancelledDto(_countdownGeneration, reason));
    }

    private async Task CompleteCountdownAsync(long generation)
    {
        if (_phase != ProtocolPhase.StartingCountdown || generation != _countdownGeneration || !_players.All(x => x.IsReady)) return;
        if (!ValidateHostConfiguration())
        {
            _configurationInvalid = true; _players.Single(x => x.IsHost).IsReady = false;
            await CancelCountdownAsync(CountdownCancellationReason.PlayerUnready); SetError(ErrorCode.DraftConfigurationInvalid); return;
        }
        _waitingForLobbyClose = true; await SendAsync(new SetLobbyOpenCommand(false));
    }

    private async Task KickCoreAsync(ProtocolPlayerId playerId)
    {
        if (_role != SessionRole.Host || _phase is not (ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown)) return;
        var player = _players.FirstOrDefault(x => x.Id == playerId && !x.IsHost); if (player is null) return;
        await SendHostAsync(player.PeerId, HostMessageCode.Kicked, new KickedDto(ErrorCode.Kicked)); await SendAsync(new ClosePeerCommand(player.PeerId, "Kicked."));
        await RemovePeerAsync(player.PeerId, CountdownCancellationReason.PlayerLeft);
    }

    private async Task HandleDisconnectAsync(SteamPeerId peerId)
    {
        if (_role == SessionRole.Client && peerId == _hostPeer)
        {
            if (_lobbyId is not null) await SendAsync(new LeaveLobbyCommand());
            ResetSession();
            Publish(ApplicationScreen.Start, ErrorText(ErrorCode.HostDisconnected), error: ErrorCode.HostDisconnected);
            return;
        }
        if (_role != SessionRole.Host) return;
        if (_phase == ProtocolPhase.PreparingAssets) { var player = _players.FirstOrDefault(x => x.PeerId == peerId); if (player is not null) await RollbackPreparationAsync(player, ErrorCode.ClientDisconnected); return; }
        if (_phase is ProtocolPhase.Drafting or ProtocolPhase.Complete && _draft is not null)
        {
            var player = _players.FirstOrDefault(x => x.PeerId == peerId); if (player is null) return;
            _authenticatedPeers.Remove(peerId); _assetNeedPages.Remove(peerId);
            player.IsConnected = false; player.IsReconnectAssetSync = false; player.PreparationStatus = PreparationStatus.Waiting;
            _draft.Disconnect(new CorePlayerId(player.Id.Value));
            if (_phase == ProtocolPhase.Drafting) { await BroadcastDraftAsync(); PublishHostDraft(); }
            return;
        }
        if (_phase is ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown) await RemovePeerAsync(peerId, CountdownCancellationReason.PlayerDisconnected);
    }

    private async Task RemovePeerAsync(SteamPeerId peerId, CountdownCancellationReason reason)
    {
        var removed = _players.RemoveAll(x => x.PeerId == peerId); _authenticatedPeers.Remove(peerId); _roomCodeBypassPeers.Remove(peerId); if (removed == 0) return;
        _lobbyRevision++; if (_phase == ProtocolPhase.StartingCountdown) await CancelCountdownAsync(reason); await RevalidateHostConfigurationAsync(); await BroadcastLobbyAsync(); PublishLobby();
    }

    private async Task LeaveCoreAsync()
    {
        _options.Logger.Write("session.leave_requested");
        if (_role == SessionRole.Host)
        {
            await BroadcastHostAsync(HostMessageCode.HostClosed, new HostClosedDto(ErrorCode.HostDisconnected));
            foreach (var player in _players.Where(x => !x.IsHost)) await SendAsync(new ClosePeerCommand(player.PeerId, "Host closed the lobby."));
        }
        else if (_role == SessionRole.Client && _hostPeer.Value != 0) await SendAsync(new ClosePeerCommand(_hostPeer, "Client left the lobby."));
        if (_lobbyId is not null) await SendAsync(new LeaveLobbyCommand());
        if (_phase == ProtocolPhase.Complete && _sessionId.Value != Guid.Empty) new SessionCacheManager(_options.Paths.SessionsDirectory).DeleteCompletedSession(_sessionId.Value);
        if (_role == SessionRole.Client && _phase == ProtocolPhase.Complete) ClearReconnectState();
        ResetSession(); Publish(ApplicationScreen.Start, "Steam connected.");
    }

    private void PublishLobby() => Publish(_phase == ProtocolPhase.StartingCountdown ? ApplicationScreen.StartingCountdown : ApplicationScreen.Lobby,
        _configurationInvalid ? ErrorText(ErrorCode.DraftConfigurationInvalid) : _phase == ProtocolPhase.StartingCountdown ? CountdownStatus(_countdownRemainingSeconds) : null,
        error: _configurationInvalid ? ErrorCode.DraftConfigurationInvalid : null);

    private void PublishFromLobbyDto(LobbySnapshotDto lobby)
    {
        _phase = _phase == ProtocolPhase.StartingCountdown ? _phase : ProtocolPhase.LobbyOpen;
        var players = lobby.Players.Select(x => new SessionPlayerSnapshot(x.PlayerId, x.Name, x.Order, x.IsHost, x.IsReady,
            x.PlayerId == lobby.RecipientPlayerId, x.ConnectionStatus)).ToArray();
        SetSnapshot(new(_phase == ProtocolPhase.StartingCountdown ? ApplicationScreen.StartingCountdown : ApplicationScreen.Lobby,
            Snapshot.SteamStatus, _role, FormatRoomCode(), players, _localPlayerId, Snapshot.ErrorCode, Snapshot.StatusMessage, _countdownGeneration));
    }

    private void PublishPreparation()
    {
        var progress = _preparedAssets is null ? (0L, 0L) : (_preparedAssets.TotalAssetBytes, _preparedAssets.TotalAssetBytes);
        Publish(ApplicationScreen.PreparingSession, "Preparing and verifying transformed assets...", bytesReady: progress.Item1, bytesTotal: progress.Item2);
    }

    private void PublishClientPreparation() => Publish(ApplicationScreen.PreparingSession, "Receiving transformed assets...", bytesReady: _clientProgress.BytesReady, bytesTotal: _clientProgress.TotalBytes);

    private void PublishPreparationDto(PreparationSnapshotDto dto)
    {
        var statuses = dto.Players.ToDictionary(x => x.PlayerId, x => x.Status);
        var players = Snapshot.Players.Select(x => x with { PreparationStatus = statuses.GetValueOrDefault(x.PlayerId) }).ToArray();
        SetSnapshot(Snapshot with { Screen = ApplicationScreen.PreparingSession, Players = players, BytesReady = dto.LocalBytesReceived, BytesTotal = dto.LocalBytesTotal });
    }

    private void PublishHostDraft() => PublishDraftDto(CreateDraftDto(_localPlayerId!.Value, new(new(Guid.NewGuid()), _draft!.DraftRevision, 0, 1)));

    private void PublishDraftDto(DraftSnapshotDto dto)
    {
        var selected = dto.SelectedInstanceIds.ToHashSet();
        var hostIds = Snapshot.Players.Where(x => x.IsHost).Select(x => x.PlayerId).ToHashSet();
        var players = dto.Players.Select(x => new SessionPlayerSnapshot(x.PlayerId, x.Name, x.Order,
            hostIds.Contains(x.PlayerId), false, x.PlayerId == _localPlayerId,
            x.ConnectionStatus, IsDraftLocked: x.IsLocked, CurrentPackCardCount: x.CurrentPackCardCount)).ToArray();
        var pack = dto.CurrentPack.Select(x => SessionCard(x)).ToArray();
        var collection = dto.Collection.Select(x => SessionCard(x)).ToArray();
        SetSnapshot(new(ApplicationScreen.Drafting, Snapshot.SteamStatus, _role, FormatRoomCode(), players, _localPlayerId, null, null, _countdownGeneration)
        {
            DraftRevision = dto.Page.Revision, PickRevision = dto.PickRevision, CurrentPack = pack,
            SelectedInstanceIds = selected.ToArray(), Collection = collection, RequiredSelectionCount = dto.RequiredSelectionCount,
            CanLock = dto.CanLock, CanUnlock = dto.CanUnlock, CanForceReady = _role == SessionRole.Host && _draft?.CanForceReady == true,
            Direction = dto.Direction, PackRound = dto.PackRound, TotalPackRounds = dto.TotalPackRounds,
        });
    }

    private void PublishCompletion(IReadOnlyList<CompletedCardDto> cards)
    {
        var collection = cards.Select(x => new SessionCardSnapshot(x.InstanceId, x.AssetHash, AssetPath(x.AssetHash), x.ExportName)).ToArray();
        SetSnapshot(new(ApplicationScreen.Complete, Snapshot.SteamStatus, _role, FormatRoomCode(), Snapshot.Players,
            _localPlayerId, null, "Draft complete.", _countdownGeneration) { Collection = collection, DraftRevision = Snapshot.DraftRevision });
    }

    private SessionCardSnapshot SessionCard(CardAssetDto card) => new(card.InstanceId, card.AssetHash, AssetPath(card.AssetHash));
    private string AssetPath(Protocol.AssetHash hash) => Path.Combine(_options.Paths.GetSessionDirectory(_sessionId.Value), "assets", $"{hash.Value}.webp");

    private void Publish(ApplicationScreen screen, string? status, SteamStatus? steamStatus = null, ErrorCode? error = null, long bytesReady = 0, long bytesTotal = 0)
    {
        var players = _role == SessionRole.Client
            ? Snapshot.Players
            : _players.OrderBy(x => x.Order).Select(x => new SessionPlayerSnapshot(x.Id, x.Name, x.Order, x.IsHost, x.IsReady,
                x.Id == _localPlayerId, x.IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected, x.PreparationStatus)).ToArray();
        SetSnapshot(new(screen, steamStatus ?? Snapshot.SteamStatus, _role, FormatRoomCode(), players, _localPlayerId, error, status, _countdownGeneration)
        { BytesReady = bytesReady, BytesTotal = bytesTotal, SteamInviteAvailable = _pendingInviteLobby is not null });
    }

    private void SetError(ErrorCode? code)
    {
        if (code is null) { SetSnapshot(Snapshot with { ErrorCode = null, StatusMessage = null }); return; }
        _options.Logger.Write("session.error", (int)code.Value);
        SetSnapshot(Snapshot with { ErrorCode = code, StatusMessage = ErrorText(code.Value) });
    }

    private void SetSnapshot(SessionSnapshot snapshot) { Volatile.Write(ref _snapshot, snapshot); SnapshotChanged?.Invoke(snapshot); }
    private string? FormatRoomCode() => _roomCode is null ? null : SteamRoomCode.Format(_roomCode);
    private PlayerState PlayerForPeer(SteamPeerId peerId) => _players.Single(x => x.PeerId == peerId);

    private async Task BroadcastLobbyAsync() { foreach (var player in _players.Where(x => !x.IsHost)) await SendLobbyAsync(player); }
    private Task SendLobbyAsync(PlayerState recipient)
    {
        var players = _players.OrderBy(x => x.Order).Select(x => new LobbyPlayerDto(x.Id, x.Name, x.Order, x.IsHost, x.IsReady,
            x.IsConnected ? ConnectionStatus.Connected : ConnectionStatus.Disconnected)).ToArray();
        return SendHostAsync(recipient.PeerId, HostMessageCode.LobbySnapshot, new LobbySnapshotDto(new(new(Guid.NewGuid()), _lobbyRevision, 0, 1), recipient.Id, false, players));
    }

    private async Task BroadcastPreparationAsync()
    {
        var status = _players.Select(x => new PlayerPreparationDto(x.Id, x.PreparationStatus)).ToArray();
        foreach (var recipient in _players.Where(x => !x.IsHost))
        {
            var local = recipient.PreparationStatus == PreparationStatus.Ready && _preparedAssets is not null ? _preparedAssets.TotalAssetBytes : 0;
            await SendHostAsync(recipient.PeerId, HostMessageCode.PreparationSnapshot,
                new PreparationSnapshotDto(new(new(Guid.NewGuid()), _manifestRevision, 0, 1), status, local, _preparedAssets?.TotalAssetBytes ?? 0));
        }
    }

    private async Task BroadcastHostAsync(HostMessageCode code, ProtocolPayload payload)
    { foreach (var player in _players.Where(x => !x.IsHost && x.IsConnected)) await SendHostAsync(player.PeerId, code, payload); }
    private Task SendErrorAsync(SteamPeerId peerId, ErrorCode code, bool fatal) => SendHostAsync(peerId, HostMessageCode.Error, new ErrorDto(code, fatal));
    private Task SendClientAsync(SteamPeerId peerId, ClientMessageCode code, ProtocolPayload payload) => SendAsync(new SendMessageCommand(peerId,
        ControlMessageCodec.SerializeClient(new(ProtocolConstants.Version, (int)code, Guid.NewGuid(), payload), _options.ClientConfiguration.ControlMessageMaxBytes)));
    private Task SendHostAsync(SteamPeerId peerId, HostMessageCode code, ProtocolPayload payload) => SendAsync(new SendMessageCommand(peerId,
        ControlMessageCodec.SerializeHost(new(ProtocolConstants.Version, (int)code, Guid.NewGuid(), payload), _hostConfiguration?.Limits.ControlMessageMaxBytes ?? ProtocolConstants.DefaultControlMessageMaxBytes)));
    private async Task SendAsync(SteamTransportCommand command) => await _transport.Commands.WriteAsync(command, _stopping.Token);

    private void ResetSession()
    {
        CancelPreparationWork(); _preparationGeneration++;
        _players.Clear(); _authenticatedPeers.Clear(); _roomCodeBypassPeers.Clear(); _assetNeedPages.Clear(); _completedPlayers.Clear(); _lobbyId = null; _hostPeer = default;
        _sessionId = default; _localPlayerId = null; _roomCode = null; _pendingName = null; _role = SessionRole.None; _phase = ProtocolPhase.Closed;
        _lobbyRevision = 0; _countdownGeneration = 0; _countdownRemainingSeconds = 0; _manifestRevision = 0; _helloSent = false; _reconnecting = false; _waitingForLobbyClose = false;
        _hostConfiguration = null; _configurationInvalid = false; _pendingReopenName = null; _pendingInviteLobby = null;
        ResetRoundState();
    }

    private void ResetRoundState()
    {
        CancelPreparationWork(); _preparationGeneration++;
        _generatedDraft = null; _draft = null; _draftRandom = null; _preparedAssets = null;
        _definitionAssets = new Dictionary<CardDefinitionId, CoreAssetHash>(); _definitionNames = new Dictionary<CardDefinitionId, string>();
        _assetNeedPages.Clear(); _manifestPages.Reset(); _draftPages.Reset(); _completionPages.Reset();
        ResetClientPreparationState(); _clientCompletion = [];
    }

    private void ResetClientPreparationState()
    {
        _clientTransfer = null; _clientProgress = new(0, 0, 0, 0); _transferProgressGeneration++;
    }

    private void BeginClientPreparationAttempt()
    {
        CancelPreparationWork(); _preparationGeneration++;
        _manifestPages.Reset(); _draftPages.Reset(); _completionPages.Reset(); ResetClientPreparationState();
    }

    private void CancelPreparationWork()
    {
        if (_preparationCancellation is null) return;
        _preparationCancellation.Cancel(); _preparationCancellation.Dispose(); _preparationCancellation = null;
    }

    private bool ValidateHostConfiguration()
    {
        if (_hostConfiguration is null) return false;
        return DraftSettingsValidator.Validate(_hostConfiguration.Settings, _players.Count, _hostConfiguration.Definitions,
            new DraftLimits(_hostConfiguration.Limits.MaxPackSize, _hostConfiguration.Limits.MaxDraftCardInstances)).IsValid;
    }

    private async Task RevalidateHostConfigurationAsync()
    {
        if (_role != SessionRole.Host || _hostConfiguration is null) return;
        _configurationInvalid = !ValidateHostConfiguration();
        if (!_configurationInvalid) return;
        var host = _players.Single(x => x.IsHost);
        host.IsReady = false;
        if (_phase == ProtocolPhase.StartingCountdown) await CancelCountdownAsync(CountdownCancellationReason.PlayerJoined);
        SetError(ErrorCode.DraftConfigurationInvalid);
    }

    private static bool IsPreparationError(ErrorCode code) => code is ErrorCode.HostAssetPreparationFailed or ErrorCode.ClientAssetTransferStalled or
        ErrorCode.ClientAssetVerificationFailed or ErrorCode.ClientDiskSpaceInsufficient or ErrorCode.ClientDisconnected;

    private bool IsExpectedHostMessage(HostMessageCode code) => code switch
    {
        HostMessageCode.Welcome => _phase == ProtocolPhase.Handshake,
        HostMessageCode.Error or HostMessageCode.HostClosed => true,
        HostMessageCode.LobbySnapshot => _phase is ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown or ProtocolPhase.Complete,
        HostMessageCode.CountdownStarted => _phase is ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown,
        HostMessageCode.CountdownCancelled => _phase is ProtocolPhase.StartingCountdown or ProtocolPhase.LobbyOpen,
        HostMessageCode.AssetManifest => _phase is ProtocolPhase.StartingCountdown or ProtocolPhase.PreparingAssets,
        HostMessageCode.PreparationSnapshot => _phase == ProtocolPhase.PreparingAssets,
        HostMessageCode.DraftSnapshot => _phase is ProtocolPhase.PreparingAssets or ProtocolPhase.Drafting,
        HostMessageCode.DraftCompleted => _phase is ProtocolPhase.PreparingAssets or ProtocolPhase.Drafting or ProtocolPhase.Complete,
        HostMessageCode.LobbyAvailability => _phase == ProtocolPhase.Complete,
        HostMessageCode.Kicked => _phase is ProtocolPhase.LobbyOpen or ProtocolPhase.StartingCountdown,
        _ => false,
    };

    public static string ErrorText(ErrorCode code) => code switch
    {
        ErrorCode.SteamUnavailable => "Steam must be running and signed in.", ErrorCode.LobbyCreationFailed => "The Steam lobby could not be created.",
        ErrorCode.LobbyNotFound => "No open room matches that code.", ErrorCode.RoomCodeInvalid => "The room code is invalid.",
        ErrorCode.RoomClosed => "That room is no longer open.", ErrorCode.LobbyFull => "The lobby is full.",
        ErrorCode.NameInvalid => "Use 1-16 printable ASCII characters for your name.", ErrorCode.NameDuplicate => "That player name is already in use.",
        ErrorCode.ProtocolVersionMismatch => "The host and client protocol versions do not match.", ErrorCode.MalformedMessage => "A malformed network message was received.",
        ErrorCode.HostDisconnected => "The host disconnected.", ErrorCode.ClientDisconnected => "A player disconnected during preparation.",
        ErrorCode.DraftConfigurationInvalid => "The draft settings or scanned card pool are invalid.", ErrorCode.HostAssetPreparationFailed => "The host could not prepare session assets.",
        ErrorCode.ClientAssetTransferStalled => "Asset transfer stalled.", ErrorCode.ClientAssetVerificationFailed => "A client could not verify session assets.",
        ErrorCode.ClientDiskSpaceInsufficient => "A client has insufficient disk space.", ErrorCode.Kicked => "The host removed you from the lobby.",
        ErrorCode.SessionNoLongerAvailable => "That draft session is no longer available.",
        ErrorCode.StaleRevision => "That draft action was stale.", _ => "That action is not available right now.",
    };

    private void PersistActiveClientState()
    {
        if (_role != SessionRole.Client || _options.ClientStateStore is null || _sessionId.Value == Guid.Empty) return;
        _options.ClientStateStore.Save(new ClientState(
            _hostPeer.Value,
            _lobbyId?.Value,
            _sessionId.Value,
            _roomCode,
            _pendingName,
            false));
    }

    private void ClearReconnectState()
    {
        if (_options.ClientStateStore is null) return;
        var playerName = Snapshot.Players.FirstOrDefault(x => x.IsLocal)?.Name ?? _pendingName;
        _options.ClientStateStore.Save(new ClientState(null, null, null, null, playerName, true));
    }

    private void PublishHostDraftOrCompletion()
    {
        if (_phase == ProtocolPhase.Drafting) PublishHostDraft();
        else if (_localPlayerId is not null) PublishCompletion(CompletedCards(_localPlayerId.Value));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel(); _commands.Writer.TryComplete(); await _eventTask; await _runTask; await _transport.DisposeAsync(); _stopping.Dispose();
    }
}
