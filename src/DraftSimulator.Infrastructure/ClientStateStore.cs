using System.Text.Json;
using System.Text.Json.Serialization;

namespace DraftSimulator.Infrastructure;

public sealed record ClientState(
    ulong? LastHostSteamId,
    ulong? LastSteamLobbyId,
    Guid? LastApplicationSessionId,
    string? LastNormalizedRoomCode,
    string? LastAcceptedPlayerName,
    bool PreviousSessionEndedNormally);

public sealed class ClientStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    private readonly string _path;

    public ClientStateStore(string path) => _path = path ?? throw new ArgumentNullException(nameof(path));

    public ClientState? Load()
    {
        if (!File.Exists(_path))
            return null;
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<ClientState>(stream, Options)
            ?? throw new JsonException("Client state must contain a JSON object.");
    }

    public void Save(ClientState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       16_384, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void Clear() => File.Delete(_path);
}
