using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Steamworks;

namespace DraftSimulator.Infrastructure;

public sealed class SteamworksNetAdapter : ISteamworksAdapter
{
    private const int ReceiveBatchSize = 32;
    private const int ReliableSendFlag = 8;

    private readonly ConcurrentQueue<SteamAdapterEvent> _events = new();
    private readonly List<IDisposable> _callResults = [];
    private readonly List<HSteamListenSocket> _listenSockets = [];
    private Callback<SteamNetConnectionStatusChangedCallback_t>? _connectionStatusCallback;
    private Callback<GameLobbyJoinRequested_t>? _lobbyJoinRequestedCallback;
    private Callback<LobbyInvite_t>? _lobbyInviteCallback;
    private bool _initialized;
    private bool _disposed;

    public bool Initialize(
        uint expectedAppId,
        out SteamPeerId localPeerId,
        out SteamInitializationFailure failure,
        out string diagnostic)
    {
        localPeerId = default;
        failure = SteamInitializationFailure.SteamUnavailable;
        diagnostic = "Steam must be running and the user must be signed in.";

        try
        {
            if (!SteamAPI.Init())
                return false;

            _initialized = true;
            var actualAppId = (uint)SteamUtils.GetAppID();
            if (actualAppId != expectedAppId)
            {
                failure = SteamInitializationFailure.AppIdMismatch;
                diagnostic = $"Steam initialized AppID {actualAppId}, but AppID {expectedAppId} was expected.";
                Shutdown();
                return false;
            }

            localPeerId = new SteamPeerId((ulong)SteamUser.GetSteamID());
            if (localPeerId.Value == 0)
            {
                Shutdown();
                return false;
            }

            _connectionStatusCallback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
            _lobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(value =>
                _events.Enqueue(new SteamAdapterEvent(
                    SteamAdapterEventKind.LobbyJoinRequested,
                    new SteamLobbyId((ulong)value.m_steamIDLobby),
                    new SteamPeerId((ulong)value.m_steamIDFriend))));
            _lobbyInviteCallback = Callback<LobbyInvite_t>.Create(value =>
                _events.Enqueue(new SteamAdapterEvent(
                    SteamAdapterEventKind.LobbyInvitationReceived,
                    new SteamLobbyId(value.m_ulSteamIDLobby),
                    new SteamPeerId(value.m_ulSteamIDUser))));
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            diagnostic = $"Steam initialization failed: {exception.Message}";
            if (_initialized)
                Shutdown();
            return false;
        }
    }

    public void WarmRelayNetworkAccess()
    {
        EnsureInitialized();
        SteamNetworkingUtils.InitRelayNetworkAccess();
    }

    public void RunCallbacks()
    {
        EnsureInitialized();
        SteamAPI.RunCallbacks();
        SteamNetworkingSockets.RunCallbacks();
    }

    public IReadOnlyList<SteamAdapterEvent> DrainEvents()
    {
        var drained = new List<SteamAdapterEvent>();
        while (_events.TryDequeue(out var adapterEvent))
            drained.Add(adapterEvent);
        return drained;
    }

    public IReadOnlyList<SteamReceivedMessage> ReceiveMessages(IReadOnlyCollection<SteamConnectionId> connections)
    {
        EnsureInitialized();
        var received = new List<SteamReceivedMessage>();
        var pointers = new IntPtr[ReceiveBatchSize];
        foreach (var connection in connections)
        {
            while (true)
            {
                var count = SteamNetworkingSockets.ReceiveMessagesOnConnection(ToHandle(connection), pointers, pointers.Length);
                if (count <= 0)
                    break;

                for (var index = 0; index < count; index++)
                {
                    var pointer = pointers[index];
                    try
                    {
                        var message = SteamNetworkingMessage_t.FromIntPtr(pointer);
                        if (message.m_cbSize < 0)
                            continue;
                        var payload = new byte[message.m_cbSize];
                        if (payload.Length != 0)
                            Marshal.Copy(message.m_pData, payload, 0, payload.Length);
                        received.Add(new SteamReceivedMessage(connection, payload));
                    }
                    finally
                    {
                        SteamNetworkingMessage_t.Release(pointer);
                        pointers[index] = IntPtr.Zero;
                    }
                }
            }
        }

        return received;
    }

