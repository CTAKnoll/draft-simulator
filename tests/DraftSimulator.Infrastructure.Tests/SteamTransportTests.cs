using System.Collections.Concurrent;
using System.Threading.Channels;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class SteamTransportTests
{
    [Fact]
    public async Task CreatesConfiguresUpdatesInvitesAndLeavesLobby()
    {
        var steam = new FakeSteamworksAdapter();
        await using var transport = CreateTransport(steam);
        await ReadEvent<SteamInitializedEvent>(transport.Events);

        await transport.Commands.WriteAsync(new CreateLobbyCommand("01234-abcde"));
        var created = await ReadEvent<LobbyCreatedEvent>(transport.Events);

        Assert.Equal(new SteamLobbyId(100), created.LobbyId);
        Assert.Equal("01234ABCDE", created.NormalizedRoomCode);
        Assert.Equal(SteamLobbyMetadata.Create("01234ABCDE"), steam.LobbyData);
        Assert.Equal(8, steam.CreatedMemberLimit);
        Assert.Equal(0, steam.CreatedVirtualPort);

        await transport.Commands.WriteAsync(new SetLobbyOpenCommand(false));
        Assert.False((await ReadEvent<LobbyOpenChangedEvent>(transport.Events)).IsOpen);
        Assert.Equal("0", steam.LobbyData[SteamLobbyMetadata.OpenKey]);
        Assert.False(steam.IsJoinable);

        await transport.Commands.WriteAsync(new InviteToLobbyCommand(new SteamPeerId(50)));
        await transport.Commands.WriteAsync(new OpenLobbyInviteOverlayCommand());
        await transport.Commands.WriteAsync(new LeaveLobbyCommand());
        Assert.Equal(new SteamLobbyId(100), (await ReadEvent<LobbyLeftEvent>(transport.Events)).LobbyId);
        Assert.Equal(new SteamPeerId(50), steam.InvitedPeer);
        Assert.True(steam.ListenSocketClosed);
        Assert.Equal(new SteamLobbyId(100), steam.InviteOverlayLobby);
    }

    [Fact]
    public async Task BindsMessagesToSteamPeerAndSurfacesHelloTimeout()
    {
        var steam = new FakeSteamworksAdapter();
        await using var transport = CreateTransport(steam, TimeSpan.FromMilliseconds(20));
        await ReadEvent<SteamInitializedEvent>(transport.Events);
        var connection = new SteamConnectionId(30);
        var peer = new SteamPeerId(40);

        steam.Emit(new SteamAdapterEvent(
            SteamAdapterEventKind.ConnectionStatusChanged,
            PeerId: peer,
            ConnectionId: connection,
            ConnectionState: SteamConnectionState.Connecting,
            IsInboundConnection: true));
        var requested = await ReadEvent<ConnectionRequestedEvent>(transport.Events);
        Assert.Equal(peer, requested.PeerId);
        Assert.False(requested.IsLobbyMember);

        await transport.Commands.WriteAsync(new AcceptConnectionCommand(connection));
        steam.QueueMessage(new SteamReceivedMessage(connection, [1, 2, 3]));
        var received = await ReadEvent<MessageReceivedEvent>(transport.Events);
        Assert.Equal(peer, received.PeerId);
        Assert.Equal([1, 2, 3], received.Payload.ToArray());

        await transport.Commands.WriteAsync(new SendMessageCommand(peer, new byte[] { 4, 5 }));
        await WaitUntil(() => steam.SentPayload is not null);
        Assert.Equal([4, 5], steam.SentPayload);

        var timedOut = await ReadEvent<HelloTimedOutEvent>(transport.Events);
        Assert.Equal(peer, timedOut.PeerId);
        Assert.Contains(connection, steam.ClosedConnections);
    }

    [Fact]
    public async Task ConnectionRequestReportsImmediateFriendAndExplicitInviteAuthentication()
    {
        var steam = new FakeSteamworksAdapter { LobbyContainsPeer = true };
        await using var transport = CreateTransport(steam);
        await ReadEvent<SteamInitializedEvent>(transport.Events);
        await transport.Commands.WriteAsync(new CreateLobbyCommand("01234-abcde"));
        await ReadEvent<LobbyCreatedEvent>(transport.Events);
        await transport.Commands.WriteAsync(new InviteToLobbyCommand(new SteamPeerId(50)));
        await WaitUntil(() => steam.InvitedPeer == new SteamPeerId(50));

        steam.Emit(ConnectionRequest(new SteamPeerId(40), new SteamConnectionId(40)));
        var friend = await ReadEvent<ConnectionRequestedEvent>(transport.Events);
        steam.Emit(ConnectionRequest(new SteamPeerId(50), new SteamConnectionId(50)));
        var invited = await ReadEvent<ConnectionRequestedEvent>(transport.Events);

        Assert.True(friend.IsLobbyMember && friend.IsImmediateFriend);
        Assert.False(friend.WasExplicitlyInvited);
        Assert.True(invited.IsLobbyMember && invited.WasExplicitlyInvited);
        Assert.False(invited.IsImmediateFriend);
    }

    private static SteamAdapterEvent ConnectionRequest(SteamPeerId peer, SteamConnectionId connection) => new(
        SteamAdapterEventKind.ConnectionStatusChanged, PeerId: peer, ConnectionId: connection,
        ConnectionState: SteamConnectionState.Connecting, IsInboundConnection: true);

    private static SteamTransport CreateTransport(FakeSteamworksAdapter steam, TimeSpan? helloTimeout = null) =>
        new(steam, new SteamTransportOptions(480)
        {
            TickInterval = TimeSpan.FromMilliseconds(1),
            HelloTimeout = helloTimeout ?? TimeSpan.FromSeconds(5),
        });

    private static async Task<T> ReadEvent<T>(ChannelReader<SteamTransportEvent> reader) where T : SteamTransportEvent
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await reader.WaitToReadAsync(timeout.Token))
        {
            while (reader.TryRead(out var transportEvent))
            {
                if (transportEvent is T expected)
                    return expected;
            }
        }
        throw new InvalidOperationException($"Transport completed before publishing {typeof(T).Name}.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(1, timeout.Token);
    }

    private sealed class FakeSteamworksAdapter : ISteamworksAdapter
    {
        private readonly ConcurrentQueue<SteamAdapterEvent> _events = new();
        private readonly ConcurrentQueue<SteamReceivedMessage> _messages = new();

        public int CreatedMemberLimit { get; private set; }
        public int CreatedVirtualPort { get; private set; } = -1;
        public Dictionary<string, string> LobbyData { get; } = new(StringComparer.Ordinal);
        public bool IsJoinable { get; private set; }
        public SteamPeerId InvitedPeer { get; private set; }
        public bool ListenSocketClosed { get; private set; }
        public SteamLobbyId InviteOverlayLobby { get; private set; }
        public byte[]? SentPayload { get; private set; }
        public bool LobbyContainsPeer { get; init; }
        public ConcurrentBag<SteamConnectionId> ClosedConnections { get; } = [];

        public bool Initialize(uint expectedAppId, out SteamPeerId localPeerId, out SteamInitializationFailure failure, out string diagnostic)
        {
            localPeerId = new SteamPeerId(1);
            failure = default;
            diagnostic = string.Empty;
            return true;
        }

        public void Emit(SteamAdapterEvent adapterEvent) => _events.Enqueue(adapterEvent);
        public void QueueMessage(SteamReceivedMessage message) => _messages.Enqueue(message);
        public void WarmRelayNetworkAccess() { }
        public void RunCallbacks() { }
        public IReadOnlyList<SteamAdapterEvent> DrainEvents()
        {
            var events = new List<SteamAdapterEvent>();
            while (_events.TryDequeue(out var item)) events.Add(item);
            return events;
        }
        public IReadOnlyList<SteamReceivedMessage> ReceiveMessages(IReadOnlyCollection<SteamConnectionId> connections)
        {
            var messages = new List<SteamReceivedMessage>();
            while (_messages.TryDequeue(out var item)) messages.Add(item);
            return messages;
        }
        public void CreatePublicLobby(int memberLimit)
        {
            CreatedMemberLimit = memberLimit;
            Emit(new SteamAdapterEvent(SteamAdapterEventKind.LobbyCreated, new SteamLobbyId(100)));
        }
        public bool SetLobbyData(SteamLobbyId lobbyId, string key, string value) { LobbyData[key] = value; return true; }
        public bool SetLobbyJoinable(SteamLobbyId lobbyId, bool joinable) { IsJoinable = joinable; return true; }
        public void RequestLobbyList(IReadOnlyDictionary<string, string> exactFilters) { }
        public void JoinLobby(SteamLobbyId lobbyId) { }
        public SteamPeerId GetLobbyOwner(SteamLobbyId lobbyId) => new(2);
        public bool IsLobbyMember(SteamLobbyId lobbyId, SteamPeerId peerId) => LobbyContainsPeer;
        public bool IsImmediateFriend(SteamPeerId peerId) => peerId == new SteamPeerId(40);
        public void LeaveLobby(SteamLobbyId lobbyId) { }
        public bool InviteUserToLobby(SteamLobbyId lobbyId, SteamPeerId peerId) { InvitedPeer = peerId; return true; }
        public void OpenLobbyInviteOverlay(SteamLobbyId lobbyId) => InviteOverlayLobby = lobbyId;
        public SteamListenSocketId CreateListenSocketP2P(int virtualPort) { CreatedVirtualPort = virtualPort; return new(10); }
        public bool CloseListenSocket(SteamListenSocketId listenSocketId) { ListenSocketClosed = true; return true; }
        public SteamConnectionId ConnectP2P(SteamPeerId peerId, int virtualPort) => new(20);
        public bool AcceptConnection(SteamConnectionId connectionId) => true;
        public bool CloseConnection(SteamConnectionId connectionId, int reason, string diagnostic) { ClosedConnections.Add(connectionId); return true; }
        public bool SendReliable(SteamConnectionId connectionId, ReadOnlySpan<byte> payload, out string diagnostic)
        {
            SentPayload = payload.ToArray();
            diagnostic = string.Empty;
            return true;
        }
        public void Shutdown() { }
        public void Dispose() { }
    }
}
