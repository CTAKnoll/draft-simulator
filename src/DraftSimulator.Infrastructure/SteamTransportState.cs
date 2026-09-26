namespace DraftSimulator.Infrastructure;

public sealed class SteamTransportState
{
    private readonly Dictionary<SteamConnectionId, SteamPeerId> _peersByConnection = [];
    private readonly Dictionary<SteamPeerId, SteamConnectionId> _connectionsByPeer = [];
    private readonly Dictionary<SteamConnectionId, DateTimeOffset> _helloDeadlines = [];

    public SteamLobbyId? CurrentLobby { get; set; }
    public SteamListenSocketId? ListenSocket { get; set; }

    public void Bind(SteamConnectionId connectionId, SteamPeerId peerId)
    {
        if (connectionId.Value == 0 || peerId.Value == 0)
            throw new ArgumentException("Steam connection and peer IDs must be nonzero.");

        if (_connectionsByPeer.TryGetValue(peerId, out var previousConnection) && previousConnection != connectionId)
        {
            _peersByConnection.Remove(previousConnection);
            _helloDeadlines.Remove(previousConnection);
        }

        _peersByConnection[connectionId] = peerId;
        _connectionsByPeer[peerId] = connectionId;
    }

    public bool TryGetPeer(SteamConnectionId connectionId, out SteamPeerId peerId) =>
        _peersByConnection.TryGetValue(connectionId, out peerId);

    public bool TryGetConnection(SteamPeerId peerId, out SteamConnectionId connectionId) =>
        _connectionsByPeer.TryGetValue(peerId, out connectionId);

    public IReadOnlyCollection<SteamConnectionId> Connections => _peersByConnection.Keys;

    public void StartHelloTimeout(SteamConnectionId connectionId, DateTimeOffset deadline)
    {
        if (!_peersByConnection.ContainsKey(connectionId))
            throw new InvalidOperationException("The connection must be bound to a Steam peer first.");
        _helloDeadlines[connectionId] = deadline;
    }

    public bool MarkHelloReceived(SteamConnectionId connectionId) => _helloDeadlines.Remove(connectionId);

    public IReadOnlyList<(SteamConnectionId ConnectionId, SteamPeerId PeerId)> TakeExpiredHelloTimeouts(DateTimeOffset now)
    {
        var expired = _helloDeadlines
            .Where(item => item.Value <= now)
            .Select(item => (item.Key, _peersByConnection[item.Key]))
            .ToArray();
        foreach (var item in expired)
            _helloDeadlines.Remove(item.Key);
        return expired;
    }

    public SteamPeerId? Remove(SteamConnectionId connectionId)
    {
        _helloDeadlines.Remove(connectionId);
        if (!_peersByConnection.Remove(connectionId, out var peerId))
            return null;
        if (_connectionsByPeer.TryGetValue(peerId, out var current) && current == connectionId)
            _connectionsByPeer.Remove(peerId);
        return peerId;
    }
}
