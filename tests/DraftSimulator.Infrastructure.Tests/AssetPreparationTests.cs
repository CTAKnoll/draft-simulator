using DraftSimulator.Core;
using DraftSimulator.Infrastructure;
using SkiaSharp;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class AssetPreparationTests
{
    [Fact]
    public void TransformsOnlyRequestedDefinitionsResizesAndDoesNotUpscale()
    {
        using var temporary = new TemporaryDirectory();
        var largePath = temporary.Combine("source", "Large.png");
        var smallPath = temporary.Combine("source", "Small.png");
        var unusedPath = temporary.Combine("source", "Unused.png");
        ImageTestData.Write(largePath, 200, 100);
        ImageTestData.Write(smallPath, 20, 30);
        ImageTestData.Write(unusedPath, 40, 40);
        var large = Definition(largePath, "Large");
        var small = Definition(smallPath, "Small");
        var limits = HostConfiguration.Defaults with { OutputMaxLongEdge = 120 };

        var result = new AssetPreparer(new FixedDiskSpace(long.MaxValue)).Prepare(
            Guid.NewGuid(), [large, small], temporary.Combine("session"), limits);

        Assert.Equal(2, result.Definitions.Count);
        Assert.Equal(2, result.Manifest.Assets.Count);
        Assert.Contains(result.Manifest.Assets, asset => asset.PixelWidth == 120 && asset.PixelHeight == 60);
        Assert.Contains(result.Manifest.Assets, asset => asset.PixelWidth == 20 && asset.PixelHeight == 30);
        Assert.Equal(2, Directory.GetFiles(temporary.Combine("session", "assets"), "*.webp").Length);
        Assert.All(result.Manifest.Assets, asset => Assert.Equal("image/webp", asset.MimeType));
        Assert.True(File.Exists(temporary.Combine("session", "manifest.json")));
        Assert.Empty(Directory.GetFiles(temporary.Combine("session"), "*.partial"));
    }

    [Fact]
    public void DeduplicatesEqualTransformedContentAndLeavesUnrelatedPartialFile()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = temporary.Combine("source", "First.png");
        var secondPath = temporary.Combine("source", "Second.png");
        ImageTestData.Write(firstPath, 20, 20);
        File.Copy(firstPath, secondPath);
        var assets = temporary.Combine("session", "assets");
        Directory.CreateDirectory(assets);
        var interrupted = Path.Combine(assets, "interrupted.partial");
        File.WriteAllText(interrupted, "partial");

        var result = new AssetPreparer(new FixedDiskSpace(long.MaxValue)).Prepare(
            Guid.NewGuid(), [Definition(firstPath, "First"), Definition(secondPath, "Second")],
            temporary.Combine("session"), HostConfiguration.Defaults);

        Assert.Single(result.Manifest.Assets);
        Assert.Equal(2, result.Definitions.Count);
        Assert.Equal(result.Definitions[0].AssetHash, result.Definitions[1].AssetHash);
        Assert.True(File.Exists(interrupted));
    }

    [Fact]
    public void ReplacesInvalidHashCacheEntryAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Combine("source.png");
        ImageTestData.Write(source, 25, 15);
        var definition = Definition(source, "Card");
        var session = temporary.Combine("session");
        var preparer = new AssetPreparer(new FixedDiskSpace(long.MaxValue));
        var initial = preparer.Prepare(Guid.NewGuid(), [definition], session, HostConfiguration.Defaults);
        var path = Path.Combine(session, "assets", $"{initial.Manifest.Assets[0].Hash}.webp");
        File.WriteAllText(path, "corrupt");

        var repaired = preparer.Prepare(Guid.NewGuid(), [definition], session, HostConfiguration.Defaults);

        using var codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        Assert.Equal(repaired.Manifest.Assets[0].EncodedByteLength, new FileInfo(path).Length);
        Assert.Empty(Directory.GetFiles(Path.Combine(session, "assets"), "*.partial"));
    }

    [Fact]
    public void RejectsPixelSessionAndFreeSpaceLimitViolations()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Combine("source.png");
        ImageTestData.Write(source, 20, 20);
        var definition = Definition(source, "Card");

        Assert.Throws<AssetPreparationException>(() => new AssetPreparer(new FixedDiskSpace(long.MaxValue)).Prepare(
            Guid.NewGuid(), [definition], temporary.Combine("pixels"), HostConfiguration.Defaults with { MaxDecodedPixels = 399 }));
        Assert.Throws<AssetPreparationException>(() => new AssetPreparer(new FixedDiskSpace(long.MaxValue)).Prepare(
            Guid.NewGuid(), [definition], temporary.Combine("size"), HostConfiguration.Defaults with { MaxSessionAssetBytes = 1, MaxMemoryCacheBytes = 1 }));
        Assert.Throws<AssetPreparationException>(() => new AssetPreparer(new FixedDiskSpace(AssetPreparer.RequiredFreeSpaceReserve)).Prepare(
            Guid.NewGuid(), [definition], temporary.Combine("disk"), HostConfiguration.Defaults));
    }

    private static CardDefinition Definition(string path, string name) =>
        new(CardDefinitionId.New(), path, name, Rarity.Common);

    private sealed class FixedDiskSpace(long bytes) : IDiskSpaceProvider
    {
        public long GetAvailableBytes(string path) => bytes;
    }
}
