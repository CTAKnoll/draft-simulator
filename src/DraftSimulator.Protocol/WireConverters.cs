using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DraftSimulator.Protocol;

public sealed class SteamIdJsonConverter : JsonConverter<SteamId>
{
    public override SteamId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(ReadUInt64String(ref reader, "Steam ID"));

    public override void Write(Utf8JsonWriter writer, SteamId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));

    internal static ulong ReadUInt64String(ref Utf8JsonReader reader, string description)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !ulong.TryParse(reader.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new JsonException($"{description} must be an unsigned decimal string.");
        return value;
    }
}

public sealed class LobbyIdJsonConverter : JsonConverter<LobbyId>
{
    public override LobbyId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(SteamIdJsonConverter.ReadUInt64String(ref reader, "Lobby ID"));

    public override void Write(Utf8JsonWriter writer, LobbyId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
}

public sealed class AssetHashJsonConverter : JsonConverter<AssetHash>
{
    public override AssetHash Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Asset hash must be a string.");
        try { return new AssetHash(reader.GetString()!); }
        catch (ArgumentException exception) { throw new JsonException(exception.Message, exception); }
    }

    public override void Write(Utf8JsonWriter writer, AssetHash value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
}

public abstract class GuidIdJsonConverter<T> : JsonConverter<T>
{
    protected abstract T Create(Guid value);
    protected abstract Guid GetValue(T value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var value) || value == Guid.Empty)
            throw new JsonException($"{typeof(T).Name} must be a non-empty UUID string.");
        return Create(value);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        var guid = GetValue(value);
        if (guid == Guid.Empty) throw new JsonException($"{typeof(T).Name} cannot be empty.");
        writer.WriteStringValue(guid);
    }
}

public sealed class SessionIdJsonConverter : GuidIdJsonConverter<SessionId>
{
    protected override SessionId Create(Guid value) => new(value);
    protected override Guid GetValue(SessionId value) => value.Value;
}

public sealed class PlayerIdJsonConverter : GuidIdJsonConverter<PlayerId>
{
    protected override PlayerId Create(Guid value) => new(value);
    protected override Guid GetValue(PlayerId value) => value.Value;
}

public sealed class CardInstanceIdJsonConverter : GuidIdJsonConverter<CardInstanceId>
{
    protected override CardInstanceId Create(Guid value) => new(value);
    protected override Guid GetValue(CardInstanceId value) => value.Value;
}

public sealed class SnapshotIdJsonConverter : GuidIdJsonConverter<SnapshotId>
{
    protected override SnapshotId Create(Guid value) => new(value);
    protected override Guid GetValue(SnapshotId value) => value.Value;
}