    public void CreatePublicLobby(int memberLimit)
    {
        EnsureInitialized();
        CallResult<LobbyCreated_t>? result = null;
        result = CallResult<LobbyCreated_t>.Create((value, ioFailure) =>
        {
            if (ioFailure || value.m_eResult != EResult.k_EResultOK)
                _events.Enqueue(new SteamAdapterEvent(SteamAdapterEventKind.LobbyCreateFailed, Diagnostic: ioFailure ? "Steam lobby creation I/O failure." : value.m_eResult.ToString()));
            else
                _events.Enqueue(new SteamAdapterEvent(SteamAdapterEventKind.LobbyCreated, new SteamLobbyId(value.m_ulSteamIDLobby)));
            ReleaseCallResult(result!);
        });
        _callResults.Add(result);
        result.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, memberLimit));
    }

    public bool SetLobbyData(SteamLobbyId lobbyId, string key, string value) =>
        SteamMatchmaking.SetLobbyData(ToSteamId(lobbyId), key, value);

    public bool SetLobbyJoinable(SteamLobbyId lobbyId, bool joinable) =>
        SteamMatchmaking.SetLobbyJoinable(ToSteamId(lobbyId), joinable);

    public void RequestLobbyList(IReadOnlyDictionary<string, string> exactFilters)
    {
        EnsureInitialized();
        foreach (var filter in exactFilters)
            SteamMatchmaking.AddRequestLobbyListStringFilter(filter.Key, filter.Value, ELobbyComparison.k_ELobbyComparisonEqual);

        CallResult<LobbyMatchList_t>? result = null;
        result = CallResult<LobbyMatchList_t>.Create((value, ioFailure) =>
        {
            var lobbyIds = new List<SteamLobbyId>();
            if (!ioFailure)
            {
                for (var index = 0; index < value.m_nLobbiesMatching; index++)
                    lobbyIds.Add(new SteamLobbyId((ulong)SteamMatchmaking.GetLobbyByIndex(index)));
            }
            _events.Enqueue(new SteamAdapterEvent(
                SteamAdapterEventKind.LobbySearchCompleted,
                LobbyIds: lobbyIds,
                Diagnostic: ioFailure ? "Steam lobby search I/O failure." : string.Empty));
            ReleaseCallResult(result!);
        });
        _callResults.Add(result);
        result.Set(SteamMatchmaking.RequestLobbyList());
    }

    public void JoinLobby(SteamLobbyId lobbyId)
    {
        EnsureInitialized();
        CallResult<LobbyEnter_t>? result = null;
        result = CallResult<LobbyEnter_t>.Create((value, ioFailure) =>
        {
            var enteredLobby = value.m_ulSteamIDLobby == 0 ? lobbyId : new SteamLobbyId(value.m_ulSteamIDLobby);
            var response = (EChatRoomEnterResponse)value.m_EChatRoomEnterResponse;
            if (ioFailure || response != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                _events.Enqueue(new SteamAdapterEvent(
                    SteamAdapterEventKind.LobbyJoinFailed,
                    enteredLobby,
                    Diagnostic: ioFailure ? "Steam lobby join I/O failure." : response.ToString()));
            }
            else
            {
                _events.Enqueue(new SteamAdapterEvent(
                    SteamAdapterEventKind.LobbyEntered,
                    enteredLobby,
                    new SteamPeerId((ulong)SteamMatchmaking.GetLobbyOwner(ToSteamId(enteredLobby)))));
            }
            ReleaseCallResult(result!);
        });
        _callResults.Add(result);
        result.Set(SteamMatchmaking.JoinLobby(ToSteamId(lobbyId)));
    }

    public SteamPeerId GetLobbyOwner(SteamLobbyId lobbyId) =>
        new((ulong)SteamMatchmaking.GetLobbyOwner(ToSteamId(lobbyId)));

    public bool IsLobbyMember(SteamLobbyId lobbyId, SteamPeerId peerId)
    {
        var memberCount = SteamMatchmaking.GetNumLobbyMembers(ToSteamId(lobbyId));
        for (var index = 0; index < memberCount; index++)
        {
            if ((ulong)SteamMatchmaking.GetLobbyMemberByIndex(ToSteamId(lobbyId), index) == peerId.Value)
                return true;
        }
        return false;
    }

    public bool IsImmediateFriend(SteamPeerId peerId) =>
        SteamFriends.GetFriendRelationship(ToSteamId(peerId)) == EFriendRelationship.k_EFriendRelationshipFriend;

    public void LeaveLobby(SteamLobbyId lobbyId) => SteamMatchmaking.LeaveLobby(ToSteamId(lobbyId));

    public bool InviteUserToLobby(SteamLobbyId lobbyId, SteamPeerId peerId) =>
        SteamMatchmaking.InviteUserToLobby(ToSteamId(lobbyId), ToSteamId(peerId));

    public void OpenLobbyInviteOverlay(SteamLobbyId lobbyId) =>
        SteamFriends.ActivateGameOverlayInviteDialog(ToSteamId(lobbyId));

    public SteamListenSocketId CreateListenSocketP2P(int virtualPort)
    {
        EnsureInitialized();
        var socket = SteamNetworkingSockets.CreateListenSocketP2P(virtualPort, 0, []);
        if (socket != HSteamListenSocket.Invalid)
            _listenSockets.Add(socket);
        return new SteamListenSocketId((uint)socket);
    }

    public SteamConnectionId ConnectP2P(SteamPeerId peerId, int virtualPort)
    {
        EnsureInitialized();
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(peerId.Value);
        var connection = SteamNetworkingSockets.ConnectP2P(ref identity, virtualPort, 0, []);
        return new SteamConnectionId((uint)connection);
    }

    public bool CloseListenSocket(SteamListenSocketId listenSocketId)
    {
        var socket = new HSteamListenSocket(listenSocketId.Value);
        var closed = SteamNetworkingSockets.CloseListenSocket(socket);
        if (closed)
            _listenSockets.Remove(socket);
        return closed;
    }

    public bool AcceptConnection(SteamConnectionId connectionId) =>
        SteamNetworkingSockets.AcceptConnection(ToHandle(connectionId)) == EResult.k_EResultOK;

    public bool CloseConnection(SteamConnectionId connectionId, int reason, string diagnostic) =>
        SteamNetworkingSockets.CloseConnection(ToHandle(connectionId), reason, diagnostic, false);

    public bool SendReliable(SteamConnectionId connectionId, ReadOnlySpan<byte> payload, out string diagnostic)
    {
        EnsureInitialized();
        var pointer = Marshal.AllocHGlobal(Math.Max(payload.Length, 1));
        try
        {
            if (!payload.IsEmpty)
                Marshal.Copy(payload.ToArray(), 0, pointer, payload.Length);
            var result = SteamNetworkingSockets.SendMessageToConnection(
                ToHandle(connectionId),
                pointer,
                checked((uint)payload.Length),
                ReliableSendFlag,
                out _);
            diagnostic = result == EResult.k_EResultOK ? string.Empty : result.ToString();
            return result == EResult.k_EResultOK;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t value)
    {
        var state = value.m_info.m_eState switch
        {
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting => SteamConnectionState.Connecting,
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected => SteamConnectionState.Connected,
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer => SteamConnectionState.ClosedByPeer,
            ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally => SteamConnectionState.ProblemDetectedLocally,
            _ => SteamConnectionState.None,
        };
        if (state == SteamConnectionState.None)
            return;

        _events.Enqueue(new SteamAdapterEvent(
            SteamAdapterEventKind.ConnectionStatusChanged,
            PeerId: new SteamPeerId(value.m_info.m_identityRemote.GetSteamID64()),
            ConnectionId: new SteamConnectionId((uint)value.m_hConn),
            ConnectionState: state,
            IsInboundConnection: value.m_info.m_hListenSocket != HSteamListenSocket.Invalid,
            Diagnostic: value.m_info.m_szEndDebug));
    }

    private void ReleaseCallResult(IDisposable result)
    {
        result.Dispose();
        _callResults.Remove(result);
    }

    private static CSteamID ToSteamId(SteamLobbyId lobbyId) => new(lobbyId.Value);
    private static CSteamID ToSteamId(SteamPeerId peerId) => new(peerId.Value);
    private static HSteamNetConnection ToHandle(SteamConnectionId connectionId) => new(connectionId.Value);

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
            throw new InvalidOperationException("Steam has not been initialized.");
    }

    public void Shutdown()
    {
        if (!_initialized)
            return;

        foreach (var socket in _listenSockets)
            SteamNetworkingSockets.CloseListenSocket(socket);
        _listenSockets.Clear();
        foreach (var result in _callResults.ToArray())
            result.Dispose();
        _callResults.Clear();
        _connectionStatusCallback?.Dispose();
        _connectionStatusCallback = null;
        _lobbyJoinRequestedCallback?.Dispose();
        _lobbyJoinRequestedCallback = null;
        _lobbyInviteCallback?.Dispose();
        _lobbyInviteCallback = null;
        SteamAPI.Shutdown();
        _initialized = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Shutdown();
        _disposed = true;
    }
}
