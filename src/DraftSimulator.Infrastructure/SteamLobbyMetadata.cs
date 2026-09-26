namespace DraftSimulator.Infrastructure;

public static class SteamLobbyMetadata
{
    public const string ApplicationKey = "ds_app";
    public const string ApplicationValue = "draft-simulator";
    public const string ProtocolKey = "ds_protocol";
    public const string ProtocolValue = "1";
    public const string RoomHashKey = "ds_room_hash";
    public const string OpenKey = "ds_open";
    public const int MemberLimit = 8;

    public static IReadOnlyDictionary<string, string> Create(string roomCode, bool isOpen = true) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ApplicationKey] = ApplicationValue,
            [ProtocolKey] = ProtocolValue,
            [RoomHashKey] = SteamRoomCode.Hash(roomCode),
            [OpenKey] = isOpen ? "1" : "0",
        };

    public static IReadOnlyDictionary<string, string> SearchFilters(string roomCode) => Create(roomCode);
}
