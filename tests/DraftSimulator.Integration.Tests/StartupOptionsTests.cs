using DraftSimulator.App;

namespace DraftSimulator.Integration.Tests;

public sealed class StartupOptionsTests
{
    [Fact]
#if DEBUG
    public void ParsesOfflineProfile()
    {
        var options = StartupOptions.Parse(["--offline", "--profile", "client1"]);

        Assert.True(options.Offline);
        Assert.Equal("client1", options.Profile);
    }
#else
    public void ReleaseBuildRejectsOfflineProfile()
    {
        Assert.Throws<InvalidOperationException>(() => StartupOptions.Parse(["--offline", "--profile", "client1"]));
    }
#endif

    [Fact]
    public void DefaultsToSteamMode()
    {
        Assert.Equal(new StartupOptions(false, null), StartupOptions.Parse([]));
    }

    [Fact]
    public void DedicatedDebugArtifactDefaultsToOfflineHostProfile()
    {
#if DEBUG
        Assert.Equal(new StartupOptions(true, "host"), StartupOptions.Parse([], offlineArtifact: true));
        Assert.Equal(new StartupOptions(true, "client1"), StartupOptions.Parse(["--profile", "client1"], offlineArtifact: true));
#else
        Assert.Throws<InvalidOperationException>(() => StartupOptions.Parse([], offlineArtifact: true));
#endif
    }

    [Theory]
    [InlineData("--offline")]
    [InlineData("--profile")]
    [InlineData("--profile", "client1")]
    public void RejectsIncompleteOrMisplacedProfileArguments(params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() => StartupOptions.Parse(arguments));
    }
}
