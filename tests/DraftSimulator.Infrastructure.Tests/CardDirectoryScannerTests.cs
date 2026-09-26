using DraftSimulator.Core;
using DraftSimulator.Infrastructure;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class CardDirectoryScannerTests
{
    [Fact]
    public void ScansImmediateRarityFoldersAndExtensionsCaseInsensitivelyWithoutRecursing()
    {
        using var temporary = new TemporaryDirectory();
        ImageTestData.Write(temporary.Combine("cOmMoN", "Alpha.PNG"), 12, 8);
        ImageTestData.Write(temporary.Combine("COMMON", "Beta.JpEg"), 9, 11, SkiaSharp.SKEncodedImageFormat.Jpeg);
        ImageTestData.Write(temporary.Combine("COMMON", "nested", "Ignored.png"), 4, 4);
        ImageTestData.Write(temporary.Combine("Other", "IgnoredToo.png"), 4, 4);

        var result = new CardDirectoryScanner().Scan(temporary.Path, HostConfiguration.Defaults);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.Definitions.Count);
        Assert.All(result.Definitions, definition => Assert.Equal(Rarity.Common, definition.Rarity));
        Assert.Contains(result.Definitions, definition => definition.FallbackName == "Alpha");
        Assert.Contains(result.Definitions, definition => definition.FallbackName == "Beta");
    }

    [Fact]
    public void ScansScryfallSpecialAndBonusRarityFolders()
    {
        using var temporary = new TemporaryDirectory();
        ImageTestData.Write(temporary.Combine("sPeCiAl", "Special [1].png"), 12, 8);
        ImageTestData.Write(temporary.Combine("BONUS", "Bonus [2].png"), 9, 11);

        var result = new CardDirectoryScanner().Scan(temporary.Path, HostConfiguration.Defaults);

        Assert.True(result.IsValid);
        Assert.Equal(Rarity.Special, Assert.Single(result.Definitions, x => x.FallbackName == "Special [1]").Rarity);
        Assert.Equal(Rarity.Bonus, Assert.Single(result.Definitions, x => x.FallbackName == "Bonus [2]").Rarity);
    }

    [Fact]
    public void DuplicateFallbackNamesAreBlockingAcrossRarities()
    {
        using var temporary = new TemporaryDirectory();
        ImageTestData.Write(temporary.Combine("Common", "Card.png"), 4, 4);
        ImageTestData.Write(temporary.Combine("Rare", "card.JPG"), 4, 4, SkiaSharp.SKEncodedImageFormat.Jpeg);

        var result = new CardDirectoryScanner().Scan(temporary.Path, HostConfiguration.Defaults);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == CardScanErrorCode.DuplicateFallbackName);
        Assert.Empty(result.Definitions);
    }

    [Fact]
    public void CandidateCountLimitIsBlocking()
    {
        using var temporary = new TemporaryDirectory();
        ImageTestData.Write(temporary.Combine("Common", "One.png"), 2, 2);
        ImageTestData.Write(temporary.Combine("Common", "Two.png"), 2, 2);
        var limits = HostConfiguration.Defaults with { MaxCardCount = 1 };

        var result = new CardDirectoryScanner().Scan(temporary.Path, limits);

        Assert.Contains(result.Errors, error => error.Code == CardScanErrorCode.TooManyCards);
    }

    [Fact]
    public void InvalidOversizedAndOverPixelImagesProduceWarningsAndAreSkipped()
    {
        using var temporary = new TemporaryDirectory();
        ImageTestData.Write(temporary.Combine("Common", "Good.png"), 5, 5);
        ImageTestData.Write(temporary.Combine("Common", "Pixels.png"), 20, 20);
        File.WriteAllBytes(temporary.Combine("Common", "Broken.png"), [1, 2, 3, 4]);
        var large = temporary.Combine("Common", "Large.png");
        ImageTestData.Write(large, 3, 3);
        using (var stream = File.OpenWrite(large))
            stream.SetLength(1000);
        var limits = HostConfiguration.Defaults with { MaxSourceImageBytes = 500, MaxDecodedPixels = 100 };

        var result = new CardDirectoryScanner().Scan(temporary.Path, limits);

        Assert.Single(result.Definitions);
        Assert.Equal("Good", result.Definitions[0].FallbackName);
        Assert.Contains(result.Warnings, warning => warning.Code == CardScanWarningCode.InvalidImage);
        Assert.Contains(result.Warnings, warning => warning.Code == CardScanWarningCode.SourceTooLarge);
        Assert.Contains(result.Warnings, warning => warning.Code == CardScanWarningCode.PixelLimitExceeded);
    }

    [Fact]
    public void DataCsvProducesDeferredEnrichmentWarning()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(temporary.Combine("data.csv"), "name");
        var result = new CardDirectoryScanner().Scan(temporary.Path, HostConfiguration.Defaults);
        Assert.Contains(result.Warnings, warning => warning.Code == CardScanWarningCode.DataCsvUnsupported);
    }
}
