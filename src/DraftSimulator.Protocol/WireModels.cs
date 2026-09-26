using System.Text.Json.Serialization;

namespace DraftSimulator.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int DefaultControlMessageMaxBytes = 262_144;
    public const int JsonMaxDepth = 16;
    public const int MaxPageEntries = 200;
}

public enum ClientMessageCode
{
    Hello = 1,
    SetName = 2,
    SetLobbyReady = 3,
    SetSelection = 4,
    SetPickLocked = 5,
    AssetNeed = 6,
    PreparationReady = 7,
    ReopenLobbyJoin = 8,
    PreparationFailed = 9,
}

public enum HostMessageCode
{
    Welcome = 101,
    Error = 102,
    LobbySnapshot = 103,
    CountdownStarted = 104,
    CountdownCancelled = 105,
    AssetManifest = 106,
    PreparationSnapshot = 107,
    DraftSnapshot = 108,
    DraftCompleted = 109,
    LobbyAvailability = 110,
    Kicked = 111,
    HostClosed = 112,
}

public enum ErrorCode
{
    SteamUnavailable = 1,
    LobbyCreationFailed = 2,
    LobbyNotFound = 3,
    RoomCodeInvalid = 4,
    RoomClosed = 5,
    LobbyFull = 6,
    NameInvalid = 7,
    NameDuplicate = 8,
    ProtocolVersionMismatch = 9,
    MalformedMessage = 10,
    InvalidActionForState = 11,
    HostDisconnected = 12,
    ClientDisconnected = 13,
    SourceScanFailed = 14,
    DraftConfigurationInvalid = 15,
    HostAssetPreparationFailed = 16,
    ClientAssetTransferStalled = 17,
    ClientAssetVerificationFailed = 18,
    ClientDiskSpaceInsufficient = 19,
    SessionNoLongerAvailable = 20,
    Kicked = 21,
    StaleRevision = 22,
    FutureRevision = 23,
}

public enum ReconnectResult { NewSession = 1, Reconnected = 2 }
public enum CountdownCancellationReason { PlayerUnready = 1, PlayerJoined = 2, PlayerLeft = 3, PlayerDisconnected = 4 }
public enum ConnectionStatus { Connected = 1, Disconnected = 2 }
public enum PreparationStatus { Waiting = 1, Transferring = 2, Ready = 3, Failed = 4 }
public enum DraftDirection { Counterclockwise = -1, Clockwise = 1 }
public enum ProtocolPhase { Handshake = 1, LobbyOpen = 2, StartingCountdown = 3, PreparingAssets = 4, Drafting = 5, Complete = 6, Closed = 7 }

[JsonConverter(typeof(SteamIdJsonConverter))]
public readonly record struct SteamId(ulong Value);

[JsonConverter(typeof(LobbyIdJsonConverter))]
public readonly record struct LobbyId(ulong Value);

[JsonConverter(typeof(SessionIdJsonConverter))]
public readonly record struct SessionId(Guid Value);
[JsonConverter(typeof(PlayerIdJsonConverter))]
public readonly record struct PlayerId(Guid Value);
[JsonConverter(typeof(CardInstanceIdJsonConverter))]
public readonly record struct CardInstanceId(Guid Value);
[JsonConverter(typeof(SnapshotIdJsonConverter))]
public readonly record struct SnapshotId(Guid Value);

[JsonConverter(typeof(AssetHashJsonConverter))]
public readonly record struct AssetHash
{
    public const int ByteLength = 32;
    public const int HexLength = ByteLength * 2;

    public AssetHash(string value)
    {
        if (!TryNormalize(value, out var normalized))
            throw new ArgumentException("Asset hashes must be 64 hexadecimal characters.", nameof(value));
        Value = normalized;
    }

    public string Value { get; }

    public static AssetHash FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
            throw new ArgumentException("An asset hash must contain exactly 32 bytes.", nameof(bytes));
        return new AssetHash(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    public byte[] ToBytes() => Convert.FromHexString(Value);

    private static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is null || value.Length != HexLength)
            return false;
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
                return false;
        }
        normalized = value.ToLowerInvariant();
        return true;
    }

    public override string ToString() => Value;
}

