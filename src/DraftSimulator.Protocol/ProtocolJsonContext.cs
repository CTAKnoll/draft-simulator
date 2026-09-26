using System.Text.Json;
using System.Text.Json.Serialization;

namespace DraftSimulator.Protocol;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(EnvelopeHeader))]
[JsonSerializable(typeof(HelloDto))]
[JsonSerializable(typeof(SetNameDto))]
[JsonSerializable(typeof(SetLobbyReadyDto))]
[JsonSerializable(typeof(SetSelectionDto))]
[JsonSerializable(typeof(SetPickLockedDto))]
[JsonSerializable(typeof(AssetNeedDto))]
[JsonSerializable(typeof(PreparationReadyDto))]
[JsonSerializable(typeof(PreparationFailedDto))]
[JsonSerializable(typeof(ReopenLobbyJoinDto))]
[JsonSerializable(typeof(WelcomeDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(LobbySnapshotDto))]
[JsonSerializable(typeof(CountdownStartedDto))]
[JsonSerializable(typeof(CountdownCancelledDto))]
[JsonSerializable(typeof(AssetManifestDto))]
[JsonSerializable(typeof(PreparationSnapshotDto))]
[JsonSerializable(typeof(DraftSnapshotDto))]
[JsonSerializable(typeof(DraftCompletedDto))]
[JsonSerializable(typeof(LobbyAvailabilityDto))]
[JsonSerializable(typeof(KickedDto))]
[JsonSerializable(typeof(HostClosedDto))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext;

internal sealed record EnvelopeHeader(int V, int Code, Guid RequestId, JsonElement Payload);
