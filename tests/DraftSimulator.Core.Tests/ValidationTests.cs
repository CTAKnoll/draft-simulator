using DraftSimulator.Core;

namespace DraftSimulator.Core.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void RejectsPlayerCountOutsideOneThroughEight(int count)
    {
        var result = DraftSettingsValidator.Validate(TestData.Settings(), count, TestData.Definitions(4));
        Assert.Contains(result.Issues, x => x.Code == ValidationCode.PlayerCount);
    }

    [Fact]
    public void RejectsNonPositiveSizesAndConfiguredLimits()
    {
        var settings = TestData.Settings(packSize: 0, pickSize: 0, rounds: 0);
        var result = DraftSettingsValidator.Validate(settings, 1, [], new DraftLimits(-1, -1));

        Assert.Contains(result.Issues, x => x.Code == ValidationCode.PackSize);
        Assert.Contains(result.Issues, x => x.Code == ValidationCode.CardsPerPick);
        Assert.Contains(result.Issues, x => x.Code == ValidationCode.PacksPerPlayer);
        Assert.Contains(result.Issues, x => x.Code == ValidationCode.MaxPackSize);
    }

    [Fact]
    public void RejectsInvalidRarityRangesWeightsAndComposition()
    {
        var rarities = TestData.OnlyCommon(1);
        var mutable = rarities.ToDictionary();
        mutable[Rarity.Common] = new RaritySettings(2, 1, double.NaN);
        var result = DraftSettingsValidator.Validate(TestData.Settings(packSize: 3, rarities: mutable), 1, TestData.Definitions(3));

        Assert.Contains(result.Issues, x => x.Code == ValidationCode.RarityRange);
        Assert.Contains(result.Issues, x => x.Code == ValidationCode.RarityWeight);
        Assert.Contains(result.Issues, x => x.Code == ValidationCode.MaximumsCannotFillPack);
    }

    [Fact]
    public void ZeroWeightAndFixedRaritiesContributeOnlyTheirMinimum()
    {
        var rarities = TestData.OnlyCommon(10, minimum: 2, weight: 0).ToDictionary();
        rarities[Rarity.Rare] = new RaritySettings(1, 5, 1);
        var settings = TestData.Settings(packSize: 5, rarities: rarities);

        var result = DraftSettingsValidator.Validate(settings, 1,
            [.. TestData.Definitions(1, Rarity.Common), .. TestData.Definitions(1, Rarity.Rare)]);

        Assert.True(result.IsValid);
        Assert.Equal(2, settings.Rarities[Rarity.Common].EffectiveMaximum);
    }

    [Fact]
    public void RejectsWhenPositiveWeightsCannotFillRemainingSlots()
    {
        var rarities = TestData.OnlyCommon(2, minimum: 1, weight: 1).ToDictionary();
        rarities[Rarity.Rare] = new RaritySettings(1, 10, 0);
        var result = DraftSettingsValidator.Validate(TestData.Settings(packSize: 4, rarities: rarities), 1,
            [.. TestData.Definitions(1), .. TestData.Definitions(1, Rarity.Rare)]);

        Assert.Contains(result.Issues, x => x.Code == ValidationCode.WeightedRaritiesCannotFillPack);
    }

    [Fact]
    public void NoReplacementUsesConservativePerRarityCapacity()
    {
        var settings = TestData.Settings(packSize: 3, rounds: 2, replacement: ReplacementMode.WithoutReplacement);
        var result = DraftSettingsValidator.Validate(settings, 2, TestData.Definitions(11));

        var issue = Assert.Single(result.Issues.Where(x => x.Code == ValidationCode.RarityCapacity));
        Assert.Equal(Rarity.Common, issue.Rarity);
    }

    [Theory]
    [InlineData(" Alice ", "Alice", true)]
    [InlineData("", "", false)]
    [InlineData("                 ", "", false)]
    [InlineData("0123456789abcdefg", "0123456789abcdefg", false)]
    [InlineData("bad\nname", "bad\nname", false)]
    public void PlayerNamesAreNormalizedAndValidated(string input, string expected, bool valid)
    {
        Assert.Equal(valid, PlayerNameValidator.TryNormalize(input, [], out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void PlayerNamesAreUniqueIgnoringCase()
    {
        Assert.False(PlayerNameValidator.TryNormalize("alice", ["Alice"], out _));
    }

    [Fact]
    public void AssetHashesAreCanonicalAndValidated()
    {
        var hash = AssetHash.Compute([1, 2, 3]);
        Assert.Equal(64, hash.Value.Length);
        Assert.Equal(hash.Value.ToLowerInvariant(), hash.Value);
        Assert.True(AssetHash.TryParse(hash.Value.ToUpperInvariant(), out var parsed));
        Assert.Equal(hash, parsed);
        Assert.False(AssetHash.TryParse("invalid", out _));
    }

    [Fact]
    public void ExportNamesReplaceControlSeparatorsAndLimitUnicodeScalars()
    {
        var input = "  A\r\n\t\0" + string.Concat(Enumerable.Repeat("😀", 300)) + "  ";
        var result = ExportNameSanitizer.Sanitize(input);

        Assert.StartsWith("A    😀", result);
        Assert.Equal(256, result.EnumerateRunes().Count());
    }
}