public sealed record PageMetadata(SnapshotId SnapshotId, long Revision, int PageIndex, int PageCount);

public abstract record ProtocolPayload;

public sealed record HelloDto(
    int ProtocolVersion,
    LobbyId? LobbyId,
    string? RoomCode,
    SessionId? ReconnectSessionId,
    string RequestedName) : ProtocolPayload;

public sealed record SetNameDto(string Name) : ProtocolPayload;
public sealed record SetLobbyReadyDto(bool IsReady) : ProtocolPayload;
public sealed record SetSelectionDto(long PickRevision, CardInstanceId[] InstanceIds) : ProtocolPayload;
public sealed record SetPickLockedDto(long PickRevision, bool IsLocked) : ProtocolPayload;
public sealed record AssetNeedDto(SessionId SessionId, PageMetadata Page, AssetHash[] Hashes) : ProtocolPayload;
public sealed record PreparationReadyDto(SessionId SessionId) : ProtocolPayload;
public sealed record PreparationFailedDto(SessionId SessionId, ErrorCode ErrorCode) : ProtocolPayload;
public sealed record ReopenLobbyJoinDto(string RequestedName) : ProtocolPayload;

public sealed record WelcomeDto(PlayerId PlayerId, SessionId SessionId, ReconnectResult ReconnectResult) : ProtocolPayload;
public sealed record ErrorDto(ErrorCode ErrorCode, bool Fatal) : ProtocolPayload;

public sealed record LobbyPlayerDto(PlayerId PlayerId, string Name, int Order, bool IsHost, bool IsReady, ConnectionStatus ConnectionStatus);
public sealed record LobbySnapshotDto(PageMetadata Page, PlayerId RecipientPlayerId, bool IsHost, LobbyPlayerDto[] Players) : ProtocolPayload;
public sealed record CountdownStartedDto(int DurationMilliseconds, long Generation) : ProtocolPayload;
public sealed record CountdownCancelledDto(long Generation, CountdownCancellationReason Reason) : ProtocolPayload;

public sealed record AssetManifestEntryDto(AssetHash Hash, long EncodedByteLength, int PixelWidth, int PixelHeight, string MimeType, int ChunkCount);
public sealed record AssetManifestDto(SessionId SessionId, int ProtocolVersion, int TransferChunkBytes, PageMetadata Page, AssetManifestEntryDto[] Assets) : ProtocolPayload;

public sealed record PlayerPreparationDto(PlayerId PlayerId, PreparationStatus Status);
public sealed record PreparationSnapshotDto(PageMetadata Page, PlayerPreparationDto[] Players, long LocalBytesReceived, long LocalBytesTotal) : ProtocolPayload;

public sealed record CardAssetDto(CardInstanceId InstanceId, AssetHash AssetHash);
public sealed record DraftPlayerDto(PlayerId PlayerId, string Name, int Order, ConnectionStatus ConnectionStatus, bool IsLocked, int CurrentPackCardCount);
public sealed record DraftSnapshotDto(
    PageMetadata Page,
    long PickRevision,
    CardAssetDto[] CurrentPack,
    CardInstanceId[] SelectedInstanceIds,
    CardAssetDto[] Collection,
    int RequiredSelectionCount,
    bool CanLock,
    bool CanUnlock,
    DraftPlayerDto[] Players,
    DraftDirection Direction,
    int PackRound,
    int TotalPackRounds) : ProtocolPayload;

public sealed record CompletedCardDto(CardInstanceId InstanceId, AssetHash AssetHash, string ExportName);
public sealed record DraftCompletedDto(PageMetadata Page, CompletedCardDto[] Collection) : ProtocolPayload;
public sealed record LobbyAvailabilityDto(bool IsOpen) : ProtocolPayload;
public sealed record KickedDto(ErrorCode Reason) : ProtocolPayload;
public sealed record HostClosedDto(ErrorCode Reason) : ProtocolPayload;

public sealed record ControlMessage(int Version, int Code, Guid RequestId, ProtocolPayload Payload);
