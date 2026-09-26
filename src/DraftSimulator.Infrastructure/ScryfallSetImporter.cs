using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DraftSimulator.Core;

namespace DraftSimulator.Infrastructure;

public sealed record ScryfallImportProgress(string Message, int CompletedCards, int TotalCards);

public sealed record ScryfallImportResult(
    string SetName,
    string SetCode,
    string Directory,
    int ImportedCards,
    int SkippedCards,
    bool ReusedExistingDirectory);

public interface IScryfallSetImporter
{
    Task<ScryfallImportResult> ImportAsync(
        string setNameOrCode,
        string scryfallDirectory,
        bool forceFetch,
        int maxCardCount,
        long maxSourceImageBytes,
        IProgress<ScryfallImportProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public sealed class ScryfallImportException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Imports a Scryfall set into the directory layout consumed by CardDirectoryScanner.</summary>
public sealed class ScryfallSetImporter : IScryfallSetImporter, IDisposable
{
    private static readonly Uri ApiRoot = new("https://api.scryfall.com/");
    private static readonly TimeSpan SetCatalogCacheLifetime = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pageDelay;
    private int _disposed;

    public ScryfallSetImporter(
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pageDelay = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = TimeSpan.FromMinutes(5) };
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DraftSimulator/1.0");
        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _pageDelay = pageDelay ?? TimeSpan.FromSeconds(1);
        if (_pageDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pageDelay));
    }

