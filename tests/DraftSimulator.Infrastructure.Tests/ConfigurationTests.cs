using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void CreatesDocumentedDefaultsAndLoadsThem()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("app", "host.ini");

        var configuration = new HostConfigurationStore(path).CreateDefaultIfMissingAndLoad();

        Assert.Equal(HostConfiguration.Defaults with { ScryfallDirectory = temporary.Combine("app", "Scryfall") }, configuration);
        Assert.Contains("[Assets]", File.ReadAllText(path));
        Assert.Contains("[Scryfall]", File.ReadAllText(path));
        Assert.Equal(temporary.Combine("app", "Scryfall"), configuration.ScryfallDirectory);
        Assert.Equal(80, configuration.OutputWebPQuality);
    }

    [Fact]
    public void ScryfallDirectoryCanBeOverriddenAndOldIniGetsTheAppDataDefault()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("host.ini");
        var store = new HostConfigurationStore(path);
        store.CreateDefaultIfMissingAndLoad();
        var custom = temporary.Combine("Imported Sets");
        File.WriteAllText(path, File.ReadAllText(path).Replace(
            $"Directory={temporary.Combine("Scryfall")}", $"Directory={custom}", StringComparison.Ordinal));
        var configuration = store.Load();
        Assert.Equal(custom, configuration.ScryfallDirectory);

        var legacyPath = temporary.Combine("legacy", "host.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, File.ReadAllText(path).Replace($"Directory={custom}{Environment.NewLine}", string.Empty, StringComparison.Ordinal));
        var legacy = new HostConfigurationStore(legacyPath).Load();
        Assert.Equal(temporary.Combine("legacy", "Scryfall"), legacy.ScryfallDirectory);
    }

    [Theory]
    [InlineData("OutputWebPQuality=0")]
    [InlineData("OutputWebPQuality=101")]
    [InlineData("MaxCardCount=-1")]
    [InlineData("MaxCardCount=not-a-number")]
    public void RejectsInvalidValues(string replacement)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("host.ini");
        new HostConfigurationStore(path).CreateDefaultIfMissingAndLoad();
        var text = File.ReadAllText(path);
        text = replacement.StartsWith("Output", StringComparison.Ordinal)
            ? text.Replace("OutputWebPQuality=80", replacement, StringComparison.Ordinal)
            : text.Replace("MaxCardCount=1000", replacement, StringComparison.Ordinal);
        File.WriteAllText(path, text);

        Assert.Throws<HostConfigurationException>(() => new HostConfigurationStore(path).Load());
    }

    [Fact]
    public void RejectsMissingValue()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("host.ini");
        File.WriteAllText(path, "[Assets]\nMaxCardCount=1\n");
        Assert.Throws<HostConfigurationException>(() => new HostConfigurationStore(path).Load());
    }

    [Fact]
    public void BuildsLocalAppDataTreePaths()
    {
        using var temporary = new TemporaryDirectory();
        var paths = new LocalAppDataPaths(temporary.Path);
        Assert.Equal(temporary.Combine("DraftSimulator", "host.ini"), paths.HostConfigurationFile);
        Assert.Equal(temporary.Combine("DraftSimulator", "Sessions"), paths.SessionsDirectory);
    }
}
