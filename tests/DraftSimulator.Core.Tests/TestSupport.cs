using DraftSimulator.Core;

namespace DraftSimulator.Core.Tests;

internal sealed class PredictableRandom(params int[] integers) : IRandomSource
{
    private readonly Queue<int> _integers = new(integers);

    public int NextInt(int exclusiveMaximum)
    {
        if (exclusiveMaximum <= 0)
            throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        return (_integers.Count == 0 ? 0 : _integers.Dequeue()) % exclusiveMaximum;
    }

    public double NextDouble() => 0;
}

internal static class TestData
{
    public static DraftSettings Settings(
        int packSize = 4,
        int pickSize = 1,
        int rounds = 1,
        ReplacementMode replacement = ReplacementMode.WithReplacement,
        DirectionRule direction = DirectionRule.AlwaysClockwise,
        IReadOnlyDictionary<Rarity, RaritySettings>? rarities = null) =>
        new(replacement, rarities ?? OnlyCommon(packSize), packSize, pickSize, rounds, direction);

    public static IReadOnlyDictionary<Rarity, RaritySettings> OnlyCommon(int maximum, int minimum = 0, double weight = 1)
    {
        var result = Enum.GetValues<Rarity>().ToDictionary(x => x, _ => new RaritySettings(0, 0, 0));
        result[Rarity.Common] = new RaritySettings(minimum, maximum, weight);
        return result;
    }

    public static List<CardDefinition> Definitions(int count, Rarity rarity = Rarity.Common) =>
        Enumerable.Range(0, count)
            .Select(index => new CardDefinition(
                CardDefinitionId.New(),
                $"/host/card-{index}.png",
                $"card-{index}",
                rarity,
                AssetHash.Compute(BitConverter.GetBytes(index))))
            .ToList();

    public static List<DraftPlayer> Players(int count) =>
        Enumerable.Range(1, count)
            .Select(index => new DraftPlayer(PlayerId.New(), (ulong)index, $"Player {index}"))
            .ToList();

    public static DraftStateMachine Machine(
        int playerCount = 2,
        int packSize = 4,
        int pickSize = 1,
        int rounds = 1,
        DirectionRule direction = DirectionRule.AlwaysClockwise,
        params int[] randomValues)
    {
        var settings = Settings(packSize, pickSize, rounds, direction: direction);
        var players = Players(playerCount);
        var generated = new PackGenerator(new PredictableRandom()).Generate(settings, players, Definitions(packSize));
        return new DraftStateMachine(settings, generated, players[0].Id, new PredictableRandom(randomValues));
    }

    public static void SelectAndLock(DraftStateMachine machine, PlayerState player, int cardIndex = 0)
    {
        var card = machine.Packs[player.CurrentPackId!.Value].Cards[cardIndex];
        Assert.True(machine.SetSelection(player.Id, machine.PickRevision, [card]).Succeeded);
        Assert.True(machine.SetPickLocked(player.Id, machine.PickRevision, true).Succeeded);
    }
}
