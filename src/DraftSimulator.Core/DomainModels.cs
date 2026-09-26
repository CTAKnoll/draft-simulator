namespace DraftSimulator.Core;

public enum Rarity
{
    Common,
    Uncommon,
    Rare,
    SuperRare,
    UltraRare,
    MythicRare,
    Special,
    Bonus,
}

public enum ReplacementMode
{
    WithReplacement,
    WithoutReplacement,
}

public enum DirectionRule
{
    AlwaysClockwise,
    AlwaysCounterclockwise,
    StartClockwise,
    StartCounterclockwise,
}

public enum DraftPhase
{
    Drafting,
    Complete,
    Closed,
}

public sealed record CardMetadata(string? Name = null, IReadOnlyDictionary<string, string>? Values = null);

public sealed record CardScanInfo(long SourceByteLength, int PixelWidth, int PixelHeight);

public sealed record CardDefinition(
    CardDefinitionId Id,
    string SourcePath,
    string FallbackName,
    Rarity Rarity,
    AssetHash? AssetHash = null,
    CardMetadata? Metadata = null,
    CardScanInfo? ScanInfo = null)
{
    public string ExportName => string.IsNullOrWhiteSpace(Metadata?.Name) ? FallbackName : Metadata.Name.Trim();
}

public sealed record CardInstance(CardInstanceId Id, CardDefinitionId DefinitionId, AssetHash? AssetHash);

public sealed class Pack
{
    private readonly List<CardInstanceId> _cards;

    public Pack(PackId id, IEnumerable<CardInstanceId> cards, PlayerId currentHolder)
    {
        ArgumentNullException.ThrowIfNull(cards);
        Id = id;
        _cards = [.. cards];
        if (_cards.Count != _cards.Distinct().Count())
        {
            throw new ArgumentException("A pack cannot contain duplicate card instance IDs.", nameof(cards));
        }

        CurrentHolder = currentHolder;
    }

    public PackId Id { get; }
    public IReadOnlyList<CardInstanceId> Cards => _cards;
    public PlayerId CurrentHolder { get; internal set; }

    internal void Remove(IEnumerable<CardInstanceId> cards)
    {
        foreach (var card in cards)
        {
            if (!_cards.Remove(card))
            {
                throw new InvalidOperationException("The selected card is not in the pack.");
            }
        }
    }
}

public sealed class PlayerState
{
    private readonly List<CardInstanceId> _collection = [];
    private readonly HashSet<CardInstanceId> _selection = [];

    internal PlayerState(PlayerId id, ulong steamId, string name, int order, bool connected)
    {
        Id = id;
        SteamId = steamId;
        Name = name;
        Order = order;
        IsConnected = connected;
    }

    public PlayerId Id { get; }
    public ulong SteamId { get; }
    public string Name { get; internal set; }
    public int Order { get; internal set; }
    public bool IsConnected { get; internal set; }
    public bool IsLobbyReady { get; internal set; }
    public bool IsDraftLocked { get; internal set; }
    public PackId? CurrentPackId { get; internal set; }
    public IReadOnlyCollection<CardInstanceId> Selection => _selection;
    public IReadOnlyList<CardInstanceId> Collection => _collection;

    internal HashSet<CardInstanceId> MutableSelection => _selection;
    internal List<CardInstanceId> MutableCollection => _collection;
}

public sealed record DraftPlayer(PlayerId Id, ulong SteamId, string Name, bool IsConnected = true);

public sealed record RaritySettings(int Minimum, int Maximum, double Weight)
{
    public int EffectiveMaximum => Weight == 0 || Minimum == Maximum ? Minimum : Maximum;
}

public sealed class DraftSettings
{
    private readonly IReadOnlyDictionary<Rarity, RaritySettings> _rarities;

    public DraftSettings(
        ReplacementMode replacementMode,
        IReadOnlyDictionary<Rarity, RaritySettings> rarities,
        int packSize,
        int cardsPerPick,
        int packsPerPlayer,
        DirectionRule directionRule)
    {
        ArgumentNullException.ThrowIfNull(rarities);
        ReplacementMode = replacementMode;
        _rarities = new Dictionary<Rarity, RaritySettings>(rarities);
        PackSize = packSize;
        CardsPerPick = cardsPerPick;
        PacksPerPlayer = packsPerPlayer;
        DirectionRule = directionRule;
    }

    public ReplacementMode ReplacementMode { get; }
    public IReadOnlyDictionary<Rarity, RaritySettings> Rarities => _rarities;
    public int PackSize { get; }
    public int CardsPerPick { get; }
    public int PacksPerPlayer { get; }
    public DirectionRule DirectionRule { get; }

    public int DirectionForRound(int zeroBasedRound) => DirectionRule switch
    {
        DirectionRule.AlwaysClockwise => 1,
        DirectionRule.AlwaysCounterclockwise => -1,
        DirectionRule.StartClockwise => zeroBasedRound % 2 == 0 ? 1 : -1,
        DirectionRule.StartCounterclockwise => zeroBasedRound % 2 == 0 ? -1 : 1,
        _ => throw new ArgumentOutOfRangeException(nameof(DirectionRule)),
    };
}

public sealed record DraftLimits(int MaxPackSize = 1000, long MaxDraftCardInstances = 10_000);

public interface ICardMetadataProvider
{
    CardMetadata? GetMetadata(CardDefinition definition);
}

public static class ExportNameSanitizer
{
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new System.Text.StringBuilder();
        var count = 0;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (count++ == 256)
                break;
            result.Append(rune.Value is '\r' or '\n' or '\t' or '\0' ? " " : rune.ToString());
        }

        return result.ToString();
    }
}