    public async Task<ScryfallImportResult> ImportAsync(
        string setNameOrCode,
        string scryfallDirectory,
        bool forceFetch,
        int maxCardCount,
        long maxSourceImageBytes,
        IProgress<ScryfallImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(setNameOrCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(scryfallDirectory);
        if (maxCardCount <= 0 || maxSourceImageBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCardCount), "Scryfall import limits must be positive.");

        var root = Path.GetFullPath(scryfallDirectory);
        Directory.CreateDirectory(root);
        if (!forceFetch && FindExistingSetDirectory(root, setNameOrCode) is { } namedDirectory)
        {
            var existingMarker = TryReadMarker(namedDirectory);
            progress?.Report(new("Using the existing Scryfall set folder.", 0, 0));
            return new(existingMarker?.SetName ?? Path.GetFileName(namedDirectory), existingMarker?.SetCode ?? string.Empty,
                namedDirectory, 0, 0, true);
        }
        progress?.Report(new("Resolving set...", 0, 0));
        var sets = await LoadSetCatalogAsync(root, cancellationToken).ConfigureAwait(false);
        var set = ResolveSet(sets, setNameOrCode);
        var destination = ResolveSetDirectory(root, set);

        if (Directory.Exists(destination) && !forceFetch)
        {
            progress?.Report(new("Using the existing Scryfall set folder.", 0, 0));
            return new(set.Name, set.Code, destination, 0, 0, true);
        }
        if (File.Exists(destination))
            throw new ScryfallImportException("A file already occupies the destination for this set.");
        if (Directory.Exists(destination) && forceFetch && !HasMatchingImportMarker(destination, set.Code))
            throw new ScryfallImportException("Force fetch will not replace a folder that was not created by a Scryfall import.");

        progress?.Report(new("Fetching card list...", 0, 0));
        var cards = await FetchCardsAsync(set, maxCardCount, progress, cancellationToken).ConfigureAwait(false);
        if (cards.Count == 0)
            throw new ScryfallImportException("Scryfall returned no cards for this set.");

        var staging = Path.Combine(root, $".{Path.GetFileName(destination)}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var imported = await DownloadImagesAsync(cards, staging, maxSourceImageBytes, progress, cancellationToken)
                .ConfigureAwait(false);
            if (imported.Imported == 0)
                throw new ScryfallImportException("No card images could be imported; the previous set folder was preserved.");

            var marker = new ScryfallImportMarker(1, set.Code, set.Name, DateTimeOffset.UtcNow,
                imported.Imported, imported.Skipped);
            await WriteMarkerAsync(staging, marker, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceDirectory(staging, destination, set.Code);
            progress?.Report(new("Scryfall set imported.", cards.Count, cards.Count));
            return new(set.Name, set.Code, destination, imported.Imported, imported.Skipped, false);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    private async Task<IReadOnlyList<ScryfallSetDto>> LoadSetCatalogAsync(string root, CancellationToken cancellationToken)
    {
        var cachePath = Path.Combine(root, ".scryfall-sets-cache.json");
        ScryfallSetCache? staleCache = null;
        try
        {
            if (File.Exists(cachePath))
            {
                await using var cacheStream = new FileStream(cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16_384, FileOptions.Asynchronous | FileOptions.SequentialScan);
                staleCache = await JsonSerializer.DeserializeAsync<ScryfallSetCache>(cacheStream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (staleCache is { Version: 1, Sets.Count: > 0 } && DateTimeOffset.UtcNow - staleCache.FetchedAt < SetCatalogCacheLifetime)
                    return staleCache.Sets;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            staleCache = null;
        }

        try
        {
            using var response = await GetApiResponseAsync(new Uri(ApiRoot, "sets"), cancellationToken).ConfigureAwait(false);
            var result = await ReadJsonAsync<ScryfallList<ScryfallSetDto>>(response.Content, "Scryfall returned an invalid set catalog.", cancellationToken)
                .ConfigureAwait(false);
            var validSets = (result.Data ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Code)).ToArray();
            if (validSets.Length == 0)
                throw new ScryfallImportException("Scryfall returned no usable set names.");
            await SaveCatalogAsync(cachePath, new(1, DateTimeOffset.UtcNow, validSets), cancellationToken).ConfigureAwait(false);
            return validSets;
        }
        catch (Exception exception) when (staleCache is { Version: 1, Sets.Count: > 0 } && exception is HttpRequestException or TaskCanceledException or ScryfallImportException)
        {
            return staleCache.Sets;
        }
        catch (HttpRequestException exception)
        {
            throw new ScryfallImportException("Could not connect to the Scryfall API.", exception);
        }
    }

    private async Task<IReadOnlyList<ScryfallCardDto>> FetchCardsAsync(
        ScryfallSetDto set,
        int maxCardCount,
        IProgress<ScryfallImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"set:{set.Code}");
        Uri? next = new(ApiRoot, $"cards/search?q={query}&unique=prints&order=set");
        var cards = new List<ScryfallCardDto>(Math.Min(maxCardCount, Math.Max(0, set.CardCount)));
        var ids = new HashSet<Guid>();
        var pageIndex = 0;
        while (next is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new($"Fetching card list (page {pageIndex + 1})...", cards.Count,
                set.CardCount > 0 ? Math.Min(set.CardCount, maxCardCount) : maxCardCount));
            if (pageIndex > 0)
                await _delay(_pageDelay, cancellationToken).ConfigureAwait(false);
            ValidateApiUri(next);
            using var response = await GetApiResponseAsync(next, cancellationToken).ConfigureAwait(false);
            var page = await ReadJsonAsync<ScryfallList<ScryfallCardDto>>(response.Content, "Scryfall returned an invalid card page.", cancellationToken)
                .ConfigureAwait(false);
            if (page.TotalCards is { } total && total > maxCardCount)
                throw new ScryfallImportException($"This set contains {total} prints, exceeding the configured limit of {maxCardCount} cards.");
            foreach (var card in page.Data ?? [])
            {
                if (card.Id == Guid.Empty || !ids.Add(card.Id) || string.IsNullOrWhiteSpace(card.Name) || string.IsNullOrWhiteSpace(card.Rarity))
                    throw new ScryfallImportException("Scryfall returned a missing or duplicate card identifier.");
                cards.Add(card);
                if (cards.Count > maxCardCount)
                    throw new ScryfallImportException($"This set exceeds the configured limit of {maxCardCount} cards.");
            }
            if (page.HasMore && page.NextPage is null)
                throw new ScryfallImportException("Scryfall indicated another page without providing its address.");
            next = page.HasMore ? page.NextPage : null;
            pageIndex++;
        }
        return cards;
    }

    private async Task<(int Imported, int Skipped)> DownloadImagesAsync(
        IReadOnlyList<ScryfallCardDto> cards,
        string staging,
        long maxSourceImageBytes,
        IProgress<ScryfallImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var directories = new ConcurrentDictionary<Rarity, string>();
        var fileNames = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        using var concurrency = new SemaphoreSlim(4, 4);
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completed = 0;
        var imported = 0;
        var skipped = 0;
        var tasks = cards.Select(async card =>
        {
            await concurrency.WaitAsync(downloadCancellation.Token).ConfigureAwait(false);
            try
            {
                var progressIndex = Interlocked.Increment(ref completed);
                progress?.Report(new($"Downloading card images ({progressIndex}/{cards.Count})...", progressIndex - 1, cards.Count));
                var rarity = ParseRarity(card.Rarity);
                var imageUri = SelectImageUri(card);
                if (imageUri is null)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }
                ValidateImageUri(imageUri);
                var folder = directories.GetOrAdd(rarity, value => Path.Combine(staging, RarityFolder(value)));
                Directory.CreateDirectory(folder);
                var stem = CreateUniqueFileStem(card, fileNames);
                var temporary = Path.Combine(folder, $".{Guid.NewGuid():N}.partial");
                try
                {
                    var extension = await DownloadImageAsync(imageUri, temporary, maxSourceImageBytes, downloadCancellation.Token)
                        .ConfigureAwait(false);
                    var destination = Path.Combine(folder, stem + extension);
                    File.Move(temporary, destination);
                    Interlocked.Increment(ref imported);
                }
                catch (ScryfallImageUnavailableException)
                {
                    Interlocked.Increment(ref skipped);
                }
                finally
                {
                    TryDeleteFile(temporary);
                }
            }
            catch
            {
                downloadCancellation.Cancel();
                throw;
            }
            finally
            {
                concurrency.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            downloadCancellation.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
            throw new ScryfallImportException("Scryfall image download failed; the previous set folder was preserved.", exception);
        }
        catch
        {
            downloadCancellation.Cancel();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { }
            throw;
        }
        return (imported, skipped);
    }

    private async Task<string> DownloadImageAsync(Uri uri, string temporaryPath, long maximumBytes, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new ScryfallImportException("Scryfall image download failed; the previous set folder was preserved.", exception);
        }
        using (response)
        {
        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.Gone)
            throw new ScryfallImageUnavailableException();
        if (!response.IsSuccessStatusCode)
            throw new ScryfallImportException($"Scryfall image download failed (HTTP {(int)response.StatusCode}).");
        if (response.Content.Headers.ContentLength is { } length && length > maximumBytes)
            throw new ScryfallImageUnavailableException();

        var extension = ImageExtension(response.Content.Headers.ContentType?.MediaType, uri);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[65_536];
        long written = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            written = checked(written + count);
            if (written > maximumBytes)
                throw new ScryfallImageUnavailableException();
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (written == 0)
            throw new ScryfallImageUnavailableException();
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return extension;
        }
    }

    private async Task<HttpResponseMessage> GetApiResponseAsync(Uri uri, CancellationToken cancellationToken)
    {
        ValidateApiUri(uri);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.UserAgent.ParseAdd("DraftSimulator/1.0");
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new ScryfallImportException("Could not connect to the Scryfall API.", exception);
            }
            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                if (!response.IsSuccessStatusCode)
                {
                    var code = (int)response.StatusCode;
                    response.Dispose();
                    throw new ScryfallImportException($"Scryfall API request failed (HTTP {code}).");
                }
                return response;
            }

