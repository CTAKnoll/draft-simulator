using System.Threading.Channels;
using System.Globalization;
using System.Reflection;

namespace DraftSimulator.Infrastructure;

public readonly record struct SteamLobbyId(ulong Value);
public readonly record struct SteamPeerId(ulong Value);
public readonly record struct SteamConnectionId(uint Value);
public readonly record struct SteamListenSocketId(uint Value);

public enum SteamConnectionState
{
    None,
    Connecting,
    Connected,
    ClosedByPeer,
    ProblemDetectedLocally,
}

public abstract record SteamTransportCommand;
public sealed record CreateLobbyCommand(string RoomCode) : SteamTransportCommand;
public sealed record FindLobbyCommand(string RoomCode) : SteamTransportCommand;
public sealed record JoinLobbyCommand(SteamLobbyId LobbyId) : SteamTransportCommand;
public sealed record LeaveLobbyCommand : SteamTransportCommand;
public sealed record InviteToLobbyCommand(SteamPeerId PeerId) : SteamTransportCommand;
public sealed record OpenLobbyInviteOverlayCommand : SteamTransportCommand;
public sealed record SetLobbyOpenCommand(bool IsOpen) : SteamTransportCommand;
public sealed record ConnectPeerCommand(SteamPeerId PeerId) : SteamTransportCommand;
public sealed record AcceptConnectionCommand(SteamConnectionId ConnectionId) : SteamTransportCommand;
public sealed record RejectConnectionCommand(SteamConnectionId ConnectionId, string Diagnostic) : SteamTransportCommand;
public sealed record MarkHelloReceivedCommand(SteamConnectionId ConnectionId) : SteamTransportCommand;
public sealed record SendMessageCommand(SteamPeerId PeerId, ReadOnlyMemory<byte> Payload) : SteamTransportCommand;
public sealed record ClosePeerCommand(SteamPeerId PeerId, string Diagnostic) : SteamTransportCommand;

public abstract record SteamTransportEvent;
public sealed record SteamInitializedEvent(SteamPeerId LocalPeerId) : SteamTransportEvent;
public sealed record SteamInitializationFailedEvent(SteamInitializationFailure Failure, string Diagnostic) : SteamTransportEvent;
public sealed record LobbyCreatedEvent(SteamLobbyId LobbyId, string NormalizedRoomCode) : SteamTransportEvent;
public sealed record LobbyCreationFailedEvent(string Diagnostic) : SteamTransportEvent;
public sealed record LobbySearchCompletedEvent(IReadOnlyList<SteamLobbyId> LobbyIds) : SteamTransportEvent;
public sealed record LobbyEnteredEvent(SteamLobbyId LobbyId, SteamPeerId OwnerId) : SteamTransportEvent;
public sealed record LobbyJoinFailedEvent(SteamLobbyId LobbyId, string Diagnostic) : SteamTransportEvent;
public sealed record LobbyInvitationReceivedEvent(SteamLobbyId LobbyId, SteamPeerId InviterId) : SteamTransportEvent;
public sealed record LobbyJoinRequestedEvent(SteamLobbyId LobbyId, SteamPeerId FriendId) : SteamTransportEvent;
public sealed record LobbyLeftEvent(SteamLobbyId LobbyId) : SteamTransportEvent;
public sealed record LobbyOpenChangedEvent(SteamLobbyId LobbyId, bool IsOpen) : SteamTransportEvent;
public sealed record ConnectionRequestedEvent(SteamConnectionId ConnectionId, SteamPeerId PeerId, bool IsLobbyMember, bool IsImmediateFriend = false, bool WasExplicitlyInvited = false) : SteamTransportEvent;
public sealed record PeerConnectedEvent(SteamConnectionId ConnectionId, SteamPeerId PeerId) : SteamTransportEvent;
public sealed record PeerDisconnectedEvent(SteamConnectionId ConnectionId, SteamPeerId PeerId, string Diagnostic) : SteamTransportEvent;
public sealed record HelloTimedOutEvent(SteamConnectionId ConnectionId, SteamPeerId PeerId) : SteamTransportEvent;
public sealed record MessageReceivedEvent(SteamConnectionId ConnectionId, SteamPeerId PeerId, ReadOnlyMemory<byte> Payload) : SteamTransportEvent;
public sealed record TransportOperationFailedEvent(string Operation, string Diagnostic) : SteamTransportEvent;
public sealed record SteamShutdownEvent : SteamTransportEvent;

public enum SteamInitializationFailure
{
    SteamUnavailable,
    AppIdMismatch,
}

public sealed record SteamTransportOptions(uint ExpectedAppId)
{
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1d / 30d);

    public static SteamTransportOptions FromBuildConfiguration()
    {
        var value = typeof(SteamTransportOptions).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "SteamAppId")
            .Value;
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var appId) || appId == 0)
            throw new InvalidOperationException("The SteamAppId MSBuild property must be a nonzero UInt32 value.");
        return new SteamTransportOptions(appId);
    }
}

public interface ISteamTransport : IAsyncDisposable
{
    ChannelWriter<SteamTransportCommand> Commands { get; }
    ChannelReader<SteamTransportEvent> Events { get; }
    Task Completion { get; }
}
