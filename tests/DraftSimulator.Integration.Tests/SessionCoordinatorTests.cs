using System.Collections.Concurrent;
using System.Threading.Channels;
using DraftSimulator.App.Services;
using DraftSimulator.Infrastructure;
using DraftSimulator.Protocol;
using DraftSimulator.Core;

namespace DraftSimulator.Integration.Tests;

public sealed class SessionCoordinatorTests
{
    [Fact]
    public async Task HostAndClientCompleteAuthenticatedHandshake()
    {
        await using var session = await TestSession.CreateAsync();

        Assert.Equal(ApplicationScreen.Lobby, session.Host.Snapshot.Screen);
        Assert.Equal(ApplicationScreen.Lobby, session.Client.Snapshot.Screen);
        Assert.Equal(2, session.Host.Snapshot.Players.Count);
        Assert.Equal(["Host", "Client"], session.Host.Snapshot.Players.Select(player => player.Name));
        Assert.Single(session.Client.Snapshot.Players, player => player.IsLocal && player.Name == "Client");
        Assert.NotEqual(session.Host.Snapshot.LocalPlayerId, session.Client.Snapshot.LocalPlayerId);
    }

    [Fact]
    public async Task HostRejectsInvalidRoomToken()
    {
        await using var harness = new NetworkHarness();
        await using var host = harness.CreateHostCoordinator();
        await harness.InitializeHostAsync(host, "Host");

        var rogue = new SteamPeerId(99);
        harness.ConnectToHost(rogue, new SteamConnectionId(99));
        harness.SendToHost(rogue, new SteamConnectionId(99), ClientMessageCode.Hello,
            new HelloDto(ProtocolConstants.Version, new LobbyId(NetworkHarness.Lobby.Value), "0123456789", null, "Rogue"));

        var error = await harness.WaitForHostMessageAsync<ErrorDto>(rogue);
        Assert.Equal(ErrorCode.RoomCodeInvalid, error.ErrorCode);
        Assert.True(error.Fatal);
        Assert.Single(host.Snapshot.Players);
    }

    [Fact]
    public async Task HostRejectsInvalidAndDuplicateNames()
    {
        await using var harness = new NetworkHarness();
        await using var host = harness.CreateHostCoordinator();
        var code = await harness.InitializeHostAsync(host, "Host");

        harness.ConnectAndHello(new SteamPeerId(90), new SteamConnectionId(90), code, "bad\nname");
        var invalid = await harness.WaitForHostMessageAsync<ErrorDto>(new SteamPeerId(90));
        Assert.Equal(ErrorCode.NameInvalid, invalid.ErrorCode);

        harness.ConnectAndHello(new SteamPeerId(91), new SteamConnectionId(91), code, "HOST");
        var duplicate = await harness.WaitForHostMessageAsync<ErrorDto>(new SteamPeerId(91));
        Assert.Equal(ErrorCode.NameDuplicate, duplicate.ErrorCode);
        Assert.Single(host.Snapshot.Players);
    }

