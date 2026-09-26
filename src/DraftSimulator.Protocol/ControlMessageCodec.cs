using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace DraftSimulator.Protocol;

public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception innerException) : base(message, innerException) { }
}

public static class ControlMessageCodec
{
    public static ControlMessage DeserializeClient(ReadOnlySpan<byte> utf8Json, int maxBytes = ProtocolConstants.DefaultControlMessageMaxBytes) =>
        Deserialize(utf8Json, true, maxBytes);

    public static ControlMessage DeserializeHost(ReadOnlySpan<byte> utf8Json, int maxBytes = ProtocolConstants.DefaultControlMessageMaxBytes) =>
        Deserialize(utf8Json, false, maxBytes);

    public static byte[] SerializeClient(ControlMessage message, int maxBytes = ProtocolConstants.DefaultControlMessageMaxBytes) =>
        Serialize(message, true, maxBytes);

    public static byte[] SerializeHost(ControlMessage message, int maxBytes = ProtocolConstants.DefaultControlMessageMaxBytes) =>
        Serialize(message, false, maxBytes);

    private static ControlMessage Deserialize(ReadOnlySpan<byte> json, bool client, int maxBytes)
    {
        ValidateLimit(json.Length, maxBytes);
        RejectDuplicateProperties(json);
        try
        {
            var header = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.EnvelopeHeader)
                ?? throw new ProtocolException("The control envelope cannot be null.");
            if (header.V != ProtocolConstants.Version)
                throw new ProtocolException($"Unsupported protocol version {header.V}.");

            var payload = client ? DeserializeClientPayload(header.Code, header.Payload) : DeserializeHostPayload(header.Code, header.Payload);
            ValidatePayload(payload);
            return new ControlMessage(header.V, header.Code, header.RequestId, payload);
        }
        catch (ProtocolException) { throw; }
        catch (JsonException exception) { throw new ProtocolException("Malformed control message.", exception); }
    }

    private static byte[] Serialize(ControlMessage message, bool client, int maxBytes)
    {
        if (message.Version != ProtocolConstants.Version)
            throw new ProtocolException($"Unsupported protocol version {message.Version}.");
        ValidateCodeAndType(message.Code, message.Payload, client);
        ValidatePayload(message.Payload);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", message.Version);
            writer.WriteNumber("code", message.Code);
            writer.WriteString("requestId", message.RequestId);
            writer.WritePropertyName("payload");
            SerializePayload(writer, message.Payload);
            writer.WriteEndObject();
        }
        ValidateLimit(buffer.WrittenCount, maxBytes);
        return buffer.WrittenSpan.ToArray();
    }

    private static ProtocolPayload DeserializeClientPayload(int code, JsonElement payload) => code switch
    {
        (int)ClientMessageCode.Hello => Deserialize(payload, ProtocolJsonContext.Default.HelloDto),
        (int)ClientMessageCode.SetName => Deserialize(payload, ProtocolJsonContext.Default.SetNameDto),
        (int)ClientMessageCode.SetLobbyReady => Deserialize(payload, ProtocolJsonContext.Default.SetLobbyReadyDto),
        (int)ClientMessageCode.SetSelection => Deserialize(payload, ProtocolJsonContext.Default.SetSelectionDto),
        (int)ClientMessageCode.SetPickLocked => Deserialize(payload, ProtocolJsonContext.Default.SetPickLockedDto),
        (int)ClientMessageCode.AssetNeed => Deserialize(payload, ProtocolJsonContext.Default.AssetNeedDto),
        (int)ClientMessageCode.PreparationReady => Deserialize(payload, ProtocolJsonContext.Default.PreparationReadyDto),
        (int)ClientMessageCode.ReopenLobbyJoin => Deserialize(payload, ProtocolJsonContext.Default.ReopenLobbyJoinDto),
        (int)ClientMessageCode.PreparationFailed => Deserialize(payload, ProtocolJsonContext.Default.PreparationFailedDto),
        _ => throw new ProtocolException($"Unknown client message code {code}."),
    };

    private static ProtocolPayload DeserializeHostPayload(int code, JsonElement payload) => code switch
    {
        (int)HostMessageCode.Welcome => Deserialize(payload, ProtocolJsonContext.Default.WelcomeDto),
        (int)HostMessageCode.Error => Deserialize(payload, ProtocolJsonContext.Default.ErrorDto),
        (int)HostMessageCode.LobbySnapshot => Deserialize(payload, ProtocolJsonContext.Default.LobbySnapshotDto),
        (int)HostMessageCode.CountdownStarted => Deserialize(payload, ProtocolJsonContext.Default.CountdownStartedDto),
        (int)HostMessageCode.CountdownCancelled => Deserialize(payload, ProtocolJsonContext.Default.CountdownCancelledDto),
        (int)HostMessageCode.AssetManifest => Deserialize(payload, ProtocolJsonContext.Default.AssetManifestDto),
        (int)HostMessageCode.PreparationSnapshot => Deserialize(payload, ProtocolJsonContext.Default.PreparationSnapshotDto),
        (int)HostMessageCode.DraftSnapshot => Deserialize(payload, ProtocolJsonContext.Default.DraftSnapshotDto),
        (int)HostMessageCode.DraftCompleted => Deserialize(payload, ProtocolJsonContext.Default.DraftCompletedDto),
        (int)HostMessageCode.LobbyAvailability => Deserialize(payload, ProtocolJsonContext.Default.LobbyAvailabilityDto),
        (int)HostMessageCode.Kicked => Deserialize(payload, ProtocolJsonContext.Default.KickedDto),
        (int)HostMessageCode.HostClosed => Deserialize(payload, ProtocolJsonContext.Default.HostClosedDto),
        _ => throw new ProtocolException($"Unknown host message code {code}."),
    };

    private static T Deserialize<T>(JsonElement element, JsonTypeInfo<T> typeInfo) where T : ProtocolPayload =>
        element.Deserialize(typeInfo) ?? throw new ProtocolException("The message payload cannot be null.");

    private static void SerializePayload(Utf8JsonWriter writer, ProtocolPayload payload)
    {
        switch (payload)
        {
            case HelloDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.HelloDto); break;
            case SetNameDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.SetNameDto); break;
            case SetLobbyReadyDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.SetLobbyReadyDto); break;
            case SetSelectionDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.SetSelectionDto); break;
            case SetPickLockedDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.SetPickLockedDto); break;
            case AssetNeedDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.AssetNeedDto); break;
            case PreparationReadyDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.PreparationReadyDto); break;
            case PreparationFailedDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.PreparationFailedDto); break;
            case ReopenLobbyJoinDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.ReopenLobbyJoinDto); break;
            case WelcomeDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.WelcomeDto); break;
            case ErrorDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.ErrorDto); break;
            case LobbySnapshotDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.LobbySnapshotDto); break;
            case CountdownStartedDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.CountdownStartedDto); break;
            case CountdownCancelledDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.CountdownCancelledDto); break;
            case AssetManifestDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.AssetManifestDto); break;
            case PreparationSnapshotDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.PreparationSnapshotDto); break;
            case DraftSnapshotDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.DraftSnapshotDto); break;
            case DraftCompletedDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.DraftCompletedDto); break;
            case LobbyAvailabilityDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.LobbyAvailabilityDto); break;
            case KickedDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.KickedDto); break;
            case HostClosedDto value: JsonSerializer.Serialize(writer, value, ProtocolJsonContext.Default.HostClosedDto); break;
            default: throw new ProtocolException($"Unsupported payload type {payload.GetType().Name}.");
        }
    }

    private static void ValidateCodeAndType(int code, ProtocolPayload payload, bool client)
    {
        var valid = client ? code switch
        {
            1 => payload is HelloDto, 2 => payload is SetNameDto, 3 => payload is SetLobbyReadyDto,
            4 => payload is SetSelectionDto, 5 => payload is SetPickLockedDto, 6 => payload is AssetNeedDto,
            7 => payload is PreparationReadyDto, 8 => payload is ReopenLobbyJoinDto,
            9 => payload is PreparationFailedDto, _ => false,
        } : code switch
        {
            101 => payload is WelcomeDto, 102 => payload is ErrorDto, 103 => payload is LobbySnapshotDto,
            104 => payload is CountdownStartedDto, 105 => payload is CountdownCancelledDto, 106 => payload is AssetManifestDto,
            107 => payload is PreparationSnapshotDto, 108 => payload is DraftSnapshotDto, 109 => payload is DraftCompletedDto,
            110 => payload is LobbyAvailabilityDto, 111 => payload is KickedDto, 112 => payload is HostClosedDto, _ => false,
        };
        if (!valid)
            throw new ProtocolException($"Message code {code} does not match payload type {payload.GetType().Name}.");
    }

    private static void ValidatePayload(ProtocolPayload payload)
    {
        switch (payload)
        {
            case HelloDto value: Text(value.RequestedName, nameof(value.RequestedName)); break;
            case SetNameDto value: Text(value.Name, nameof(value.Name)); break;
            case SetSelectionDto value: Items(value.InstanceIds, nameof(value.InstanceIds)); break;
            case WelcomeDto value: Defined(value.ReconnectResult); break;
            case ErrorDto value: Defined(value.ErrorCode); break;
            case LobbySnapshotDto value: Page(value.Page, Items(value.Players, nameof(value.Players))); foreach (var player in value.Players) { Required(player, nameof(value.Players)); Text(player.Name, nameof(player.Name)); Defined(player.ConnectionStatus); } break;
            case CountdownCancelledDto value: Defined(value.Reason); break;
            case AssetManifestDto value: Page(value.Page, Items(value.Assets, nameof(value.Assets))); foreach (var asset in value.Assets) { Required(asset, nameof(value.Assets)); Text(asset.MimeType, nameof(asset.MimeType)); } break;
            case PreparationSnapshotDto value: Page(value.Page, Items(value.Players, nameof(value.Players))); foreach (var player in value.Players) { Required(player, nameof(value.Players)); Defined(player.Status); } break;
            case DraftSnapshotDto value:
                var entryCount = checked(Items(value.CurrentPack, nameof(value.CurrentPack)) + Items(value.SelectedInstanceIds, nameof(value.SelectedInstanceIds)) + Items(value.Collection, nameof(value.Collection)) + Items(value.Players, nameof(value.Players)));
                Page(value.Page, entryCount);
                Defined(value.Direction);
                foreach (var card in value.CurrentPack) Required(card, nameof(value.CurrentPack));
                foreach (var card in value.Collection) Required(card, nameof(value.Collection));
                foreach (var player in value.Players) { Required(player, nameof(value.Players)); Text(player.Name, nameof(player.Name)); Defined(player.ConnectionStatus); }
                break;
            case DraftCompletedDto value: Page(value.Page, Items(value.Collection, nameof(value.Collection))); foreach (var card in value.Collection) { Required(card, nameof(value.Collection)); Text(card.ExportName, nameof(card.ExportName)); } break;
            case KickedDto value: Defined(value.Reason); break;
            case HostClosedDto value: Defined(value.Reason); break;
            case AssetNeedDto value: Page(value.Page, Items(value.Hashes, nameof(value.Hashes))); break;
            case ReopenLobbyJoinDto value: Text(value.RequestedName, nameof(value.RequestedName)); break;
            case PreparationFailedDto value: Defined(value.ErrorCode); break;
        }
    }

    private static void Page(PageMetadata page, int entries)
    {
        Required(page, nameof(page));
        if (page.PageCount <= 0 || page.PageIndex < 0 || page.PageIndex >= page.PageCount || page.Revision < 0)
            throw new ProtocolException("Invalid page metadata.");
        if (entries > ProtocolConstants.MaxPageEntries)
            throw new ProtocolException($"A page cannot contain more than {ProtocolConstants.MaxPageEntries} variable-list entries.");
    }

    private static int Items<T>(T[]? items, string name)
    {
        if (items is null) throw new ProtocolException($"Required property {name} is missing or null.");
        return items.Length;
    }

    private static void Text(string? value, string name)
    {
        if (value is null) throw new ProtocolException($"Required property {name} is missing or null.");
    }

    private static void Required<T>(T? value, string name) where T : class
    {
        if (value is null) throw new ProtocolException($"Required property {name} is missing or null.");
    }

    private static void Defined<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ProtocolException($"Unknown {typeof(T).Name} code {value}.");
    }

    private static void ValidateLimit(int length, int maxBytes)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (length > maxBytes) throw new ProtocolException($"Control message exceeds the {maxBytes}-byte limit.");
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = ProtocolConstants.JsonMaxDepth });
            var propertySets = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject) propertySets.Push(new HashSet<string>(StringComparer.Ordinal));
                else if (reader.TokenType == JsonTokenType.EndObject) propertySets.Pop();
                else if (reader.TokenType == JsonTokenType.PropertyName && !propertySets.Peek().Add(reader.GetString()!))
                    throw new ProtocolException($"Duplicate JSON property '{reader.GetString()}'.");
            }
        }
        catch (ProtocolException) { throw; }
        catch (JsonException exception) { throw new ProtocolException("Malformed control message.", exception); }
    }
}
