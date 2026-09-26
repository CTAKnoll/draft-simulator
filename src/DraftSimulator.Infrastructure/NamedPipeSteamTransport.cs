using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace DraftSimulator.Infrastructure;

public sealed record NamedPipeSteamTransportOptions(string SharedDirectory, SteamPeerId LocalPeerId)
{
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan RegistryStaleAfter { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>A machine-local ISteamTransport implementation backed by named pipes and a shared lobby registry.</summary>
public sealed class NamedPipeSteamTransport : ISteamTransport
{
    private const int MaxRemotePeers = 7;
    private const int MaxFrameLength = 16 * 1024 * 1024;
    private const byte ConnectFrame = 1;
    private const byte AcceptedFrame = 2;
    private const byte RejectedFrame = 3;
    private const byte MessageFrame = 4;
    private const byte CloseFrame = 5;

    private readonly NamedPipeSteamTransportOptions _options;
    private readonly string _directory;
    private readonly string _pipePrefix;
    private readonly Channel<SteamTransportCommand> _commands;
    private readonly Channel<SteamTransportEvent> _events;
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<SteamConnectionId, Connection> _connections = new();
    private readonly ConcurrentDictionary<SteamPeerId, SteamConnectionId> _connectionsByPeer = new();
    private readonly ConcurrentBag<Task> _backgroundTasks = [];
    private readonly Task _worker;
    private readonly long _processStartedUtcTicks;
    private LobbyRecord? _currentLobby;
    private uint _nextConnectionId;
    private int _inboundConnectionCount;
    private int _disposeStarted;

    public NamedPipeSteamTransport(NamedPipeSteamTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.SharedDirectory))
            throw new ArgumentException("A shared lobby registry directory is required.", nameof(options));
        if (options.LocalPeerId.Value == 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The local peer ID must be nonzero.");
        if (options.HelloTimeout <= TimeSpan.Zero || options.ConnectTimeout <= TimeSpan.Zero || options.RegistryStaleAfter <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Timeouts must be positive.");

        _options = options;
        _directory = Path.GetFullPath(options.SharedDirectory);
        var directoryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_directory)))[..16];
        _pipePrefix = $"draftsim-{directoryHash.ToLowerInvariant()}";
        _processStartedUtcTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        _commands = Channel.CreateUnbounded<SteamTransportCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _events = Channel.CreateUnbounded<SteamTransportEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(RunAsync);
    }

    public ChannelWriter<SteamTransportCommand> Commands => _commands.Writer;
    public ChannelReader<SteamTransportEvent> Events => _events.Reader;
    public Task Completion => _worker;

    private async Task RunAsync()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            for (var index = 0; index < MaxRemotePeers; index++)
                Track(AcceptLoopAsync(_stopping.Token));

            Publish(new SteamInitializedEvent(_options.LocalPeerId));
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            var commands = ProcessCommandsAsync(_stopping.Token);
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                ExpireHelloDeadlines();
                if (_currentLobby is { OwnerId: var owner } lobby && owner == _options.LocalPeerId &&
                    DateTime.UtcNow.Ticks - lobby.UpdatedUtcTicks >= TimeSpan.FromSeconds(2).Ticks)
                {
                    _currentLobby = lobby with { UpdatedUtcTicks = DateTime.UtcNow.Ticks };
                    TryWriteLobby(_currentLobby);
                }
            }
            await commands.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Publish(new TransportOperationFailedEvent("Worker", exception.Message));
        }
        finally
        {
            RemoveOwnedLobby();
            foreach (var connection in _connections.Values.ToArray())
                await CloseConnectionAsync(connection, "Transport is shutting down.", notifyRemote: true).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_backgroundTasks.ToArray()).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            catch (Exception exception)
            {
                if (!_stopping.IsCancellationRequested)
                    Publish(new TransportOperationFailedEvent("BackgroundWorker", exception.Message));
            }
            Publish(new SteamShutdownEvent());
            _events.Writer.TryComplete();
        }
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in _commands.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            switch (command)
            {
                case CreateLobbyCommand create:
                    CreateLobby(create);
                    break;
                case FindLobbyCommand find:
                    FindLobby(find);
                    break;
                case JoinLobbyCommand join:
                    JoinLobby(join);
                    break;
                case LeaveLobbyCommand:
                    LeaveLobby();
                    break;
                case SetLobbyOpenCommand open:
                    SetLobbyOpen(open.IsOpen);
                    break;
                case ConnectPeerCommand connect:
                    Track(ConnectPeerAsync(connect.PeerId, _currentLobby?.LobbyId ?? default, cancellationToken));
                    break;
                case AcceptConnectionCommand accept:
                    Track(AcceptConnectionAsync(accept.ConnectionId));
                    break;
                case RejectConnectionCommand reject:
                    Track(RejectConnectionAsync(reject.ConnectionId, reject.Diagnostic));
                    break;
                case MarkHelloReceivedCommand hello:
                    if (!_connections.TryGetValue(hello.ConnectionId, out var helloConnection) ||
                        Interlocked.Exchange(ref helloConnection.AwaitingHello, 0) == 0)
                        Failed(nameof(MarkHelloReceivedCommand), "No pending Hello timeout exists for the connection.");
                    break;
                case SendMessageCommand send:
                    if (!_connectionsByPeer.TryGetValue(send.PeerId, out var sendId) ||
                        !_connections.TryGetValue(sendId, out var sendConnection) || !sendConnection.Connected)
                        Failed(nameof(SendMessageCommand), "The peer is not connected.");
                    else
                        Track(SendMessageAsync(sendConnection, send.Payload));
                    break;
                case ClosePeerCommand close:
                    if (_connectionsByPeer.TryGetValue(close.PeerId, out var closeId) && _connections.TryGetValue(closeId, out var closeConnection))
                        Track(CloseConnectionAsync(closeConnection, close.Diagnostic, notifyRemote: true));
                    break;
                case InviteToLobbyCommand:
                case OpenLobbyInviteOverlayCommand:
                    Failed(command.GetType().Name, "Steam invitations are unavailable on the machine-local transport.");
                    break;
            }
        }
    }

    private void CreateLobby(CreateLobbyCommand command)
    {
        string roomCode;
        try
        {
            roomCode = SteamRoomCode.Normalize(command.RoomCode);
        }
        catch (FormatException exception)
        {
            Failed(nameof(CreateLobbyCommand), exception.Message);
            return;
        }

        RemoveOwnedLobby();
        _currentLobby = null;
        SteamLobbyId lobbyId;
        do
        {
            lobbyId = new SteamLobbyId(BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(sizeof(ulong))));
        } while (lobbyId.Value == 0 || File.Exists(LobbyPath(lobbyId)));

        var now = DateTime.UtcNow.Ticks;
        var lobby = new LobbyRecord(1, lobbyId, _options.LocalPeerId, roomCode, true, now,
            Environment.ProcessId, _processStartedUtcTicks);
        try
        {
            WriteLobby(lobby);
            _currentLobby = lobby;
            Publish(new LobbyCreatedEvent(lobbyId, roomCode));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Publish(new LobbyCreationFailedEvent(exception.Message));
        }
    }

    private void FindLobby(FindLobbyCommand command)
    {
        string roomCode;
        try
        {
            roomCode = SteamRoomCode.Normalize(command.RoomCode);
        }
        catch (FormatException exception)
        {
            Failed(nameof(FindLobbyCommand), exception.Message);
            return;
        }

        var matches = ReadLobbies()
            .Where(lobby => lobby.IsOpen && string.Equals(lobby.RoomCode, roomCode, StringComparison.Ordinal))
            .Select(lobby => lobby.LobbyId)
            .ToArray();
        Publish(new LobbySearchCompletedEvent(matches));
    }

    private void JoinLobby(JoinLobbyCommand command)
    {
        var lobby = TryReadLobby(LobbyPath(command.LobbyId));
        if (lobby is null || !IsLive(lobby) || !lobby.IsOpen)
        {
            Publish(new LobbyJoinFailedEvent(command.LobbyId, "The lobby does not exist, is stale, or is closed."));
            return;
        }

        _currentLobby = lobby;
        Publish(new LobbyEnteredEvent(lobby.LobbyId, lobby.OwnerId));
        Track(ConnectPeerAsync(lobby.OwnerId, lobby.LobbyId, _stopping.Token));
    }

    private void LeaveLobby()
    {
        if (_currentLobby is not { } lobby)
            return;
        RemoveOwnedLobby();
        _currentLobby = null;
        Publish(new LobbyLeftEvent(lobby.LobbyId));
    }

    private void SetLobbyOpen(bool isOpen)
    {
        if (_currentLobby is not { } lobby || lobby.OwnerId != _options.LocalPeerId)
        {
            Failed(nameof(SetLobbyOpenCommand), "There is no locally owned lobby.");
            return;
        }
        lobby = lobby with { IsOpen = isOpen, UpdatedUtcTicks = DateTime.UtcNow.Ticks };
        try
        {
            WriteLobby(lobby);
            _currentLobby = lobby;
            Publish(new LobbyOpenChangedEvent(lobby.LobbyId, isOpen));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Failed(nameof(SetLobbyOpenCommand), exception.Message);
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(PipeName(_options.LocalPeerId), PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                Track(HandleInboundAsync(server, cancellationToken));
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private async Task HandleInboundAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            var frame = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (frame.Type != ConnectFrame || frame.Payload.Length != 16)
                throw new InvalidDataException("The peer sent an invalid connection handshake.");
            var peerId = new SteamPeerId(BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload));
            var lobbyId = new SteamLobbyId(BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(8)));
            if (peerId.Value == 0 || peerId == _options.LocalPeerId)
                throw new InvalidDataException("The peer ID in the connection handshake is invalid.");
            if (Interlocked.Increment(ref _inboundConnectionCount) > MaxRemotePeers)
            {
                Interlocked.Decrement(ref _inboundConnectionCount);
                await WriteFrameAsync(pipe, RejectedFrame, Encoding.UTF8.GetBytes("The host is full."), cancellationToken).ConfigureAwait(false);
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }

            Connection connection;
            try
            {
                connection = AddConnection(peerId, pipe, inbound: true);
            }
            catch
            {
                Interlocked.Decrement(ref _inboundConnectionCount);
                throw;
            }
            var isLobbyMember = _currentLobby is { OwnerId: var owner, LobbyId: var current } &&
                owner == _options.LocalPeerId && current == lobbyId;
            Publish(new ConnectionRequestedEvent(connection.Id, peerId, isLobbyMember));
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or InvalidDataException or OperationCanceledException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ConnectPeerAsync(SteamPeerId peerId, SteamLobbyId lobbyId, CancellationToken cancellationToken)
    {
        if (peerId.Value == 0 || peerId == _options.LocalPeerId)
        {
            Failed(nameof(ConnectPeerCommand), "The remote peer ID must be nonzero and different from the local peer ID.");
            return;
        }
        if (_connectionsByPeer.TryGetValue(peerId, out var existingId) && _connections.TryGetValue(existingId, out var existing) && existing.Connected)
            return;

        var pipe = new NamedPipeClientStream(".", PipeName(peerId), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var handshake = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(handshake, _options.LocalPeerId.Value);
            BinaryPrimitives.WriteUInt64LittleEndian(handshake.AsSpan(8), lobbyId.Value);
            await WriteFrameAsync(pipe, ConnectFrame, handshake, timeout.Token).ConfigureAwait(false);
            var response = await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (response.Type == RejectedFrame)
            {
                Failed(nameof(ConnectPeerCommand), DecodeDiagnostic(response.Payload, "The connection was rejected."));
                await pipe.DisposeAsync().ConfigureAwait(false);
                return;
            }
            if (response.Type != AcceptedFrame)
                throw new InvalidDataException("The peer sent an invalid connection response.");

            var connection = AddConnection(peerId, pipe, inbound: false);
            connection.Connected = true;
            Publish(new PeerConnectedEvent(connection.Id, peerId));
            Track(ReadLoopAsync(connection));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or InvalidDataException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (!_stopping.IsCancellationRequested)
                Failed(nameof(ConnectPeerCommand), exception is OperationCanceledException ? "The connection attempt timed out." : exception.Message);
        }
    }

    private async Task AcceptConnectionAsync(SteamConnectionId connectionId)
    {
        if (!_connections.TryGetValue(connectionId, out var connection) || !connection.Inbound || connection.Connected)
        {
            Failed(nameof(AcceptConnectionCommand), "The inbound connection request does not exist.");
            return;
        }
        try
        {
            await WriteFrameAsync(connection.Pipe, AcceptedFrame, ReadOnlyMemory<byte>.Empty, _stopping.Token, connection.WriteLock).ConfigureAwait(false);
            connection.Connected = true;
            StartHelloTimeout(connection);
            Publish(new PeerConnectedEvent(connection.Id, connection.PeerId));
            Track(ReadLoopAsync(connection));
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            await DisconnectAsync(connection, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task RejectConnectionAsync(SteamConnectionId connectionId, string diagnostic)
    {
        if (!_connections.TryGetValue(connectionId, out var connection) || connection.Connected)
        {
            Failed(nameof(RejectConnectionCommand), "The inbound connection request does not exist.");
            return;
        }
        try
        {
            await WriteFrameAsync(connection.Pipe, RejectedFrame, Encoding.UTF8.GetBytes(diagnostic), _stopping.Token, connection.WriteLock).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
        }
        RemoveConnection(connection);
        await connection.Pipe.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Connection connection)
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(connection.Pipe, _stopping.Token).ConfigureAwait(false);
                if (frame.Type == MessageFrame)
                    Publish(new MessageReceivedEvent(connection.Id, connection.PeerId, frame.Payload));
                else if (frame.Type == CloseFrame)
                {
                    await DisconnectAsync(connection, DecodeDiagnostic(frame.Payload, "The peer closed the connection.")).ConfigureAwait(false);
                    return;
                }
                else
                    throw new InvalidDataException("The peer sent an unexpected frame.");
            }
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException or InvalidDataException or OperationCanceledException)
        {
            if (!_stopping.IsCancellationRequested)
                await DisconnectAsync(connection, exception is EndOfStreamException ? "The peer closed the pipe." : exception.Message).ConfigureAwait(false);
        }
    }

    private async Task SendMessageAsync(Connection connection, ReadOnlyMemory<byte> payload)
    {
        try
        {
            await WriteFrameAsync(connection.Pipe, MessageFrame, payload, _stopping.Token, connection.WriteLock).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ArgumentOutOfRangeException)
        {
            Failed(nameof(SendMessageCommand), exception.Message);
            await DisconnectAsync(connection, exception.Message).ConfigureAwait(false);
        }
    }

    private async Task CloseConnectionAsync(Connection connection, string diagnostic, bool notifyRemote)
    {
        if (!RemoveConnection(connection))
            return;
        if (notifyRemote)
        {
            try
            {
                using var timeout = new CancellationTokenSource(_options.ConnectTimeout);
                await WriteFrameAsync(connection.Pipe, CloseFrame, Encoding.UTF8.GetBytes(diagnostic), timeout.Token, connection.WriteLock).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }
        await connection.Pipe.DisposeAsync().ConfigureAwait(false);
        if (connection.Connected)
            Publish(new PeerDisconnectedEvent(connection.Id, connection.PeerId, diagnostic));
    }

    private async Task DisconnectAsync(Connection connection, string diagnostic) =>
        await CloseConnectionAsync(connection, diagnostic, notifyRemote: false).ConfigureAwait(false);

    private void ExpireHelloDeadlines()
    {
        var now = DateTime.UtcNow.Ticks;
        foreach (var connection in _connections.Values)
        {
            if (Volatile.Read(ref connection.AwaitingHello) != 0 && connection.HelloDeadlineUtcTicks <= now &&
                Interlocked.Exchange(ref connection.AwaitingHello, 0) != 0)
            {
                Publish(new HelloTimedOutEvent(connection.Id, connection.PeerId));
                Track(CloseConnectionAsync(connection, "Hello was not received before the timeout.", notifyRemote: true));
            }
        }
    }

    private void StartHelloTimeout(Connection connection)
    {
        connection.HelloDeadlineUtcTicks = DateTime.UtcNow.Add(_options.HelloTimeout).Ticks;
        Volatile.Write(ref connection.AwaitingHello, 1);
    }

    private Connection AddConnection(SteamPeerId peerId, PipeStream pipe, bool inbound)
    {
        var id = NextConnectionId();
        var connection = new Connection(id, peerId, pipe, inbound);
        if (_connectionsByPeer.TryGetValue(peerId, out var previousId) && _connections.TryGetValue(previousId, out var previous))
            Track(CloseConnectionAsync(previous, "A replacement connection was established.", notifyRemote: true));
        _connections[id] = connection;
        _connectionsByPeer[peerId] = id;
        return connection;
    }

    private bool RemoveConnection(Connection connection)
    {
        if (!_connections.TryRemove(connection.Id, out _))
            return false;
        _connectionsByPeer.TryRemove(new KeyValuePair<SteamPeerId, SteamConnectionId>(connection.PeerId, connection.Id));
        if (connection.Inbound)
            Interlocked.Decrement(ref _inboundConnectionCount);
        Volatile.Write(ref connection.AwaitingHello, 0);
        return true;
    }

    private SteamConnectionId NextConnectionId()
    {
        uint value;
        do value = Interlocked.Increment(ref _nextConnectionId); while (value == 0);
        return new SteamConnectionId(value);
    }

    private IEnumerable<LobbyRecord> ReadLobbies()
    {
        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(_directory, "lobby-*.json").ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var path in paths)
        {
            var lobby = TryReadLobby(path);
            if (lobby is not null && IsLive(lobby))
                yield return lobby;
            else
                TryDelete(path);
        }
    }

    private LobbyRecord? TryReadLobby(string path)
    {
        try
        {
            var lobby = JsonSerializer.Deserialize<LobbyRecord>(File.ReadAllBytes(path));
            return lobby is { Version: 1 } && lobby.LobbyId.Value != 0 && lobby.OwnerId.Value != 0 &&
                string.Equals(Path.GetFileName(path), Path.GetFileName(LobbyPath(lobby.LobbyId)), StringComparison.Ordinal)
                ? lobby : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private bool IsLive(LobbyRecord lobby)
    {
        if (DateTime.UtcNow.Ticks - lobby.UpdatedUtcTicks <= _options.RegistryStaleAfter.Ticks)
            return true;
        try
        {
            using var process = Process.GetProcessById(lobby.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == lobby.ProcessStartedUtcTicks && !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void WriteLobby(LobbyRecord lobby)
    {
        var path = LobbyPath(lobby.LobbyId);
        var temporary = path + $".{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(lobby));
            File.Move(temporary, path, true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private void TryWriteLobby(LobbyRecord? lobby)
    {
        if (lobby is null)
            return;
        try
        {
            WriteLobby(lobby);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Failed("RegistryHeartbeat", exception.Message);
        }
    }

    private void RemoveOwnedLobby()
    {
        if (_currentLobby is not { OwnerId: var owner } lobby || owner != _options.LocalPeerId)
            return;
        var path = LobbyPath(lobby.LobbyId);
        var stored = TryReadLobby(path);
        if (stored is not null && stored.OwnerId == _options.LocalPeerId && stored.ProcessStartedUtcTicks == _processStartedUtcTicks)
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string LobbyPath(SteamLobbyId lobbyId) => Path.Combine(_directory, $"lobby-{lobbyId.Value:x16}.json");
    private string PipeName(SteamPeerId peerId) => $"{_pipePrefix}-{peerId.Value:x16}";

    private static async Task<(byte Type, byte[] Payload)> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[5];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length < 0 || length > MaxFrameLength)
            throw new InvalidDataException("The named-pipe frame length is invalid.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return (header[0], payload);
    }

    private static async Task WriteFrameAsync(Stream stream, byte type, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken, SemaphoreSlim? writeLock = null)
    {
        if (payload.Length > MaxFrameLength)
            throw new ArgumentOutOfRangeException(nameof(payload), $"Messages cannot exceed {MaxFrameLength} bytes.");
        if (writeLock is not null)
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var header = new byte[5];
            header[0] = type;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), payload.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock?.Release();
        }
    }

    private static string DecodeDiagnostic(byte[] payload, string fallback) =>
        payload.Length == 0 ? fallback : Encoding.UTF8.GetString(payload);

    private void Track(Task task) => _backgroundTasks.Add(task);
    private void Publish(SteamTransportEvent transportEvent) => _events.Writer.TryWrite(transportEvent);
    private void Failed(string operation, string diagnostic) => Publish(new TransportOperationFailedEvent(operation, diagnostic));

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

    private sealed record LobbyRecord(int Version, SteamLobbyId LobbyId, SteamPeerId OwnerId, string RoomCode,
        bool IsOpen, long UpdatedUtcTicks, int ProcessId, long ProcessStartedUtcTicks);

    private sealed class Connection(SteamConnectionId id, SteamPeerId peerId, PipeStream pipe, bool inbound)
    {
        public SteamConnectionId Id { get; } = id;
        public SteamPeerId PeerId { get; } = peerId;
        public PipeStream Pipe { get; } = pipe;
        public bool Inbound { get; } = inbound;
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public bool Connected { get; set; }
        public int AwaitingHello;
        public long HelloDeadlineUtcTicks;
    }
}
