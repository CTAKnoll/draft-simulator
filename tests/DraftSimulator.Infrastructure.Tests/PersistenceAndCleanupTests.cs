using System.Text.Json;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class PersistenceAndCleanupTests
{
    [Fact]
    public void ClientStateRoundTripsAndAtomicUpdateLeavesNoTemporaryFiles()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("app", "client-state.json");
        var store = new ClientStateStore(path);
        var first = new ClientState(1, 2, Guid.NewGuid(), "ABCDE12345", "Alice", false);
        var second = first with { LastAcceptedPlayerName = "Bob", PreviousSessionEndedNormally = true };

        store.Save(first);
        store.Save(second);

        Assert.Equal(second, store.Load());
        Assert.Single(Directory.GetFiles(temporary.Combine("app")));
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("Bob", document.RootElement.GetProperty("lastAcceptedPlayerName").GetString());
    }

    [Fact]
    public void ClientStateRejectsUnknownPersistedFields()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("client-state.json");
        File.WriteAllText(path, "{\"unknown\":true}");
        Assert.Throws<JsonException>(() => new ClientStateStore(path).Load());
    }

    [Fact]
    public void CleanupPreservesReconnectSessionAndDeletesOthers()
    {
        using var temporary = new TemporaryDirectory();
        var keep = Guid.NewGuid();
        var remove = Guid.NewGuid();
        Directory.CreateDirectory(temporary.Combine(keep.ToString("D"), "assets"));
        Directory.CreateDirectory(temporary.Combine(remove.ToString("D"), "assets"));
        var manager = new SessionCacheManager(temporary.Path);

        var result = manager.DeleteAbandonedSessions(keep);

        Assert.True(result.Succeeded);
        Assert.True(Directory.Exists(temporary.Combine(keep.ToString("D"))));
        Assert.False(Directory.Exists(temporary.Combine(remove.ToString("D"))));
        Assert.True(manager.DeleteCompletedSession(keep).Succeeded);
        Assert.False(Directory.Exists(temporary.Combine(keep.ToString("D"))));
    }

    [Fact]
    public void OfflineProfilePersistsIdentityAndIsolatesApplicationData()
    {
        using var temporary = new TemporaryDirectory();
        SteamPeerId firstId;
        using (var first = OfflineDebugProfile.Open(temporary.Path, "client-1"))
        {
            firstId = first.PeerId;
            Assert.NotEqual(0UL, firstId.Value);
            Assert.Contains(Path.Combine("OfflineDebug", "Profiles", "client-1"), first.Paths.Root, StringComparison.Ordinal);
            Assert.Contains(Path.Combine("OfflineDebug", "Lobbies"), first.SharedLobbyDirectory, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => OfflineDebugProfile.Open(temporary.Path, "client-1"));
        }

        using var reopened = OfflineDebugProfile.Open(temporary.Path, "client-1");
        using var other = OfflineDebugProfile.Open(temporary.Path, "client-2");
        Assert.Equal(firstId, reopened.PeerId);
        Assert.NotEqual(reopened.PeerId, other.PeerId);
        Assert.NotEqual(reopened.Paths.Root, other.Paths.Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("spaces are invalid")]
    [InlineData("../escape")]
    public void OfflineProfileRejectsUnsafeNames(string name)
    {
        using var temporary = new TemporaryDirectory();
        Assert.Throws<ArgumentException>(() => OfflineDebugProfile.Open(temporary.Path, name));
    }
}
