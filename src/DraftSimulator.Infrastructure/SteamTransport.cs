using System.Diagnostics;
using System.Threading.Channels;

namespace DraftSimulator.Infrastructure;

public sealed class SteamTransport : ISteamTransport
{
    private const int ApplicationCloseReason = 1000;
    private const int HelloTimeoutCloseReason = 1001;
    private const int VirtualPort = 0;

    private readonly ISteamworksAdapter _steam;
    private readonly SteamTransportOptions _options;
    private readonly ILocalLogger _logger;
    private readonly Channel<SteamTransportCommand> _commands;
    private readonly Channel<SteamTransportEvent> _events;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private readonly HashSet<SteamPeerId> _invitedPeers = [];
    private int _disposeStarted;

    public SteamTransport(ISteamworksAdapter steam, SteamTransportOptions options, ILocalLogger? logger = null)
    {
        _steam = steam ?? throw new ArgumentNullException(nameof(steam));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLocalLogger.Instance;
        if (options.ExpectedAppId == 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The expected Steam AppID must be nonzero.");
        if (options.TickInterval <= TimeSpan.Zero || options.HelloTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Worker timing values must be positive.");

        _commands = Channel.CreateUnbounded<SteamTransportCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _events = Channel.CreateUnbounded<SteamTransportEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Factory.StartNew(
            Run,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public ChannelWriter<SteamTransportCommand> Commands => _commands.Writer;
    public ChannelReader<SteamTransportEvent> Events => _events.Reader;
    public Task Completion => _worker;

    private void Run()
    {
        var state = new SteamTransportState();
        var initialized = false;
        try
        {
            if (!_steam.Initialize(_options.ExpectedAppId, out var localPeerId, out var failure, out var diagnostic))
            {
                Publish(new SteamInitializationFailedEvent(failure, diagnostic));
                _logger.Write("steam.initialization_failed", (int)failure);
                return;
            }

            initialized = true;
            _steam.WarmRelayNetworkAccess();
            Publish(new SteamInitializedEvent(localPeerId));
            _logger.Write("steam.initialized");

            while (!_stopping.IsCancellationRequested)
            {
                var tick = Stopwatch.StartNew();
                while (_commands.Reader.TryRead(out var command))
                    Execute(command, state);

                _steam.RunCallbacks();
                foreach (var adapterEvent in _steam.DrainEvents())
                    Handle(adapterEvent, state);

                foreach (var message in _steam.ReceiveMessages(state.Connections))
                {
                    if (state.TryGetPeer(message.ConnectionId, out var peerId))
                        Publish(new MessageReceivedEvent(message.ConnectionId, peerId, message.Payload));
                }

                foreach (var timeout in state.TakeExpiredHelloTimeouts(DateTimeOffset.UtcNow))
                {
                    Publish(new HelloTimedOutEvent(timeout.ConnectionId, timeout.PeerId));
                    _steam.CloseConnection(timeout.ConnectionId, HelloTimeoutCloseReason, "Hello was not received within five seconds.");
                    state.Remove(timeout.ConnectionId);
                }

                var remaining = _options.TickInterval - tick.Elapsed;
                if (remaining > TimeSpan.Zero)
                    _stopping.Token.WaitHandle.WaitOne(remaining);
            }
        }
        catch (Exception exception)
        {
            Publish(new TransportOperationFailedEvent("Worker", exception.Message));
            _logger.Write("steam.worker_failed");
        }
        finally
        {
            foreach (var connection in state.Connections.ToArray())
                _steam.CloseConnection(connection, ApplicationCloseReason, "Application shutting down.");
            if (state.CurrentLobby is { } lobby)
                _steam.LeaveLobby(lobby);
            if (initialized)
                _steam.Shutdown();
            _steam.Dispose();
            Publish(new SteamShutdownEvent());
            _logger.Write("steam.shutdown");
            _events.Writer.TryComplete();
        }
    }

    private void Execute(SteamTransportCommand command, SteamTransportState state)
    {
        switch (command)
        {
            case CreateLobbyCommand create:
                try
                {
                    _pendingRoomCode = SteamRoomCode.Normalize(create.RoomCode);
                    _steam.CreatePublicLobby(SteamLobbyMetadata.MemberLimit);
                }
                catch (FormatException exception)
                {
                    Publish(new TransportOperationFailedEvent(nameof(CreateLobbyCommand), exception.Message));
                }
                break;

            case FindLobbyCommand find:
                try
                {
                    _steam.RequestLobbyList(SteamLobbyMetadata.SearchFilters(find.RoomCode));
                }
                catch (FormatException exception)
                {
                    Publish(new TransportOperationFailedEvent(nameof(FindLobbyCommand), exception.Message));
                }
                break;

            case JoinLobbyCommand join:
                _steam.JoinLobby(join.LobbyId);
                break;

            case LeaveLobbyCommand:
                if (state.CurrentLobby is { } leaving)
                {
                    _steam.LeaveLobby(leaving);
                    if (state.ListenSocket is { } listenSocket)
                        _steam.CloseListenSocket(listenSocket);
                    state.CurrentLobby = null;
                    state.ListenSocket = null;
                    Publish(new LobbyLeftEvent(leaving));
                }
                break;

            case InviteToLobbyCommand invite:
                if (state.CurrentLobby is not { } inviteLobby || !_steam.InviteUserToLobby(inviteLobby, invite.PeerId))
                    Publish(new TransportOperationFailedEvent(nameof(InviteToLobbyCommand), "No active lobby or Steam rejected the invitation."));
                else
                    _invitedPeers.Add(invite.PeerId);
                break;

            case OpenLobbyInviteOverlayCommand:
                if (state.CurrentLobby is not { } overlayLobby)
                    Publish(new TransportOperationFailedEvent(nameof(OpenLobbyInviteOverlayCommand), "There is no active lobby."));
                else
                    _steam.OpenLobbyInviteOverlay(overlayLobby);
                break;

            case SetLobbyOpenCommand setOpen:
                SetLobbyOpen(state, setOpen.IsOpen);
                break;

            case ConnectPeerCommand connect:
                BindOutgoingConnection(state, connect.PeerId);
                break;

            case AcceptConnectionCommand accept:
                if (!state.TryGetPeer(accept.ConnectionId, out _) || !_steam.AcceptConnection(accept.ConnectionId))
                    Publish(new TransportOperationFailedEvent(nameof(AcceptConnectionCommand), "Steam rejected the connection acceptance."));
                else
                    state.StartHelloTimeout(accept.ConnectionId, DateTimeOffset.UtcNow + _options.HelloTimeout);
                break;

            case RejectConnectionCommand reject:
                _steam.CloseConnection(reject.ConnectionId, ApplicationCloseReason, reject.Diagnostic);
                state.Remove(reject.ConnectionId);
                break;

            case MarkHelloReceivedCommand hello:
                if (!state.MarkHelloReceived(hello.ConnectionId))
                    Publish(new TransportOperationFailedEvent(nameof(MarkHelloReceivedCommand), "No pending Hello timeout exists for the connection."));
                break;

            case SendMessageCommand send:
                if (!state.TryGetConnection(send.PeerId, out var sendConnection))
                    Publish(new TransportOperationFailedEvent(nameof(SendMessageCommand), "The Steam peer is not connected."));
                else if (!_steam.SendReliable(sendConnection, send.Payload.Span, out var sendDiagnostic))
                    Publish(new TransportOperationFailedEvent(nameof(SendMessageCommand), sendDiagnostic));
                break;

            case ClosePeerCommand close:
                if (state.TryGetConnection(close.PeerId, out var closeConnection))
                {
                    _steam.CloseConnection(closeConnection, ApplicationCloseReason, close.Diagnostic);
                    state.Remove(closeConnection);
                }
                break;
        }
    }

    private string? _pendingRoomCode;

    private void Handle(SteamAdapterEvent adapterEvent, SteamTransportState state)
    {
        switch (adapterEvent.Kind)
        {
            case SteamAdapterEventKind.LobbyCreated:
                if (_pendingRoomCode is null)
                {
                    Publish(new LobbyCreationFailedEvent("Steam returned an unsolicited lobby creation result."));
                    return;
                }

                var metadata = SteamLobbyMetadata.Create(_pendingRoomCode);
                var configured = metadata.All(item => _steam.SetLobbyData(adapterEvent.LobbyId, item.Key, item.Value));
                configured &= _steam.SetLobbyJoinable(adapterEvent.LobbyId, true);
                var listenSocket = _steam.CreateListenSocketP2P(VirtualPort);
                if (!configured || listenSocket.Value == 0)
                {
                    if (listenSocket.Value != 0)
                        _steam.CloseListenSocket(listenSocket);
                    _steam.LeaveLobby(adapterEvent.LobbyId);
                    Publish(new LobbyCreationFailedEvent("Steam could not configure the lobby or P2P listen socket."));
                    return;
                }

                state.CurrentLobby = adapterEvent.LobbyId;
                state.ListenSocket = listenSocket;
                Publish(new LobbyCreatedEvent(adapterEvent.LobbyId, _pendingRoomCode));
                _pendingRoomCode = null;
                break;

            case SteamAdapterEventKind.LobbyCreateFailed:
                _pendingRoomCode = null;
                Publish(new LobbyCreationFailedEvent(adapterEvent.Diagnostic));
                break;

            case SteamAdapterEventKind.LobbySearchCompleted:
                Publish(new LobbySearchCompletedEvent(adapterEvent.LobbyIds ?? []));
                break;

            case SteamAdapterEventKind.LobbyEntered:
                state.CurrentLobby = adapterEvent.LobbyId;
                var owner = adapterEvent.PeerId.Value == 0 ? _steam.GetLobbyOwner(adapterEvent.LobbyId) : adapterEvent.PeerId;
                Publish(new LobbyEnteredEvent(adapterEvent.LobbyId, owner));
                BindOutgoingConnection(state, owner);
                break;

            case SteamAdapterEventKind.LobbyJoinFailed:
                Publish(new LobbyJoinFailedEvent(adapterEvent.LobbyId, adapterEvent.Diagnostic));
                break;

            case SteamAdapterEventKind.LobbyInvitationReceived:
                Publish(new LobbyInvitationReceivedEvent(adapterEvent.LobbyId, adapterEvent.PeerId));
                break;

            case SteamAdapterEventKind.LobbyJoinRequested:
                Publish(new LobbyJoinRequestedEvent(adapterEvent.LobbyId, adapterEvent.PeerId));
                break;

            case SteamAdapterEventKind.ConnectionStatusChanged:
                HandleConnectionStatus(adapterEvent, state);
                break;
        }
    }

    private void HandleConnectionStatus(SteamAdapterEvent change, SteamTransportState state)
    {
        if (change.PeerId.Value != 0)
            state.Bind(change.ConnectionId, change.PeerId);

        if (!state.TryGetPeer(change.ConnectionId, out var peerId))
            return;

        switch (change.ConnectionState)
        {
            case SteamConnectionState.Connecting:
                if (change.IsInboundConnection)
                    Publish(new ConnectionRequestedEvent(
                        change.ConnectionId,
                        peerId,
                        state.CurrentLobby is { } lobby && _steam.IsLobbyMember(lobby, peerId),
                        _steam.IsImmediateFriend(peerId),
                        _invitedPeers.Remove(peerId)));
                break;
            case SteamConnectionState.Connected:
                Publish(new PeerConnectedEvent(change.ConnectionId, peerId));
                break;
            case SteamConnectionState.ClosedByPeer:
            case SteamConnectionState.ProblemDetectedLocally:
                state.Remove(change.ConnectionId);
                Publish(new PeerDisconnectedEvent(change.ConnectionId, peerId, change.Diagnostic));
                _steam.CloseConnection(change.ConnectionId, ApplicationCloseReason, change.Diagnostic);
                break;
        }
    }

    private void BindOutgoingConnection(SteamTransportState state, SteamPeerId peerId)
    {
        var connection = _steam.ConnectP2P(peerId, VirtualPort);
        if (connection.Value == 0)
            Publish(new TransportOperationFailedEvent(nameof(ConnectPeerCommand), "Steam could not create the P2P connection."));
        else
            state.Bind(connection, peerId);
    }

    private void SetLobbyOpen(SteamTransportState state, bool isOpen)
    {
        if (state.CurrentLobby is not { } lobby)
        {
            Publish(new TransportOperationFailedEvent(nameof(SetLobbyOpenCommand), "There is no active lobby."));
            return;
        }

        var succeeded = _steam.SetLobbyData(lobby, SteamLobbyMetadata.OpenKey, isOpen ? "1" : "0");
        succeeded &= _steam.SetLobbyJoinable(lobby, isOpen);
        if (succeeded)
            Publish(new LobbyOpenChangedEvent(lobby, isOpen));
        else
            Publish(new TransportOperationFailedEvent(nameof(SetLobbyOpenCommand), "Steam rejected the lobby availability update."));
    }

    private void Publish(SteamTransportEvent transportEvent) => _events.Writer.TryWrite(transportEvent);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            await _worker.ConfigureAwait(false);
            return;
        }

        _commands.Writer.TryComplete();
        _stopping.Cancel();
        await _worker.ConfigureAwait(false);
        _stopping.Dispose();
    }
}
