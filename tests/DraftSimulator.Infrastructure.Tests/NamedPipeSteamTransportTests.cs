using System.Text.Json;
using System.Threading.Channels;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class NamedPipeSteamTransportTests
{
    [Fact]
    public async Task CreatesFindsJoinsAndExchangesFramedMessages()
    {
        using var directory = new TemporaryDirectory();
        await using var host = Create(directory.Path, 101);
        await using var client = Create(directory.Path, 202);
        Assert.Equal(new SteamPeerId(101), (await Read<SteamInitializedEvent>(host)).LocalPeerId);
        await Read<SteamInitializedEvent>(client);

        await host.Commands.WriteAsync(new CreateLobbyCommand("01234-abcde"));
        var created = await Read<LobbyCreatedEvent>(host);
        Assert.NotEqual(0UL, created.LobbyId.Value);
        Assert.Equal("01234ABCDE", created.NormalizedRoomCode);

        await client.Commands.WriteAsync(new FindLobbyCommand("01234 ABCDE"));
        Assert.Equal([created.LobbyId], (await Read<LobbySearchCompletedEvent>(client)).LobbyIds);
        await client.Commands.WriteAsync(new JoinLobbyCommand(created.LobbyId));
        var entered = await Read<LobbyEnteredEvent>(client);
        Assert.Equal(new SteamPeerId(101), entered.OwnerId);

        var requested = await Read<ConnectionRequestedEvent>(host);
        Assert.Equal(new SteamPeerId(202), requested.PeerId);
        Assert.True(requested.IsLobbyMember);
        await host.Commands.WriteAsync(new AcceptConnectionCommand(requested.ConnectionId));
        var hostConnected = await Read<PeerConnectedEvent>(host);
        var clientConnected = await Read<PeerConnectedEvent>(client);

        await host.Commands.WriteAsync(new MarkHelloReceivedCommand(hostConnected.ConnectionId));
        await client.Commands.WriteAsync(new SendMessageCommand(new SteamPeerId(101), new byte[] { 1, 2, 3 }));
        var hostMessage = await Read<MessageReceivedEvent>(host);
        Assert.Equal(new SteamPeerId(202), hostMessage.PeerId);
        Assert.Equal([1, 2, 3], hostMessage.Payload.ToArray());

        await host.Commands.WriteAsync(new SendMessageCommand(new SteamPeerId(202), new byte[] { 4, 5 }));
        Assert.Equal([4, 5], (await Read<MessageReceivedEvent>(client)).Payload.ToArray());
    }

    [Fact]
    public async Task ClosedLobbyIsHiddenAndCannotBeJoinedButOwnerAllowsDirectReconnect()
    {
        using var directory = new TemporaryDirectory();
        await using var host = Create(directory.Path, 301);
        await using var client = Create(directory.Path, 302);
        await Read<SteamInitializedEvent>(host);
        await Read<SteamInitializedEvent>(client);
        await host.Commands.WriteAsync(new CreateLobbyCommand("11111-22222"));
        var lobby = (await Read<LobbyCreatedEvent>(host)).LobbyId;
        await host.Commands.WriteAsync(new SetLobbyOpenCommand(false));
        Assert.False((await Read<LobbyOpenChangedEvent>(host)).IsOpen);

        await client.Commands.WriteAsync(new FindLobbyCommand("1111122222"));
        Assert.Empty((await Read<LobbySearchCompletedEvent>(client)).LobbyIds);
        await client.Commands.WriteAsync(new JoinLobbyCommand(lobby));
        await Read<LobbyJoinFailedEvent>(client);

        await client.Commands.WriteAsync(new ConnectPeerCommand(new SteamPeerId(301)));
        var request = await Read<ConnectionRequestedEvent>(host);
        Assert.False(request.IsLobbyMember);
        await host.Commands.WriteAsync(new AcceptConnectionCommand(request.ConnectionId));
        Assert.Equal(new SteamPeerId(301), (await Read<PeerConnectedEvent>(client)).PeerId);
    }

    [Fact]
    public async Task RejectDoesNotConnectAndReportsFailureToCaller()
    {
        using var directory = new TemporaryDirectory();
        await using var host = Create(directory.Path, 401);
        await using var client = Create(directory.Path, 402);
        await Read<SteamInitializedEvent>(host);
        await Read<SteamInitializedEvent>(client);

        await client.Commands.WriteAsync(new ConnectPeerCommand(new SteamPeerId(401)));
        var request = await Read<ConnectionRequestedEvent>(host);
        await host.Commands.WriteAsync(new RejectConnectionCommand(request.ConnectionId, "not allowed"));
        var failed = await Read<TransportOperationFailedEvent>(client);
        Assert.Equal(nameof(ConnectPeerCommand), failed.Operation);
        Assert.Equal("not allowed", failed.Diagnostic);
    }

    [Fact]
    public async Task AcceptedConnectionTimesOutUnlessHelloIsMarked()
    {
        using var directory = new TemporaryDirectory();
        await using var host = Create(directory.Path, 501, TimeSpan.FromMilliseconds(100));
        await using var client = Create(directory.Path, 502, TimeSpan.FromSeconds(2));
        await Read<SteamInitializedEvent>(host);
        await Read<SteamInitializedEvent>(client);
        await client.Commands.WriteAsync(new ConnectPeerCommand(new SteamPeerId(501)));
        var request = await Read<ConnectionRequestedEvent>(host);
        await host.Commands.WriteAsync(new AcceptConnectionCommand(request.ConnectionId));
        await Read<PeerConnectedEvent>(host);
        await Read<PeerConnectedEvent>(client);

        var timedOut = await Read<HelloTimedOutEvent>(host);
        Assert.Equal(request.ConnectionId, timedOut.ConnectionId);
        Assert.Equal(new SteamPeerId(502), (await Read<PeerDisconnectedEvent>(host)).PeerId);
        await Read<PeerDisconnectedEvent>(client);
    }

    [Fact]
    public async Task ClosePeerDisconnectsBothEndsAndAllowsReconnect()
    {
        using var directory = new TemporaryDirectory();
        await using var host = Create(directory.Path, 601);
        await using var client = Create(directory.Path, 602);
        await Read<SteamInitializedEvent>(host);
        await Read<SteamInitializedEvent>(client);
        var first = await Connect(host, client, 601);

        await host.Commands.WriteAsync(new ClosePeerCommand(new SteamPeerId(602), "restart"));
        Assert.Equal("restart", (await Read<PeerDisconnectedEvent>(host)).Diagnostic);
        Assert.Equal("restart", (await Read<PeerDisconnectedEvent>(client)).Diagnostic);

        await client.Commands.WriteAsync(new ConnectPeerCommand(new SteamPeerId(601)));
        var secondRequest = await Read<ConnectionRequestedEvent>(host);
        Assert.NotEqual(first, secondRequest.ConnectionId);
        await host.Commands.WriteAsync(new AcceptConnectionCommand(secondRequest.ConnectionId));
        await Read<PeerConnectedEvent>(host);
        await Read<PeerConnectedEvent>(client);
    }

    [Fact]
    public async Task IgnoresCorruptAndStaleRegistryEntriesAndRemovesOwnedEntryOnDispose()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory.Path, "lobby-0000000000000001.json"), "not json");
        var stalePath = System.IO.Path.Combine(directory.Path, "lobby-0000000000000002.json");
        await File.WriteAllTextAsync(stalePath, JsonSerializer.Serialize(new
        {
            Version = 1,
            LobbyId = new { Value = 2UL },
            OwnerId = new { Value = 999UL },
            RoomCode = "01234ABCDE",
            IsOpen = true,
            UpdatedUtcTicks = DateTime.UtcNow.AddDays(-1).Ticks,
            ProcessId = int.MaxValue,
            ProcessStartedUtcTicks = 1L,
        }));
        SteamLobbyId owned;
        await using (var host = Create(directory.Path, 701, staleAfter: TimeSpan.FromMilliseconds(10)))
        {
            await Read<SteamInitializedEvent>(host);
            await host.Commands.WriteAsync(new FindLobbyCommand("01234ABCDE"));
            Assert.Empty((await Read<LobbySearchCompletedEvent>(host)).LobbyIds);
            await host.Commands.WriteAsync(new CreateLobbyCommand("01234ABCDE"));
            owned = (await Read<LobbyCreatedEvent>(host)).LobbyId;
            Assert.True(File.Exists(LobbyPath(directory.Path, owned)));
        }
        Assert.False(File.Exists(LobbyPath(directory.Path, owned)));
        Assert.False(File.Exists(stalePath));
    }

    [Fact]
    public async Task HostAcceptsSevenSimultaneousRemotePeers()
    {
        using var directory = new TemporaryDirectory();
        await using var host = Create(directory.Path, 801);
        await Read<SteamInitializedEvent>(host);
        var clients = Enumerable.Range(0, 7).Select(index => Create(directory.Path, (ulong)(900 + index))).ToArray();
        try
        {
            foreach (var client in clients)
                await Read<SteamInitializedEvent>(client);
            foreach (var client in clients)
                await client.Commands.WriteAsync(new ConnectPeerCommand(new SteamPeerId(801)));
            var requests = new ConnectionRequestedEvent[clients.Length];
            for (var index = 0; index < clients.Length; index++)
                requests[index] = await Read<ConnectionRequestedEvent>(host);
            foreach (var request in requests)
                await host.Commands.WriteAsync(new AcceptConnectionCommand(request.ConnectionId));
            foreach (var request in requests)
                await Read<PeerConnectedEvent>(host);
            foreach (var client in clients)
                await Read<PeerConnectedEvent>(client);

            await using var overflow = Create(directory.Path, 999);
            await Read<SteamInitializedEvent>(overflow);
            await overflow.Commands.WriteAsync(new ConnectPeerCommand(new SteamPeerId(801)));
            var failure = await Read<TransportOperationFailedEvent>(overflow);
            Assert.Equal("The host is full.", failure.Diagnostic);
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }

    private static async Task<SteamConnectionId> Connect(NamedPipeSteamTransport host, NamedPipeSteamTransport client, ulong hostId)
    {
        await client.Commands.WriteAsync(new ConnectPeerCommand(new SteamPeerId(hostId)));
        var request = await Read<ConnectionRequestedEvent>(host);
        await host.Commands.WriteAsync(new AcceptConnectionCommand(request.ConnectionId));
        await Read<PeerConnectedEvent>(host);
        await Read<PeerConnectedEvent>(client);
        return request.ConnectionId;
    }

    private static NamedPipeSteamTransport Create(string directory, ulong peerId, TimeSpan? hello = null, TimeSpan? staleAfter = null) =>
        new(new NamedPipeSteamTransportOptions(directory, new SteamPeerId(peerId))
        {
            HelloTimeout = hello ?? TimeSpan.FromSeconds(5),
            ConnectTimeout = TimeSpan.FromSeconds(2),
            RegistryStaleAfter = staleAfter ?? TimeSpan.FromSeconds(15),
        });

    private static async Task<T> Read<T>(ISteamTransport transport) where T : SteamTransportEvent
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (await transport.Events.WaitToReadAsync(timeout.Token))
        {
            while (transport.Events.TryRead(out var item))
            {
                if (item is T expected)
                    return expected;
                if (item is TransportOperationFailedEvent failed && typeof(T) != typeof(TransportOperationFailedEvent))
                    throw new Xunit.Sdk.XunitException($"Unexpected transport failure in {failed.Operation}: {failed.Diagnostic}");
            }
        }
        throw new Xunit.Sdk.XunitException($"Transport completed before {typeof(T).Name}.");
    }

    private static string LobbyPath(string directory, SteamLobbyId lobbyId) =>
        System.IO.Path.Combine(directory, $"lobby-{lobbyId.Value:x16}.json");

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"draftsim-pipe-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
