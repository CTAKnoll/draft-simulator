namespace DraftSimulator.Infrastructure;

public enum SteamAdapterEventKind
{
    LobbyCreated,
    LobbyCreateFailed,
    LobbySearchCompleted,
    LobbyEntered,
    LobbyJoinFailed,
    LobbyInvitationReceived,
    LobbyJoinRequested,
    ConnectionStatusChanged,
}

public sealed record SteamAdapterEvent(
    SteamAdapterEventKind Kind,
    SteamLobbyId LobbyId = default,
    SteamPeerId PeerId = default,
    SteamConnectionId ConnectionId = default,
    SteamConnectionState ConnectionState = SteamConnectionState.None,
    bool IsInboundConnection = false,
    IReadOnlyList<SteamLobbyId>? LobbyIds = null,
    string Diagnostic = "");

public sealed record SteamReceivedMessage(SteamConnectionId ConnectionId, byte[] Payload);

public interface ISteamworksAdapter : IDisposable
{
    bool Initialize(uint expectedAppId, out SteamPeerId localPeerId, out SteamInitializationFailure failure, out string diagnostic);
    void Shutdown();
    void WarmRelayNetworkAccess();
    void RunCallbacks();
    IReadOnlyList<SteamAdapterEvent> DrainEvents();
    IReadOnlyList<SteamReceivedMessage> ReceiveMessages(IReadOnlyCollection<SteamConnectionId> connections);

    void CreatePublicLobby(int memberLimit);
    bool SetLobbyData(SteamLobbyId lobbyId, string key, string value);
    bool SetLobbyJoinable(SteamLobbyId lobbyId, bool joinable);
    void RequestLobbyList(IReadOnlyDictionary<string, string> exactFilters);
    void JoinLobby(SteamLobbyId lobbyId);
    SteamPeerId GetLobbyOwner(SteamLobbyId lobbyId);
    bool IsLobbyMember(SteamLobbyId lobbyId, SteamPeerId peerId);
    bool IsImmediateFriend(SteamPeerId peerId);
    void LeaveLobby(SteamLobbyId lobbyId);
    bool InviteUserToLobby(SteamLobbyId lobbyId, SteamPeerId peerId);
    void OpenLobbyInviteOverlay(SteamLobbyId lobbyId);

    SteamListenSocketId CreateListenSocketP2P(int virtualPort);
    bool CloseListenSocket(SteamListenSocketId listenSocketId);
    SteamConnectionId ConnectP2P(SteamPeerId peerId, int virtualPort);
    bool AcceptConnection(SteamConnectionId connectionId);
    bool CloseConnection(SteamConnectionId connectionId, int reason, string diagnostic);
    bool SendReliable(SteamConnectionId connectionId, ReadOnlySpan<byte> payload, out string diagnostic);
}