            var retryAfter = response.Headers.RetryAfter;
            var retryDelay = retryAfter?.Delta ??
                (retryAfter?.Date is { } retryDate ? retryDate - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(30));
            if (retryDelay < TimeSpan.Zero) retryDelay = TimeSpan.Zero;
            response.Dispose();
            if (attempt == 2)
                throw new ScryfallImportException("Scryfall rate-limited the import. Wait briefly and try again.");
            await _delay(retryDelay, cancellationToken).ConfigureAwait(false);
        }
        throw new ScryfallImportException("Scryfall API request failed.");
    }

    private async Task SaveCatalogAsync(string path, ScryfallSetCache cache, CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             65_536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, cache, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpContent content, string errorMessage, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new ScryfallImportException(errorMessage);
        }
        catch (JsonException exception)
        {
            throw new ScryfallImportException(errorMessage, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new ScryfallImportException(errorMessage, exception);
        }
    }

    private static async Task WriteMarkerAsync(string staging, ScryfallImportMarker marker, CancellationToken cancellationToken)
    {
        var path = Path.Combine(staging, ".scryfall-import.json");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16_384, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, marker, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ScryfallSetDto ResolveSet(IReadOnlyList<ScryfallSetDto> sets, string input)
    {
        var normalized = input.Trim();
        var matches = sets.Where(set => string.Equals(set.Code, normalized, StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(set.Name, normalized, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1)
            throw new ScryfallImportException("That set name matches multiple Scryfall sets. Enter the set code instead.");
        throw new ScryfallImportException("No Scryfall set matched that exact name or set code.");
    }

    private static string ResolveSetDirectory(string root, ScryfallSetDto set)
    {
        var safeName = SanitizePathComponent(set.Name);
        var path = Path.Combine(root, safeName);
        if (Directory.Exists(path) && TryReadMarkerCode(path) is { } existingCode &&
            !string.Equals(existingCode, set.Code, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(root, $"{safeName} ({SanitizePathComponent(set.Code)})");
        return path;
    }

    private static string? FindExistingSetDirectory(string root, string setNameOrCode)
    {
        var safeName = SanitizePathComponent(setNameOrCode);
        var direct = Path.Combine(root, safeName);
        if (Directory.Exists(direct)) return direct;
        try
        {
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), safeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool HasMatchingImportMarker(string directory, string setCode) =>
        string.Equals(TryReadMarkerCode(directory), setCode, StringComparison.OrdinalIgnoreCase);

    private static string? TryReadMarkerCode(string directory)
        => TryReadMarker(directory)?.SetCode;

    private static ScryfallImportMarker? TryReadMarker(string directory)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<ScryfallImportMarker>(File.ReadAllBytes(Path.Combine(directory, ".scryfall-import.json")), JsonOptions);
            return marker is { Version: 1 } ? marker : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void ReplaceDirectory(string staging, string destination, string setCode)
    {
        if (!Directory.Exists(destination))
        {
            Directory.Move(staging, destination);
            return;
        }
        if (!HasMatchingImportMarker(destination, setCode))
            throw new ScryfallImportException("The existing destination is not a matching Scryfall import and was not replaced.");

        var backup = destination + $".backup-{Guid.NewGuid():N}";
        Directory.Move(destination, backup);
        try
        {
            Directory.Move(staging, destination);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw;
        }
        TryDeleteDirectory(backup);
    }

    private static string CreateUniqueFileStem(ScryfallCardDto card, ConcurrentDictionary<string, byte> names)
    {
        var baseName = SanitizePathComponent(card.Name);
        if (baseName.Length > 120)
            baseName = baseName[..120].TrimEnd(' ', '.');
        var collectorNumber = SanitizePathComponent(card.CollectorNumber ?? "unknown");
        if (collectorNumber.Length > 36)
            collectorNumber = collectorNumber[..36];
        var stem = $"{baseName} [{collectorNumber}]";
        if (names.TryAdd(stem, 0))
            return stem;
        stem = $"{stem} [{card.Id.ToString("N")[..8]}]";
        if (names.TryAdd(stem, 0))
            return stem;
        throw new ScryfallImportException("Scryfall returned colliding card file names.");
    }

    private static string SanitizePathComponent(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        foreach (var character in "<>:\"/\\|?*") invalid.Add(character);
        var chars = value.Trim().Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray();
        var result = new string(chars).Trim().TrimEnd('.', ' ');
        if (result.Length == 0 || result is "." or "..") result = "Unnamed";
        var stem = result.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             stem[3] is >= '1' and <= '9'))
            result = "_" + result;
        return result;
    }

    private static Rarity ParseRarity(string? rarity) => rarity?.ToLowerInvariant() switch
    {
        "common" => Rarity.Common,
        "uncommon" => Rarity.Uncommon,
        "rare" => Rarity.Rare,
        "mythic" => Rarity.MythicRare,
        "special" => Rarity.Special,
        "bonus" => Rarity.Bonus,
        _ => throw new ScryfallImportException("Scryfall returned a card with an unsupported rarity."),
    };

    private static string RarityFolder(Rarity rarity) => rarity switch
    {
        Rarity.Common => "Common",
        Rarity.Uncommon => "Uncommon",
        Rarity.Rare => "Rare",
        Rarity.MythicRare => "Mythic Rare",
        Rarity.SuperRare => "Super Rare",
        Rarity.UltraRare => "Ultra Rare",
        Rarity.Special => "Special",
        Rarity.Bonus => "Bonus",
        _ => throw new ArgumentOutOfRangeException(nameof(rarity)),
    };

    private static Uri? SelectImageUri(ScryfallCardDto card)
    {
        var image = card.ImageUris ?? card.CardFaces?.Select(face => face.ImageUris).FirstOrDefault(value => value is not null);
        var uri = image?.Png ?? image?.Large ?? image?.Normal;
        return Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ? parsed : null;
    }

    private static string ImageExtension(string? contentType, Uri uri)
    {
        if (contentType?.Equals("image/png", StringComparison.OrdinalIgnoreCase) == true) return ".png";
        if (contentType?.Equals("image/webp", StringComparison.OrdinalIgnoreCase) == true) return ".webp";
        if (contentType?.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) == true) return ".jpg";
        return Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
        {
            ".png" => ".png",
            ".webp" => ".webp",
            ".jpeg" => ".jpeg",
            _ => ".jpg",
        };
    }

    private static void ValidateApiUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("api.scryfall.com", StringComparison.OrdinalIgnoreCase))
            throw new ScryfallImportException("Scryfall returned an unexpected API page address.");
    }

    private static void ValidateImageUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.Equals("scryfall.io", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".scryfall.io", StringComparison.OrdinalIgnoreCase)))
            throw new ScryfallImportException("Scryfall returned an unexpected image address.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && _ownsHttpClient)
            _httpClient.Dispose();
    }

    private sealed class ScryfallImageUnavailableException : Exception { }

    private sealed record ScryfallSetCache(int Version, DateTimeOffset FetchedAt, IReadOnlyList<ScryfallSetDto> Sets);
    private sealed record ScryfallImportMarker(int Version, string SetCode, string SetName, DateTimeOffset ImportedAt, int ImportedCards, int SkippedCards);

    private sealed record ScryfallList<T>(
        [property: JsonPropertyName("data")] T[]? Data,
        [property: JsonPropertyName("has_more")] bool HasMore = false,
        [property: JsonPropertyName("next_page")] Uri? NextPage = null,
        [property: JsonPropertyName("total_cards")] int? TotalCards = null);

    private sealed record ScryfallSetDto(
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("card_count")] int CardCount = 0);

    private sealed record ScryfallCardDto(
        [property: JsonPropertyName("id")] Guid Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("collector_number")] string? CollectorNumber,
        [property: JsonPropertyName("rarity")] string? Rarity,
        [property: JsonPropertyName("image_uris")] ScryfallImageUris? ImageUris,
        [property: JsonPropertyName("card_faces")] ScryfallFaceDto[]? CardFaces);

    private sealed record ScryfallFaceDto([property: JsonPropertyName("image_uris")] ScryfallImageUris? ImageUris);
    private sealed record ScryfallImageUris(
        [property: JsonPropertyName("png")] string? Png,
        [property: JsonPropertyName("large")] string? Large,
        [property: JsonPropertyName("normal")] string? Normal);
}