    [Fact]
    public async Task AllReadyCountdownCancelsWhenPlayerUnready()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(200));

        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.StartingCountdown);
        await session.Client.SetReadyAsync(false);

        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Lobby &&
                                  session.Client.Snapshot.Screen == ApplicationScreen.Lobby);
        Assert.False(session.Host.Snapshot.Players.Single(player => player.Name == "Client").IsReady);
    }

    [Fact]
    public async Task AllReadyCountdownPublishesEachRemainingSecondToHostAndClient()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(2250));
        var hostStatuses = new ConcurrentQueue<string>();
        var clientStatuses = new ConcurrentQueue<string>();
        session.Host.SnapshotChanged += snapshot =>
        {
            if (snapshot.Screen == ApplicationScreen.StartingCountdown && snapshot.StatusMessage is not null)
                hostStatuses.Enqueue(snapshot.StatusMessage);
        };
        session.Client.SnapshotChanged += snapshot =>
        {
            if (snapshot.Screen == ApplicationScreen.StartingCountdown && snapshot.StatusMessage is not null)
                clientStatuses.Enqueue(snapshot.StatusMessage);
        };

        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Drafting &&
                                  session.Client.Snapshot.Screen == ApplicationScreen.Drafting);

        Assert.Contains("Game starts in 3...", hostStatuses);
        Assert.Contains("Game starts in 2...", hostStatuses);
        Assert.Contains("Game starts in 1...", hostStatuses);
        Assert.Contains("Game starts in 3...", clientStatuses);
        Assert.Contains("Game starts in 2...", clientStatuses);
        Assert.Contains("Game starts in 1...", clientStatuses);
    }

    [Fact]
    public async Task AllReadyCountdownPreparesAssetsAndTransitionsToDraft()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(30));

        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);

        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Drafting);
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Drafting);
        Assert.Contains(session.Harness.HostCommands, command => command is SetLobbyOpenCommand { IsOpen: false });
        Assert.All(session.Host.Snapshot.CurrentPack, card => Assert.EndsWith(".webp", card.AssetPath));
    }

    [Fact]
    public async Task PersonalizedSnapshotsSelectionLockPassAndCompletionRemainPrivate()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Drafting && session.Client.Snapshot.Screen == ApplicationScreen.Drafting);

        var hostCard = session.Host.Snapshot.CurrentPack[0].InstanceId;
        await session.Host.SetSelectionAsync([hostCard]);
        await session.Host.SetPickLockedAsync(true);
        await WaitUntilAsync(() => session.Client.Snapshot.Players.Single(x => x.IsHost).IsDraftLocked);
        Assert.Empty(session.Client.Snapshot.SelectedInstanceIds);
        Assert.Empty(session.Client.Snapshot.Collection);

        var clientCard = session.Client.Snapshot.CurrentPack[0].InstanceId;
        await session.Client.SetSelectionAsync([clientCard]);
        await session.Client.SetPickLockedAsync(true);
        await WaitUntilAsync(() => session.Host.Snapshot.PickRevision == 2 && session.Client.Snapshot.PickRevision == 2);
        Assert.Single(session.Host.Snapshot.Collection);
        Assert.Single(session.Client.Snapshot.Collection);

        await session.Host.SetSelectionAsync([session.Host.Snapshot.CurrentPack[0].InstanceId]);
        await session.Host.SetPickLockedAsync(true);
        await session.Client.SetSelectionAsync([session.Client.Snapshot.CurrentPack[0].InstanceId]);
        await session.Client.SetPickLockedAsync(true);
        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Complete && session.Client.Snapshot.Screen == ApplicationScreen.Complete);
        Assert.Equal(2, session.Host.Snapshot.Collection.Count);
        Assert.Equal(2, session.Client.Snapshot.Collection.Count);
        Assert.All(session.Client.Snapshot.Collection, card => Assert.Equal("Card", card.ExportName));

        await session.Host.ReopenLobbyAsync();
        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Lobby && session.Client.Snapshot.LobbyAvailable);
        Assert.Single(session.Host.Snapshot.Players);
        await session.Client.JoinReopenedLobbyAsync("Client");
        await WaitUntilAsync(() => session.Host.Snapshot.Players.Count == 2 && session.Client.Snapshot.Screen == ApplicationScreen.Lobby);
        Assert.All(session.Host.Snapshot.Players, player => Assert.False(player.IsReady));
    }

    [Fact]
    public async Task ForceReadyIncludesDisconnectedPlayerAndRetainsRoster()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Drafting);
        await session.Host.SetSelectionAsync([session.Host.Snapshot.CurrentPack[0].InstanceId]);
        await session.Host.SetPickLockedAsync(true);

        session.Harness.DisconnectClientFromHostOnly();
        await WaitUntilAsync(() => session.Host.Snapshot.Players.Single(x => !x.IsHost).ConnectionStatus == ConnectionStatus.Disconnected);
        await session.Host.ForceReadyAsync();

        await WaitUntilAsync(() => session.Host.Snapshot.PickRevision == 2);
        Assert.Equal(2, session.Host.Snapshot.Players.Count);
    }

    [Fact]
    public async Task DisconnectedClientReconnectsDirectlyAndReceivesLatestPersonalizedState()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Drafting);

        var playerId = session.Client.Snapshot.LocalPlayerId;
        await session.Client.SetSelectionAsync([session.Client.Snapshot.CurrentPack[0].InstanceId]);
        await WaitUntilAsync(() => session.Client.Snapshot.SelectedInstanceIds.Count == 1);
        await session.Host.SetSelectionAsync([session.Host.Snapshot.CurrentPack[0].InstanceId]);
        await session.Host.SetPickLockedAsync(true);

        session.Harness.DisconnectClient();
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Start &&
                                  session.Host.Snapshot.Players.Single(x => !x.IsHost).ConnectionStatus == ConnectionStatus.Disconnected);
        await session.Host.ForceReadyAsync();
        await WaitUntilAsync(() => session.Host.Snapshot.PickRevision == 2);
        var assetChunkCount = session.Harness.HostCommands.Count(command =>
            command is SendMessageCommand send && send.Payload.Span.StartsWith("DSAS"u8));

        var state = session.Harness.LoadClientState();
        await session.Client.ReconnectAsync(state!);

        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Drafting &&
                                  session.Host.Snapshot.Players.Single(x => !x.IsHost).ConnectionStatus == ConnectionStatus.Connected);
        Assert.Equal(playerId, session.Client.Snapshot.LocalPlayerId);
        Assert.Equal(session.Host.Snapshot.PickRevision, session.Client.Snapshot.PickRevision);
        Assert.Single(session.Client.Snapshot.Collection);
        Assert.Equal(assetChunkCount, session.Harness.HostCommands.Count(command =>
            command is SendMessageCommand send && send.Payload.Span.StartsWith("DSAS"u8)));
        Assert.Contains(session.Harness.ClientCommands, command => command is ConnectPeerCommand { PeerId: var peer } && peer == NetworkHarness.HostPeer);
    }

    [Fact]
    public async Task DisconnectedClientReconnectsToItsCompletionScreen()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await CompleteDraftAsync(session);
        var playerId = session.Client.Snapshot.LocalPlayerId;
        var reconnectState = session.Harness.LoadClientState();
        Assert.Equal(ApplicationScreen.Complete, session.Client.Snapshot.Screen);

        session.Harness.DisconnectClient();
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Start);
        await session.Client.ReconnectAsync(reconnectState!);

        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Complete);
        Assert.Equal(playerId, session.Client.Snapshot.LocalPlayerId);
        Assert.Equal(2, session.Client.Snapshot.Collection.Count);
    }

    [Fact]
    public async Task ReconnectWithWrongSessionIsRejectedAndClearsReconnectState()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Drafting);
        session.Harness.DisconnectClient();
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Start);

        var state = session.Harness.LoadClientState()! with { LastApplicationSessionId = Guid.NewGuid() };
        await session.Client.ReconnectAsync(state);

        await WaitUntilAsync(() => session.Client.Snapshot.ErrorCode == ErrorCode.SessionNoLongerAvailable);
        Assert.Equal(ApplicationScreen.Start, session.Client.Snapshot.Screen);
        Assert.True(session.Harness.LoadClientState()!.PreviousSessionEndedNormally);
    }

    [Fact]
    public async Task GracefulHostClosureReturnsClientToStartWithPredefinedError()
    {
        await using var session = await TestSession.CreateAsync();

        await session.Host.LeaveAsync();

        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Start &&
                                  session.Client.Snapshot.ErrorCode == ErrorCode.HostDisconnected);
        Assert.Equal(ApplicationScreen.Start, session.Host.Snapshot.Screen);
        Assert.Contains(session.Harness.HostCommands, command => command is ClosePeerCommand { PeerId: var peer } && peer == NetworkHarness.ClientPeer);
    }

    [Fact]
    public async Task NormalCompletionExitClearsReconnectStateAndSessionCache()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await CompleteDraftAsync(session);
        Assert.False(session.Harness.LoadClientState()!.PreviousSessionEndedNormally);

        await session.Client.LeaveAsync();

        Assert.True(session.Harness.LoadClientState()!.PreviousSessionEndedNormally);
        Assert.False(Directory.Exists(session.Harness.ClientSessionDirectory));
    }

    [Fact]
    public async Task CorruptTransferRollsEveryoneBackAndUnreadiesFailingClient()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        session.Harness.CorruptNextAssetChunk = true;
        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);

        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Lobby &&
                                  session.Host.Snapshot.ErrorCode is not null &&
                                  session.Client.Snapshot.Screen == ApplicationScreen.Lobby);
        Assert.False(session.Host.Snapshot.Players.Single(x => !x.IsHost).IsReady);
        Assert.Equal(ApplicationScreen.Lobby, session.Client.Snapshot.Screen);
        Assert.Contains(session.Harness.HostCommands, command => command is SetLobbyOpenCommand { IsOpen: true });
    }

    [Fact]
    public async Task ValidClientCacheHitRequestsNoAssetChunks()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        session.Harness.PrepopulateClientCache();
        await session.Host.SetReadyAsync(true);
        await session.Client.SetReadyAsync(true);

        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Drafting);
        Assert.DoesNotContain(session.Harness.HostCommands, command =>
            command is SendMessageCommand send && send.Payload.Span.StartsWith("DSAS"u8));
    }

    [Fact]
    public async Task ClientDisconnectIsRemovedFromLobby()
    {
        await using var session = await TestSession.CreateAsync();

        session.Harness.DisconnectClient();

        await WaitUntilAsync(() => session.Host.Snapshot.Players.Count == 1);
        Assert.Equal("Host", session.Host.Snapshot.Players[0].Name);
    }

    [Fact]
    public async Task KickNotifiesClientClosesConnectionAndRemovesPlayer()
    {
        await using var session = await TestSession.CreateAsync();
        var clientId = session.Host.Snapshot.Players.Single(player => !player.IsHost).PlayerId;

        await session.Host.KickAsync(clientId);

        await WaitUntilAsync(() => session.Host.Snapshot.Players.Count == 1 &&
                                  session.Client.Snapshot.ErrorCode == ErrorCode.Kicked);
        Assert.Contains(session.Harness.HostCommands, command => command is ClosePeerCommand close && close.PeerId == NetworkHarness.ClientPeer);
        Assert.Contains(session.Harness.ClientCommands, command => command is LeaveLobbyCommand);
    }

    [Fact]
    public async Task ReopenedSessionClearsPriorDraftCompletionAndTransferState()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await CompleteDraftAsync(session);
        Assert.Equal(2, session.Client.Snapshot.Collection.Count);

        await session.Host.ReopenLobbyAsync();
        await WaitUntilAsync(() => session.Client.Snapshot.LobbyAvailable);
        Assert.Contains(session.Harness.ClientCommands, command => command is LeaveLobbyCommand);
        await session.Client.JoinReopenedLobbyAsync("Client");
        await WaitUntilAsync(() => session.Host.Snapshot.Players.Count == 2 && session.Client.Snapshot.Screen == ApplicationScreen.Lobby);

        await CompleteDraftAsync(session);
        Assert.Equal(2, session.Host.Snapshot.Collection.Count);
        Assert.Equal(2, session.Client.Snapshot.Collection.Count);
    }

    [Fact]
    public async Task DetachedPreparationFromFailedAttemptCannotMutateRetry()
    {
        var preparer = new ControlledPreparer();
        await using var harness = new NetworkHarness();
        await using var host = harness.CreateHostCoordinator(TimeSpan.FromMilliseconds(20), preparer);
        await using var client = harness.CreateClientCoordinator(TimeSpan.FromMilliseconds(20));
        var code = await harness.InitializeHostAsync(host, "Host");
        harness.InitializeClient();
        await WaitUntilAsync(() => client.Snapshot.SteamStatus == SteamStatus.Ready);
        await client.JoinAsync("Client", code);
        await WaitUntilAsync(() => host.Snapshot.Players.Count == 2);
        await host.ConfigureHostAsync(harness.CreateConfiguration());
        await host.SetReadyAsync(true); await client.SetReadyAsync(true);
        await preparer.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        harness.FailHostOperation();
        await WaitUntilAsync(() => host.Snapshot.Screen == ApplicationScreen.Lobby);
        await host.SetReadyAsync(true);
        await WaitUntilAsync(() => preparer.CallCount >= 2);
        await WaitUntilAsync(() => host.Snapshot.Screen == ApplicationScreen.Drafting);

        preparer.ReleaseFirst.Set();
        await Task.Delay(100);
        Assert.Equal(ApplicationScreen.Drafting, host.Snapshot.Screen);
    }

    [Fact]
    public async Task RapidQueuedTogglesComposeAndReconcileAuthoritatively()
    {
        await using var session = await TestSession.CreateAsync(TimeSpan.FromMilliseconds(20));
        await session.Host.SetReadyAsync(true); await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Drafting);
        var cards = session.Client.Snapshot.CurrentPack.Select(x => x.InstanceId).Take(2).ToArray();

        await Task.WhenAll(session.Client.ToggleSelectionAsync(cards[0]), session.Client.ToggleSelectionAsync(cards[1]));
        await WaitUntilAsync(() => session.Client.Snapshot.SelectedInstanceIds.Count == 2);

        Assert.Equal(cards.OrderBy(x => x.Value), session.Client.Snapshot.SelectedInstanceIds.OrderBy(x => x.Value));
    }

    [Fact]
    public async Task RosterChangeRevalidatesAndBlocksInvalidHostConfiguration()
    {
        await using var harness = new NetworkHarness();
        await using var host = harness.CreateHostCoordinator();
        var code = await harness.InitializeHostAsync(host, "Host");
        var configuration = harness.CreateConfiguration();
        await host.ConfigureHostAsync(configuration with { Limits = configuration.Limits with { MaxDraftCardInstances = 2 } });
        await host.SetReadyAsync(true);

        harness.ConnectAndHello(new SteamPeerId(90), new SteamConnectionId(90), code, "Second");
        await WaitUntilAsync(() => host.Snapshot.Players.Count == 2);

        Assert.Equal(ErrorCode.DraftConfigurationInvalid, host.Snapshot.ErrorCode);
        Assert.False(host.Snapshot.Players.Single(x => x.IsHost).IsReady);
        Assert.Equal(ApplicationScreen.Lobby, host.Snapshot.Screen);
    }

    private static async Task CompleteDraftAsync(TestSession session)
    {
        await session.Host.SetReadyAsync(true); await session.Client.SetReadyAsync(true);
        await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Drafting && session.Client.Snapshot.Screen == ApplicationScreen.Drafting);
        while (session.Host.Snapshot.Screen == ApplicationScreen.Drafting)
        {
            var revision = session.Host.Snapshot.PickRevision;
            await session.Host.SetSelectionAsync([session.Host.Snapshot.CurrentPack[0].InstanceId]);
            await session.Host.SetPickLockedAsync(true);
            await session.Client.SetSelectionAsync([session.Client.Snapshot.CurrentPack[0].InstanceId]);
            await session.Client.SetPickLockedAsync(true);
            await WaitUntilAsync(() => session.Host.Snapshot.Screen == ApplicationScreen.Complete || session.Host.Snapshot.PickRevision > revision);
        }
        await WaitUntilAsync(() => session.Client.Snapshot.Screen == ApplicationScreen.Complete);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(2, timeout.Token);
    }

    private sealed class TestSession : IAsyncDisposable
    {
        private TestSession(NetworkHarness harness, SessionCoordinator host, SessionCoordinator client) =>
            (Harness, Host, Client) = (harness, host, client);

        public NetworkHarness Harness { get; }
        public SessionCoordinator Host { get; }
        public SessionCoordinator Client { get; }

        public static async Task<TestSession> CreateAsync(TimeSpan? countdown = null)
        {
            var harness = new NetworkHarness();
            var host = harness.CreateHostCoordinator(countdown);
            var client = harness.CreateClientCoordinator(countdown);
            try
            {
                var code = await harness.InitializeHostAsync(host, "Host");
                harness.InitializeClient();
                await WaitUntilAsync(() => client.Snapshot.SteamStatus == SteamStatus.Ready);
                await client.JoinAsync("Client", code);
                await WaitUntilAsync(() => host.Snapshot.Players.Count == 2 && client.Snapshot.Players.Count == 2);
                await host.ConfigureHostAsync(harness.CreateConfiguration());
                return new TestSession(harness, host, client);
            }
            catch
            {
                await host.DisposeAsync();
                await client.DisposeAsync();
                await harness.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Client.DisposeAsync();
            await Harness.DisposeAsync();
        }
    }

    private sealed class NetworkHarness : IAsyncDisposable
    {
        public static readonly SteamLobbyId Lobby = new(500);
        public static readonly SteamPeerId HostPeer = new(1);
        public static readonly SteamPeerId ClientPeer = new(2);
        private static readonly SteamConnectionId HostConnection = new(10);
        private static readonly SteamConnectionId ClientConnection = new(20);

        private readonly FakeSteamTransport _host = new();
        private readonly FakeSteamTransport _client = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly ConcurrentQueue<(SteamPeerId Peer, ControlMessage Message)> _hostMessages = new();
        private readonly Task _hostPump;
        private readonly Task _clientPump;
        private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"draft-simulator-integration-{Guid.NewGuid():N}");
        private readonly Guid _sessionId = Guid.NewGuid();
        private HostDraftConfiguration? _configuration;
        private string? _roomCode;

        public bool CorruptNextAssetChunk { get; set; }

        public NetworkHarness()
        {
            Directory.CreateDirectory(_temporaryDirectory);
            Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "host"));
            Directory.CreateDirectory(Path.Combine(_temporaryDirectory, "client"));
            File.WriteAllBytes(Path.Combine(_temporaryDirectory, "Card.png"), Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
            _hostPump = PumpHostAsync();
            _clientPump = PumpClientAsync();
        }

        public ConcurrentQueue<SteamTransportCommand> HostCommands => _host.SeenCommands;
        public ConcurrentQueue<SteamTransportCommand> ClientCommands => _client.SeenCommands;
        public string ClientSessionDirectory => new LocalAppDataPaths(Path.Combine(_temporaryDirectory, "client")).GetSessionDirectory(_sessionId);

        public SessionCoordinator CreateHostCoordinator(TimeSpan? countdown = null, IAssetPreparer? preparer = null) =>
            new(_host, new SessionCoordinatorOptions
            {
                CountdownDuration = countdown ?? TimeSpan.FromSeconds(3),
                Paths = new LocalAppDataPaths(Path.Combine(_temporaryDirectory, "host")),
                RandomSourceFactory = static () => (7, new SeededRandomSource(7)),
                SessionIdFactory = () => _sessionId,
                AssetPreparer = preparer ?? new AssetPreparer(),
            });

        public SessionCoordinator CreateClientCoordinator(TimeSpan? countdown = null) =>
            new(_client, new SessionCoordinatorOptions
            {
                CountdownDuration = countdown ?? TimeSpan.FromSeconds(3),
                Paths = new LocalAppDataPaths(Path.Combine(_temporaryDirectory, "client")),
                ClientStateStore = new ClientStateStore(new LocalAppDataPaths(Path.Combine(_temporaryDirectory, "client")).ClientStateFile),
            });

        public ClientState? LoadClientState() =>
            new ClientStateStore(new LocalAppDataPaths(Path.Combine(_temporaryDirectory, "client")).ClientStateFile).Load();

        public HostDraftConfiguration CreateConfiguration()
        {
            if (_configuration is not null) return _configuration;
            var rarities = Enum.GetValues<Rarity>().ToDictionary(x => x, _ => new RaritySettings(0, 0, 0));
            rarities[Rarity.Common] = new RaritySettings(0, 2, 1);
            var definition = new CardDefinition(CardDefinitionId.New(), Path.Combine(_temporaryDirectory, "Card.png"), "Card", Rarity.Common);
            return _configuration = new HostDraftConfiguration(new DraftSettings(ReplacementMode.WithReplacement, rarities, 2, 1, 1, DirectionRule.AlwaysClockwise), [definition], HostConfiguration.Defaults);
        }

        public void PrepopulateClientCache()
        {
            var configuration = CreateConfiguration();
            var paths = new LocalAppDataPaths(Path.Combine(_temporaryDirectory, "client"));
            new AssetPreparer().Prepare(_sessionId, configuration.Definitions, paths.GetSessionDirectory(_sessionId), configuration.Limits);
        }

        public async Task<string> InitializeHostAsync(SessionCoordinator host, string name)
        {
            _host.Emit(new SteamInitializedEvent(HostPeer));
            await WaitUntilAsync(() => host.Snapshot.SteamStatus == SteamStatus.Ready);
            await host.HostAsync(name);
            await WaitUntilAsync(() => host.Snapshot.Screen == ApplicationScreen.Lobby);
            return host.Snapshot.RoomCode!.Replace("-", string.Empty, StringComparison.Ordinal);
        }

        public void InitializeClient() => _client.Emit(new SteamInitializedEvent(ClientPeer));

        public void ConnectToHost(SteamPeerId peer, SteamConnectionId connection) =>
            _host.Emit(new ConnectionRequestedEvent(connection, peer, true));

        public void ConnectAndHello(SteamPeerId peer, SteamConnectionId connection, string roomCode, string name)
        {
            ConnectToHost(peer, connection);
            SendToHost(peer, connection, ClientMessageCode.Hello,
                new HelloDto(ProtocolConstants.Version, new LobbyId(Lobby.Value), roomCode, null, name));
        }

        public void SendToHost(SteamPeerId peer, SteamConnectionId connection, ClientMessageCode code, ProtocolPayload payload)
        {
            var bytes = ControlMessageCodec.SerializeClient(new ControlMessage(
                ProtocolConstants.Version, (int)code, Guid.NewGuid(), payload));
            _host.Emit(new MessageReceivedEvent(connection, peer, bytes));
        }

        public async Task<T> WaitForHostMessageAsync<T>(SteamPeerId peer) where T : ProtocolPayload
        {
            T? found = null;
            await WaitUntilAsync(() =>
            {
                foreach (var item in _hostMessages)
                {
                    if (item.Peer == peer && item.Message.Payload is T payload)
                    {
                        found = payload;
                        return true;
                    }
                }
                return false;
            });
            return found!;
        }

        public void DisconnectClient()
        {
            _host.Emit(new PeerDisconnectedEvent(HostConnection, ClientPeer, "test disconnect"));
            _client.Emit(new PeerDisconnectedEvent(ClientConnection, HostPeer, "test disconnect"));
        }

        public void DisconnectClientFromHostOnly() =>
            _host.Emit(new PeerDisconnectedEvent(HostConnection, ClientPeer, "test disconnect"));

        public void FailHostOperation() => _host.Emit(new TransportOperationFailedEvent("test", "test"));

        private async Task PumpHostAsync()
        {
            try
            {
                await foreach (var command in _host.CommandsReader.ReadAllAsync(_stopping.Token))
                {
                    _host.SeenCommands.Enqueue(command);
                    switch (command)
                    {
                        case CreateLobbyCommand create:
                            _roomCode = SteamRoomCode.Normalize(create.RoomCode);
                            _host.Emit(new LobbyCreatedEvent(Lobby, _roomCode));
                            break;
                        case SendMessageCommand send when send.PeerId == ClientPeer && send.Payload.Span.StartsWith("DSAS"u8):
                            var payload = send.Payload.ToArray();
                            if (CorruptNextAssetChunk)
                            {
                                CorruptNextAssetChunk = false;
                                payload[^1] ^= 0xff;
                            }
                            _client.Emit(new MessageReceivedEvent(ClientConnection, HostPeer, payload));
                            break;
                        case SendMessageCommand send when send.PeerId == ClientPeer:
                            _hostMessages.Enqueue((send.PeerId, ControlMessageCodec.DeserializeHost(send.Payload.Span)));
                            _client.Emit(new MessageReceivedEvent(ClientConnection, HostPeer, send.Payload));
                            break;
                        case SendMessageCommand send:
                            _hostMessages.Enqueue((send.PeerId, ControlMessageCodec.DeserializeHost(send.Payload.Span)));
                            break;
                        case SetLobbyOpenCommand setOpen:
                            _host.Emit(new LobbyOpenChangedEvent(Lobby, setOpen.IsOpen));
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        }

        private async Task PumpClientAsync()
        {
            try
            {
                await foreach (var command in _client.CommandsReader.ReadAllAsync(_stopping.Token))
                {
                    _client.SeenCommands.Enqueue(command);
                    switch (command)
                    {
                        case FindLobbyCommand find when SteamRoomCode.Normalize(find.RoomCode) == _roomCode:
                            _client.Emit(new LobbySearchCompletedEvent([Lobby]));
                            break;
                        case FindLobbyCommand:
                            _client.Emit(new LobbySearchCompletedEvent([]));
                            break;
                        case JoinLobbyCommand:
                            _client.Emit(new LobbyEnteredEvent(Lobby, HostPeer));
                            _host.Emit(new ConnectionRequestedEvent(HostConnection, ClientPeer, true));
                            _host.Emit(new PeerConnectedEvent(HostConnection, ClientPeer));
                            _client.Emit(new PeerConnectedEvent(ClientConnection, HostPeer));
                            break;
                        case ConnectPeerCommand connect when connect.PeerId == HostPeer:
                            _host.Emit(new ConnectionRequestedEvent(HostConnection, ClientPeer, false));
                            _host.Emit(new PeerConnectedEvent(HostConnection, ClientPeer));
                            _client.Emit(new PeerConnectedEvent(ClientConnection, HostPeer));
                            break;
                        case SendMessageCommand send when send.PeerId == HostPeer:
                            _host.Emit(new MessageReceivedEvent(HostConnection, ClientPeer, send.Payload));
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            await Task.WhenAll(_hostPump, _clientPump);
            try { Directory.Delete(_temporaryDirectory, recursive: true); } catch (IOException) { }
            _stopping.Dispose();
        }
    }

    private sealed class ControlledPreparer : IAssetPreparer
    {
        private readonly AssetPreparer _inner = new();
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseFirst { get; } = new(false);

        public AssetPreparationResult Prepare(Guid sessionId, IEnumerable<CardDefinition> usedDefinitions, string sessionDirectory,
            HostConfiguration configuration, CancellationToken cancellationToken = default)
        {
            var definitions = usedDefinitions.ToArray();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                FirstStarted.SetResult();
                ReleaseFirst.Wait(TimeSpan.FromSeconds(5));
            }
            return _inner.Prepare(sessionId, definitions, sessionDirectory, configuration, CancellationToken.None);
        }
    }

    private sealed class FakeSteamTransport : ISteamTransport
    {
        private readonly Channel<SteamTransportCommand> _commands = Channel.CreateUnbounded<SteamTransportCommand>();
        private readonly Channel<SteamTransportEvent> _events = Channel.CreateUnbounded<SteamTransportEvent>();

        public ChannelWriter<SteamTransportCommand> Commands => _commands.Writer;
        public ChannelReader<SteamTransportCommand> CommandsReader => _commands.Reader;
        public ChannelReader<SteamTransportEvent> Events => _events.Reader;
        public Task Completion => Task.CompletedTask;
        public ConcurrentQueue<SteamTransportCommand> SeenCommands { get; } = new();
        public void Emit(SteamTransportEvent value) => _events.Writer.TryWrite(value);
        public ValueTask DisposeAsync()
        {
            _commands.Writer.TryComplete();
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
