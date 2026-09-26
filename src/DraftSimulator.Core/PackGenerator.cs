namespace DraftSimulator.Core;

public sealed class GeneratedDraft
{
    internal GeneratedDraft(
        IReadOnlyList<DraftPlayer> playerOrder,
        IReadOnlyList<IReadOnlyList<Pack>> packRounds,
        IReadOnlyDictionary<CardInstanceId, CardInstance> instances)
    {
        PlayerOrder = playerOrder;
        PackRounds = packRounds;
        Instances = instances;
    }

    public IReadOnlyList<DraftPlayer> PlayerOrder { get; }
    public IReadOnlyList<IReadOnlyList<Pack>> PackRounds { get; }
    public IReadOnlyDictionary<CardInstanceId, CardInstance> Instances { get; }
}

public sealed class PackGenerator
{
    private readonly IRandomSource _random;

    public PackGenerator(IRandomSource random) => _random = random ?? throw new ArgumentNullException(nameof(random));

    public GeneratedDraft Generate(
        DraftSettings settings,
        IReadOnlyCollection<DraftPlayer> players,
        IReadOnlyCollection<CardDefinition> definitions,
        DraftLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(players);
        ArgumentNullException.ThrowIfNull(definitions);
        var validation = DraftSettingsValidator.Validate(settings, players.Count, definitions, limits);
        if (!validation.IsValid)
            throw new ArgumentException(string.Join(" ", validation.Issues.Select(x => x.Message)), nameof(settings));
        if (players.Select(x => x.Id).Distinct().Count() != players.Count || players.Select(x => x.SteamId).Distinct().Count() != players.Count)
            throw new ArgumentException("Player IDs and Steam IDs must be unique.", nameof(players));

        var acceptedNames = new List<string>(players.Count);
        foreach (var player in players)
        {
            if (!PlayerNameValidator.TryNormalize(player.Name, acceptedNames, out var normalized) || normalized != player.Name)
                throw new ArgumentException("Player names must be normalized, valid, and unique.", nameof(players));
            acceptedNames.Add(normalized);
        }

        var order = players.ToList();
        _random.Shuffle(order);
        var pools = definitions.GroupBy(x => x.Rarity).ToDictionary(x => x.Key, x => x.ToList());
        var instances = new Dictionary<CardInstanceId, CardInstance>();
        var rounds = new List<IReadOnlyList<Pack>>(settings.PacksPerPlayer);

        for (var round = 0; round < settings.PacksPerPlayer; round++)
        {
            var packs = new List<Pack>(order.Count);
            foreach (var owner in order)
            {
                var cards = GeneratePack(settings, pools, instances);
                packs.Add(new Pack(PackId.New(), cards, owner.Id));
            }

            rounds.Add(packs);
        }

        return new GeneratedDraft(order, rounds, instances);
    }

    private IReadOnlyList<CardInstanceId> GeneratePack(
        DraftSettings settings,
        IDictionary<Rarity, List<CardDefinition>> pools,
        IDictionary<CardInstanceId, CardInstance> instances)
    {
        var cards = new List<CardInstanceId>(settings.PackSize);
        var counts = Enum.GetValues<Rarity>().ToDictionary(x => x, _ => 0);

        foreach (var rarity in Enum.GetValues<Rarity>())
        {
            for (var count = 0; count < settings.Rarities[rarity].Minimum; count++)
                AddRandomCard(rarity, settings, pools, cards, instances, counts);
        }

        while (cards.Count < settings.PackSize)
        {
            var eligible = Enum.GetValues<Rarity>()
                .Where(r => settings.Rarities[r].Weight > 0 && counts[r] < settings.Rarities[r].EffectiveMaximum)
                .ToArray();
            var totalWeight = eligible.Sum(r => settings.Rarities[r].Weight);
            var target = _random.NextDouble() * totalWeight;
            var chosen = eligible[^1];
            foreach (var rarity in eligible)
            {
                target -= settings.Rarities[rarity].Weight;
                if (target < 0)
                {
                    chosen = rarity;
                    break;
                }
            }

            AddRandomCard(chosen, settings, pools, cards, instances, counts);
        }

        return cards;
    }

    private void AddRandomCard(
        Rarity rarity,
        DraftSettings settings,
        IDictionary<Rarity, List<CardDefinition>> pools,
        ICollection<CardInstanceId> cards,
        IDictionary<CardInstanceId, CardInstance> instances,
        IDictionary<Rarity, int> counts)
    {
        var pool = pools[rarity];
        var index = _random.NextInt(pool.Count);
        var definition = pool[index];
        if (settings.ReplacementMode == ReplacementMode.WithoutReplacement)
            pool.RemoveAt(index);

        var instance = new CardInstance(CardInstanceId.New(), definition.Id, definition.AssetHash);
        instances.Add(instance.Id, instance);
        cards.Add(instance.Id);
        counts[rarity]++;
    }
}
