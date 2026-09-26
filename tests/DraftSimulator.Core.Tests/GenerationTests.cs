using DraftSimulator.Core;

namespace DraftSimulator.Core.Tests;

public sealed class GenerationTests
{
    [Fact]
    public void GeneratesEveryRoundAndPackBeforeDrafting()
    {
        var settings = TestData.Settings(packSize: 3, rounds: 2);
        var generated = new PackGenerator(new PredictableRandom()).Generate(settings, TestData.Players(3), TestData.Definitions(3));

        Assert.Equal(2, generated.PackRounds.Count);
        Assert.All(generated.PackRounds, round => Assert.Equal(3, round.Count));
        Assert.All(generated.PackRounds.SelectMany(x => x), pack => Assert.Equal(3, pack.Cards.Count));
        Assert.Equal(18, generated.Instances.Count);
    }

    [Fact]
    public void ReplacementAllowsARepeatedDefinitionButCreatesUniqueInstances()
    {
        var generated = new PackGenerator(new PredictableRandom()).Generate(
            TestData.Settings(packSize: 4), TestData.Players(1), TestData.Definitions(1));
        var instances = generated.Instances.Values.ToArray();

        Assert.Single(instances.Select(x => x.DefinitionId).Distinct());
        Assert.Equal(4, instances.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void NoReplacementIsGloballyUniqueAcrossRoundsAndPacks()
    {
        var settings = TestData.Settings(packSize: 2, rounds: 2, replacement: ReplacementMode.WithoutReplacement);
        var generated = new PackGenerator(new PredictableRandom()).Generate(settings, TestData.Players(2), TestData.Definitions(8));

        Assert.Equal(8, generated.Instances.Values.Select(x => x.DefinitionId).Distinct().Count());
    }

    [Fact]
    public void HonorsMinimumMaximumAndDeterministicWeightedChoice()
    {
        var rarities = TestData.OnlyCommon(1, minimum: 1).ToDictionary();
        rarities[Rarity.Rare] = new RaritySettings(0, 2, 1);
        var definitions = new[] { TestData.Definitions(1), TestData.Definitions(1, Rarity.Rare) }.SelectMany(x => x).ToList();
        var generated = new PackGenerator(new PredictableRandom()).Generate(
            TestData.Settings(packSize: 3, rarities: rarities), TestData.Players(1), definitions);
        var rarityByDefinition = definitions.ToDictionary(x => x.Id, x => x.Rarity);
        var generatedRarities = generated.Instances.Values.Select(x => rarityByDefinition[x.DefinitionId]).ToArray();

        Assert.Equal(1, generatedRarities.Count(x => x == Rarity.Common));
        Assert.Equal(2, generatedRarities.Count(x => x == Rarity.Rare));
    }

    [Fact]
    public void ShufflesPlayerOrderUsingInjectedRandomness()
    {
        var players = TestData.Players(3);
        var generated = new PackGenerator(new PredictableRandom(0, 0)).Generate(
            TestData.Settings(packSize: 1), players, TestData.Definitions(1));

        Assert.Equal([players[1].Id, players[2].Id, players[0].Id], generated.PlayerOrder.Select(x => x.Id));
    }

    [Fact]
    public void RejectsInvalidOrDuplicatePlayerNames()
    {
        var players = TestData.Players(2);
        players[1] = players[1] with { Name = players[0].Name.ToLowerInvariant() };

        Assert.Throws<ArgumentException>(() => new PackGenerator(new PredictableRandom()).Generate(
            TestData.Settings(packSize: 1), players, TestData.Definitions(1)));
    }
}
