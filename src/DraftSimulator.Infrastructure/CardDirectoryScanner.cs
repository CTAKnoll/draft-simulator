using DraftSimulator.Core;
using SkiaSharp;

namespace DraftSimulator.Infrastructure;

public enum CardScanErrorCode { DirectoryUnavailable, TooManyCards, DuplicateFallbackName }
public enum CardScanWarningCode { DataCsvUnsupported, EmptyName, NameTooLong, FileUnreadable, SourceTooLarge, InvalidImage, PixelLimitExceeded }

public sealed record CardScanError(CardScanErrorCode Code, string Message);
public sealed record CardScanWarning(CardScanWarningCode Code, string Message);
public sealed record CardScanResult(
    IReadOnlyList<CardDefinition> Definitions,
    IReadOnlyList<CardScanWarning> Warnings,
    IReadOnlyList<CardScanError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed class CardDirectoryScanner
{
    public const int MaxFallbackNameScalars = 256;

    private static readonly IReadOnlyDictionary<string, Rarity> RarityFolders =
        new Dictionary<string, Rarity>(StringComparer.OrdinalIgnoreCase)
        {
            ["Common"] = Rarity.Common,
            ["Uncommon"] = Rarity.Uncommon,
            ["Rare"] = Rarity.Rare,
            ["Super Rare"] = Rarity.SuperRare,
            ["Ultra Rare"] = Rarity.UltraRare,
            ["Mythic Rare"] = Rarity.MythicRare,
            ["Special"] = Rarity.Special,
            ["Bonus"] = Rarity.Bonus,
        };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp" };

    public CardScanResult Scan(string rootDirectory, HostConfiguration limits)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(limits);
        var warnings = new List<CardScanWarning>();
        var errors = new List<CardScanError>();
        var candidates = new List<(string Path, string Name, Rarity Rarity)>();

        try
        {
            if (!Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException();

            if (File.Exists(Path.Combine(rootDirectory, "data.csv")))
                warnings.Add(new(CardScanWarningCode.DataCsvUnsupported, "data.csv enrichment is not supported; fallback metadata will be used."));

            foreach (var directory in Directory.EnumerateDirectories(rootDirectory))
            {
                if (!RarityFolders.TryGetValue(Path.GetFileName(directory), out var rarity))
                    continue;
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    if (Extensions.Contains(Path.GetExtension(path)))
                        candidates.Add((path, Path.GetFileNameWithoutExtension(path), rarity));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            errors.Add(new(CardScanErrorCode.DirectoryUnavailable, "The selected card directory could not be scanned."));
            return new([], warnings, errors);
        }

        if (candidates.Count > limits.MaxCardCount)
        {
            errors.Add(new(CardScanErrorCode.TooManyCards, "The number of candidate card files exceeds MaxCardCount."));
            return new([], warnings, errors);
        }

        foreach (var duplicate in candidates.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            errors.Add(new(CardScanErrorCode.DuplicateFallbackName, $"Fallback card name '{duplicate.Key}' is duplicated."));
        if (errors.Count != 0)
            return new([], warnings, errors);

        var definitions = new List<CardDefinition>();
        foreach (var candidate in candidates.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(candidate.Name))
            {
                warnings.Add(new(CardScanWarningCode.EmptyName, "A card file has an empty fallback name and was skipped."));
                continue;
            }
            if (candidate.Name.EnumerateRunes().Count() > MaxFallbackNameScalars)
            {
                warnings.Add(new(CardScanWarningCode.NameTooLong, "A card fallback name exceeds 256 Unicode scalar values and was skipped."));
                continue;
            }

            try
            {
                using var stream = new FileStream(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = stream.Length;
                if (length > limits.MaxSourceImageBytes)
                {
                    warnings.Add(new(CardScanWarningCode.SourceTooLarge, "A source image exceeds MaxSourceImageBytes and was skipped."));
                    continue;
                }

                using var codec = SKCodec.Create(stream);
                if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                {
                    warnings.Add(new(CardScanWarningCode.InvalidImage, "An image has an invalid or unsupported header and was skipped."));
                    continue;
                }

                long pixels;
                try { pixels = checked((long)codec.Info.Width * codec.Info.Height); }
                catch (OverflowException) { pixels = long.MaxValue; }
                if (pixels > limits.MaxDecodedPixels)
                {
                    warnings.Add(new(CardScanWarningCode.PixelLimitExceeded, "An image exceeds MaxDecodedPixels and was skipped."));
                    continue;
                }

                using var bitmap = new SKBitmap(new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                var decodeResult = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
                if (decodeResult != SKCodecResult.Success)
                {
                    warnings.Add(new(CardScanWarningCode.InvalidImage, "An image could not be fully decoded and was skipped."));
                    continue;
                }

                definitions.Add(new(CardDefinitionId.New(), candidate.Path, candidate.Name, candidate.Rarity,
                    ScanInfo: new CardScanInfo(length, codec.Info.Width, codec.Info.Height)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                warnings.Add(new(CardScanWarningCode.FileUnreadable, "A source image could not be read and was skipped."));
            }
        }

        return new(definitions, warnings, errors);
    }
}
