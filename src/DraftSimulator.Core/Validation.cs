namespace DraftSimulator.Core;

public enum ValidationCode
{
    PlayerCount,
    PackSize,
    CardsPerPick,
    PacksPerPlayer,
    MaxPackSize,
    MaxDraftCardInstances,
    MissingRarity,
    RarityRange,
    RarityWeight,
    MinimumsExceedPack,
    MaximumsCannotFillPack,
    WeightedRaritiesCannotFillPack,
    RarityUnavailable,
    RarityCapacity,
    ArithmeticOverflow,
}

public sealed record ValidationIssue(ValidationCode Code, string Message, Rarity? Rarity = null);

public sealed class ValidationResult
{
    internal ValidationResult(IEnumerable<ValidationIssue> issues) => Issues = [.. issues];
    public IReadOnlyList<ValidationIssue> Issues { get; }
    public bool IsValid => Issues.Count == 0;
}

public static class DraftSettingsValidator
{
    public static ValidationResult Validate(
        DraftSettings settings,
        int playerCount,
        IEnumerable<CardDefinition> definitions,
        DraftLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(definitions);
        limits ??= new DraftLimits();
        var issues = new List<ValidationIssue>();
        var cardsByRarity = definitions.GroupBy(x => x.Rarity).ToDictionary(x => x.Key, x => x.Count());

        if (playerCount is < 1 or > 8)
            issues.Add(new(ValidationCode.PlayerCount, "Player count must be between one and eight."));
        if (settings.PackSize <= 0)
            issues.Add(new(ValidationCode.PackSize, "Pack size must be positive."));
        if (settings.CardsPerPick <= 0)
            issues.Add(new(ValidationCode.CardsPerPick, "Cards per pick must be positive."));
        if (settings.PacksPerPlayer <= 0)
            issues.Add(new(ValidationCode.PacksPerPlayer, "Packs per player must be positive."));
        if (settings.PackSize > limits.MaxPackSize)
            issues.Add(new(ValidationCode.MaxPackSize, "Pack size exceeds the configured limit."));

        long totalPackCount = 0;
        try
        {
            totalPackCount = checked((long)playerCount * settings.PacksPerPlayer);
            var instanceCount = checked(totalPackCount * settings.PackSize);
            if (instanceCount > limits.MaxDraftCardInstances)
                issues.Add(new(ValidationCode.MaxDraftCardInstances, "The draft exceeds the card instance limit."));
        }
        catch (OverflowException)
        {
            issues.Add(new(ValidationCode.ArithmeticOverflow, "Draft size arithmetic overflowed."));
        }

        long minimumTotal = 0;
        long effectiveMaximumTotal = 0;
        long weightedCapacity = 0;
        foreach (var rarity in Enum.GetValues<Rarity>())
        {
            if (!settings.Rarities.TryGetValue(rarity, out var raritySettings))
            {
                issues.Add(new(ValidationCode.MissingRarity, $"Settings for {rarity} are missing.", rarity));
                continue;
            }

            if (raritySettings.Minimum < 0 || raritySettings.Maximum < 0 || raritySettings.Minimum > raritySettings.Maximum)
                issues.Add(new(ValidationCode.RarityRange, $"The minimum and maximum for {rarity} are invalid.", rarity));
            if (!double.IsFinite(raritySettings.Weight) || raritySettings.Weight < 0)
                issues.Add(new(ValidationCode.RarityWeight, $"The weight for {rarity} must be finite and nonnegative.", rarity));

            if (raritySettings.Minimum < 0 || raritySettings.Maximum < 0 || !double.IsFinite(raritySettings.Weight) || raritySettings.Weight < 0)
                continue;

            var effectiveMaximum = raritySettings.EffectiveMaximum;
            minimumTotal += raritySettings.Minimum;
            effectiveMaximumTotal += effectiveMaximum;
            if (raritySettings.Weight > 0)
                weightedCapacity += effectiveMaximum - raritySettings.Minimum;

            var available = cardsByRarity.GetValueOrDefault(rarity);
            if (effectiveMaximum > 0 && available == 0)
                issues.Add(new(ValidationCode.RarityUnavailable, $"No cards are available for required rarity {rarity}.", rarity));

            if (settings.ReplacementMode == ReplacementMode.WithoutReplacement && totalPackCount > 0)
            {
                try
                {
                    var required = checked((long)effectiveMaximum * totalPackCount);
                    if (available < required)
                        issues.Add(new(ValidationCode.RarityCapacity, $"Rarity {rarity} requires {required} unique definitions but only {available} are available.", rarity));
                }
                catch (OverflowException)
                {
                    issues.Add(new(ValidationCode.ArithmeticOverflow, $"Capacity arithmetic overflowed for {rarity}.", rarity));
                }
            }
        }

        if (minimumTotal > settings.PackSize)
            issues.Add(new(ValidationCode.MinimumsExceedPack, "Rarity minimums exceed the pack size."));
        if (effectiveMaximumTotal < settings.PackSize)
            issues.Add(new(ValidationCode.MaximumsCannotFillPack, "Effective rarity maximums cannot fill a pack."));

        var remaining = (long)settings.PackSize - minimumTotal;
        if (remaining > 0 && weightedCapacity < remaining)
            issues.Add(new(ValidationCode.WeightedRaritiesCannotFillPack, "Positive-weight rarities cannot fill all remaining pack slots."));

        return new ValidationResult(issues);
    }
}

public static class PlayerNameValidator
{
    public static bool TryNormalize(string? name, IEnumerable<string> existingNames, out string normalized)
    {
        normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 16 || normalized.Any(c => c is < ' ' or > '~'))
            return false;

        var candidate = normalized;
        return !existingNames.Any(existing => StringComparer.OrdinalIgnoreCase.Equals(existing, candidate));
    }
}
