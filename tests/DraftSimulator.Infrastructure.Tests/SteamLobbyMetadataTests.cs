using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class SteamLobbyMetadataTests
{
    [Fact]
    public void CreatesExactDocumentedMetadata()
    {
        var metadata = SteamLobbyMetadata.Create("01234-ABCDE");

        Assert.Equal(4, metadata.Count);
        Assert.Equal("draft-simulator", metadata["ds_app"]);
        Assert.Equal("1", metadata["ds_protocol"]);
        Assert.Equal(SteamRoomCode.Hash("01234ABCDE"), metadata["ds_room_hash"]);
        Assert.Equal("1", metadata["ds_open"]);
        Assert.Equal(8, SteamLobbyMetadata.MemberLimit);
    }

    [Fact]
    public void SearchUsesAllFourExactOpenLobbyValues()
    {
        var filters = SteamLobbyMetadata.SearchFilters("01234ABCDE");

        Assert.Equal(SteamLobbyMetadata.Create("01234ABCDE"), filters);
        Assert.Equal("1", filters[SteamLobbyMetadata.OpenKey]);
    }

    [Fact]
    public void ClosedMetadataOnlyChangesOpenValue()
    {
        var metadata = SteamLobbyMetadata.Create("01234ABCDE", isOpen: false);
        Assert.Equal("0", metadata[SteamLobbyMetadata.OpenKey]);
    }

    [Fact]
    public void ReadsAppIdFromBuildConfiguration() =>
        Assert.NotEqual(0u, SteamTransportOptions.FromBuildConfiguration().ExpectedAppId);
}
