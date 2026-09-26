using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class SteamTransportStateTests
{
    private static readonly SteamConnectionId Connection = new(10);
    private static readonly SteamPeerId Peer = new(20);

    [Fact]
    public void BindsConnectionsInBothDirections()
    {
        var state = new SteamTransportState();
        state.Bind(Connection, Peer);

        Assert.True(state.TryGetPeer(Connection, out var peer));
        Assert.Equal(Peer, peer);
        Assert.True(state.TryGetConnection(Peer, out var connection));
        Assert.Equal(Connection, connection);
    }

    [Fact]
    public void RebindingPeerReplacesItsOldConnection()
    {
        var state = new SteamTransportState();
        state.Bind(Connection, Peer);
        state.Bind(new SteamConnectionId(11), Peer);

        Assert.False(state.TryGetPeer(Connection, out _));
        Assert.True(state.TryGetConnection(Peer, out var connection));
        Assert.Equal(new SteamConnectionId(11), connection);
    }

    [Fact]
    public void TakesOnlyExpiredHelloDeadlinesOnce()
    {
        var state = new SteamTransportState();
        var now = DateTimeOffset.UtcNow;
        state.Bind(Connection, Peer);
        state.Bind(new SteamConnectionId(11), new SteamPeerId(21));
        state.StartHelloTimeout(Connection, now);
        state.StartHelloTimeout(new SteamConnectionId(11), now.AddSeconds(1));

        Assert.Equal([(Connection, Peer)], state.TakeExpiredHelloTimeouts(now));
        Assert.Empty(state.TakeExpiredHelloTimeouts(now));
        Assert.True(state.MarkHelloReceived(new SteamConnectionId(11)));
    }

    [Fact]
    public void RemovingConnectionClearsPeerAndHelloState()
    {
        var state = new SteamTransportState();
        state.Bind(Connection, Peer);
        state.StartHelloTimeout(Connection, DateTimeOffset.MinValue);

        Assert.Equal(Peer, state.Remove(Connection));
        Assert.False(state.TryGetConnection(Peer, out _));
        Assert.Empty(state.TakeExpiredHelloTimeouts(DateTimeOffset.MaxValue));
    }

    [Fact]
    public void RejectsInvalidBindingsAndUnboundHelloDeadline()
    {
        var state = new SteamTransportState();
        Assert.Throws<ArgumentException>(() => state.Bind(default, Peer));
        Assert.Throws<InvalidOperationException>(() => state.StartHelloTimeout(Connection, DateTimeOffset.UtcNow));
    }
}
