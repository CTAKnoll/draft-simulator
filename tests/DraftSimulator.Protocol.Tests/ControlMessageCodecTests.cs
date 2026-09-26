using System.Text;
using DraftSimulator.Protocol;

namespace DraftSimulator.Protocol.Tests;

public sealed class ControlMessageCodecTests
{
    private static readonly SessionId Session = new(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
    private static readonly PlayerId Player = new(Guid.Parse("11112233-4455-6677-8899-aabbccddeeff"));
    private static readonly CardInstanceId Card = new(Guid.Parse("22222233-4455-6677-8899-aabbccddeeff"));
    private static readonly AssetHash Hash = new(new string('a', 64));
    private static readonly PageMetadata Page = new(new SnapshotId(Guid.Parse("33332233-4455-6677-8899-aabbccddeeff")), 4, 0, 1);

    [Fact]
    public void EveryClientDtoRoundTrips()
    {
        var cases = new (ClientMessageCode Code, ProtocolPayload Payload)[]
        {
            (ClientMessageCode.Hello, new HelloDto(1, new LobbyId(ulong.MaxValue), "ABCDE12345", Session, "Alice")),
            (ClientMessageCode.SetName, new SetNameDto("Bob")),
            (ClientMessageCode.SetLobbyReady, new SetLobbyReadyDto(true)),
            (ClientMessageCode.SetSelection, new SetSelectionDto(9, [Card])),
            (ClientMessageCode.SetPickLocked, new SetPickLockedDto(9, true)),
            (ClientMessageCode.AssetNeed, new AssetNeedDto(Session, Page, [Hash])),
            (ClientMessageCode.PreparationReady, new PreparationReadyDto(Session)),
            (ClientMessageCode.PreparationFailed, new PreparationFailedDto(Session, ErrorCode.ClientAssetVerificationFailed)),
            (ClientMessageCode.ReopenLobbyJoin, new ReopenLobbyJoinDto("Alice")),
        };

        foreach (var item in cases)
        {
            var message = new ControlMessage(1, (int)item.Code, Guid.NewGuid(), item.Payload);
            var bytes = ControlMessageCodec.SerializeClient(message);
            var decoded = ControlMessageCodec.DeserializeClient(bytes);
            Assert.Equal(item.Payload.GetType(), decoded.Payload.GetType());
            Assert.Equal(bytes, ControlMessageCodec.SerializeClient(decoded));
        }
    }

    [Fact]
    public void EveryHostDtoFamilyRoundTrips()
    {
        var lobbyPlayer = new LobbyPlayerDto(Player, "Alice", 0, true, true, ConnectionStatus.Connected);
        var cardAsset = new CardAssetDto(Card, Hash);
        var cases = new (HostMessageCode Code, ProtocolPayload Payload)[]
        {
            (HostMessageCode.Welcome, new WelcomeDto(Player, Session, ReconnectResult.Reconnected)),
            (HostMessageCode.Error, new ErrorDto(ErrorCode.MalformedMessage, true)),
            (HostMessageCode.LobbySnapshot, new LobbySnapshotDto(Page, Player, true, [lobbyPlayer])),
            (HostMessageCode.CountdownStarted, new CountdownStartedDto(3000, 2)),
            (HostMessageCode.CountdownCancelled, new CountdownCancelledDto(2, CountdownCancellationReason.PlayerLeft)),
            (HostMessageCode.AssetManifest, new AssetManifestDto(Session, 1, 64, Page, [new AssetManifestEntryDto(Hash, 12, 2, 3, "image/webp", 1)])),
            (HostMessageCode.PreparationSnapshot, new PreparationSnapshotDto(Page, [new PlayerPreparationDto(Player, PreparationStatus.Ready)], 12, 12)),
            (HostMessageCode.DraftSnapshot, new DraftSnapshotDto(Page, 7, [cardAsset], [Card], [cardAsset], 1, true, false, [new DraftPlayerDto(Player, "Alice", 0, ConnectionStatus.Connected, false, 1)], DraftDirection.Clockwise, 1, 3)),
            (HostMessageCode.DraftCompleted, new DraftCompletedDto(Page, [new CompletedCardDto(Card, Hash, "Card Name")])),
            (HostMessageCode.LobbyAvailability, new LobbyAvailabilityDto(true)),
            (HostMessageCode.Kicked, new KickedDto(ErrorCode.Kicked)),
            (HostMessageCode.HostClosed, new HostClosedDto(ErrorCode.HostDisconnected)),
        };

        foreach (var item in cases)
        {
            var message = new ControlMessage(1, (int)item.Code, Guid.NewGuid(), item.Payload);
            var bytes = ControlMessageCodec.SerializeHost(message);
            var decoded = ControlMessageCodec.DeserializeHost(bytes);
            Assert.Equal(item.Payload.GetType(), decoded.Payload.GetType());
            Assert.Equal(bytes, ControlMessageCodec.SerializeHost(decoded));
        }
    }

    [Fact]
    public void LargeNumericIdentifiersAreStringsOnWire()
    {
        var message = new ControlMessage(1, 1, Guid.NewGuid(), new HelloDto(1, new LobbyId(ulong.MaxValue), null, null, "A"));
        var json = Encoding.UTF8.GetString(ControlMessageCodec.SerializeClient(message));
        Assert.Contains("\"lobbyId\":\"18446744073709551615\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownEnvelopeAndPayloadPropertiesAreRejected()
    {
        var requestId = Guid.NewGuid();
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(Json($"{{\"v\":1,\"code\":2,\"requestId\":\"{requestId}\",\"extra\":0,\"payload\":{{\"name\":\"A\"}}}}")));
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(Json($"{{\"v\":1,\"code\":2,\"requestId\":\"{requestId}\",\"payload\":{{\"name\":\"A\",\"extra\":0}}}}")));
    }

    [Fact]
    public void DuplicatePropertiesAreRejectedAtAnyLevel()
    {
        var requestId = Guid.NewGuid();
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(Json($"{{\"v\":1,\"v\":1,\"code\":2,\"requestId\":\"{requestId}\",\"payload\":{{\"name\":\"A\"}}}}")));
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(Json($"{{\"v\":1,\"code\":2,\"requestId\":\"{requestId}\",\"payload\":{{\"name\":\"A\",\"name\":\"B\"}}}}")));
    }

    [Fact]
    public void UnknownAndWrongDirectionCodesAreRejected()
    {
        var requestId = Guid.NewGuid();
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(Json($"{{\"v\":1,\"code\":99,\"requestId\":\"{requestId}\",\"payload\":{{}}}}")));
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(Json($"{{\"v\":1,\"code\":101,\"requestId\":\"{requestId}\",\"payload\":{{}}}}")));
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeHost(Json($"{{\"v\":1,\"code\":1,\"requestId\":\"{requestId}\",\"payload\":{{}}}}")));
    }

    [Fact]
    public void PhaseInvalidCodesAreRejected()
    {
        MessagePhaseValidator.ValidateClient(ClientMessageCode.SetSelection, ProtocolPhase.Drafting);
        MessagePhaseValidator.ValidateHost(HostMessageCode.LobbySnapshot, ProtocolPhase.LobbyOpen);
        Assert.Throws<ProtocolException>(() => MessagePhaseValidator.ValidateClient(ClientMessageCode.SetSelection, ProtocolPhase.LobbyOpen));
        Assert.Throws<ProtocolException>(() => MessagePhaseValidator.ValidateHost(HostMessageCode.DraftSnapshot, ProtocolPhase.PreparingAssets));
    }

    [Fact]
    public void UnknownEnumCodesAreRejected()
    {
        var requestId = Guid.NewGuid();
        var json = Json($"{{\"v\":1,\"code\":102,\"requestId\":\"{requestId}\",\"payload\":{{\"errorCode\":999,\"fatal\":true}}}}" );
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeHost(json));
    }

    [Fact]
    public void OversizedJsonIsRejectedBeforeParsing()
    {
        var json = Json("{\"notEvenValid\":");
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(json, json.Length - 1));
    }

    [Fact]
    public void JsonDeeperThanSixteenIsRejected()
    {
        var requestId = Guid.NewGuid();
        var nested = string.Concat(Enumerable.Repeat("{\"x\":", 17)) + "0" + new string('}', 17);
        var json = Json($"{{\"v\":1,\"code\":2,\"requestId\":\"{requestId}\",\"payload\":{nested}}}");
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(json));
    }

    [Fact]
    public void InvalidIdsAndPageBoundsAreRejected()
    {
        var requestId = Guid.NewGuid();
        var invalidId = Json($"{{\"v\":1,\"code\":7,\"requestId\":\"{requestId}\",\"payload\":{{\"sessionId\":\"00000000-0000-0000-0000-000000000000\"}}}}" );
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.DeserializeClient(invalidId));

        var hashes = Enumerable.Repeat(Hash, 201).ToArray();
        var payload = new AssetNeedDto(Session, Page, hashes);
        Assert.Throws<ProtocolException>(() => ControlMessageCodec.SerializeClient(new ControlMessage(1, 6, requestId, payload)));
    }

    private static byte[] Json(string value) => Encoding.UTF8.GetBytes(value);
}
