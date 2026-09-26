using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace DraftSimulator.Infrastructure;

public sealed record HostConfiguration(
    int MaxCardCount,
    long MaxSourceImageBytes,
    long MaxDecodedPixels,
    long MaxSessionAssetBytes,
    long MaxMemoryCacheBytes,
    int MaxPackSize,
    long MaxDraftCardInstances,
    int OutputMaxLongEdge,
    int OutputWebPQuality,
    int TransferChunkBytes,
    int TransferStallSeconds,
    int ControlMessageMaxBytes)
{
    public string ScryfallDirectory { get; init; } = string.Empty;

    public static HostConfiguration Defaults { get; } = new(
        1000, 2_097_152, 25_000_000, 2_147_483_648, 268_435_456, 1000, 10_000,
        1200, 80, 65_536, 60, 262_144);
}

public sealed class HostConfigurationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class HostConfigurationStore
{
    private const string DefaultContents = """
        [Assets]
        MaxCardCount=1000
        MaxSourceImageBytes=2097152
        MaxDecodedPixels=25000000
        MaxSessionAssetBytes=2147483648
        MaxMemoryCacheBytes=268435456
        MaxPackSize=1000
        MaxDraftCardInstances=10000
        OutputMaxLongEdge=1200
        OutputWebPQuality=80
        TransferChunkBytes=65536
        TransferStallSeconds=60

        [Protocol]
        ControlMessageMaxBytes=262144
        """;

    private readonly string _path;
    private readonly string _defaultScryfallDirectory;

    public HostConfigurationStore(string path, string? defaultScryfallDirectory = null)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _defaultScryfallDirectory = Path.GetFullPath(defaultScryfallDirectory ??
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "Scryfall"));
    }

    public HostConfiguration CreateDefaultIfMissingAndLoad()
    {
        if (!File.Exists(_path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            try
            {
                using var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.Write(DefaultContents);
                writer.WriteLine();
                writer.WriteLine("[Scryfall]");
                writer.WriteLine($"Directory={_defaultScryfallDirectory}");
            }
            catch (IOException) when (File.Exists(_path))
            {
                // Another process created the configuration first; load that file.
            }
        }

        return Load();
    }

    public HostConfiguration Load()
    {
        try
        {
            var configuration = new ConfigurationBuilder().AddIniFile(_path, optional: false, reloadOnChange: false).Build();
            var result = new HostConfiguration(
                ReadInt(configuration, "Assets:MaxCardCount"),
                ReadLong(configuration, "Assets:MaxSourceImageBytes"),
                ReadLong(configuration, "Assets:MaxDecodedPixels"),
                ReadLong(configuration, "Assets:MaxSessionAssetBytes"),
                ReadLong(configuration, "Assets:MaxMemoryCacheBytes"),
                ReadInt(configuration, "Assets:MaxPackSize"),
                ReadLong(configuration, "Assets:MaxDraftCardInstances"),
                ReadInt(configuration, "Assets:OutputMaxLongEdge"),
                ReadInt(configuration, "Assets:OutputWebPQuality"),
                ReadInt(configuration, "Assets:TransferChunkBytes"),
                ReadInt(configuration, "Assets:TransferStallSeconds"),
                ReadInt(configuration, "Protocol:ControlMessageMaxBytes"))
            {
                ScryfallDirectory = ReadScryfallDirectory(configuration),
            };

            Validate(result);
            return result;
        }
        catch (HostConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new HostConfigurationException("The host configuration could not be loaded.", exception);
        }
    }

    private string ReadScryfallDirectory(IConfiguration configuration)
    {
        var configured = configuration["Scryfall:Directory"];
        var expanded = string.IsNullOrWhiteSpace(configured)
            ? _defaultScryfallDirectory
            : Environment.ExpandEnvironmentVariables(configured.Trim());
        try
        {
            return Path.GetFullPath(expanded);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new HostConfigurationException("The Scryfall directory path is invalid.", exception);
        }
    }

    private static int ReadInt(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new HostConfigurationException($"Host configuration value '{key}' is missing or invalid.");
        return result;
    }

    private static long ReadLong(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
            throw new HostConfigurationException($"Host configuration value '{key}' is missing or invalid.");
        return result;
    }

    private static void Validate(HostConfiguration value)
    {
        if (value.MaxCardCount <= 0 || value.MaxSourceImageBytes <= 0 || value.MaxDecodedPixels <= 0 ||
            value.MaxSessionAssetBytes <= 0 || value.MaxMemoryCacheBytes <= 0 || value.MaxPackSize <= 0 ||
            value.MaxDraftCardInstances <= 0 || value.OutputMaxLongEdge <= 0 || value.TransferChunkBytes <= 0 ||
            value.TransferStallSeconds <= 0 || value.ControlMessageMaxBytes <= 0 ||
            value.OutputWebPQuality is < 1 or > 100 || value.MaxMemoryCacheBytes > value.MaxSessionAssetBytes ||
            value.MaxPackSize > value.MaxDraftCardInstances)
            throw new HostConfigurationException("The host configuration contains a negative, zero, or nonsensical value.");
    }
}
