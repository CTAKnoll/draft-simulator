using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DraftSimulator.Core;
using DraftSimulator.Infrastructure;
using SkiaSharp;

namespace DraftSimulator.Infrastructure.Tests;

public sealed class ScryfallSetImporterTests
{
    [Fact]
    public async Task ImportsAllScryfallRaritiesAndReusesExistingSetWithoutFetchingAgain()
    {
        using var temporary = new TemporaryDirectory();
        var cards = MakeCards(("common", "Common Card", "1"), ("uncommon", "Uncommon Card", "2"),
            ("rare", "Rare Card", "3"), ("mythic", "Mythic Card", "4"),
            ("special", "Special Card", "5"), ("bonus", "Bonus Card", "6"));
        var handler = CreateHandler(cards);
        using var client = new HttpClient(handler);
        using var importer = new ScryfallSetImporter(client, static (_, _) => Task.CompletedTask, TimeSpan.Zero);

        var result = await importer.ImportAsync("zendikar", temporary.Path, false, 100, 2_097_152);

        Assert.Equal("Zendikar", result.SetName);
        Assert.Equal("zen", result.SetCode);
        Assert.Equal(6, result.ImportedCards);
        Assert.Equal(0, result.SkippedCards);
        Assert.False(result.ReusedExistingDirectory);
        Assert.True(File.Exists(Path.Combine(result.Directory, "Special", "Special Card [5].png")));
        Assert.True(File.Exists(Path.Combine(result.Directory, "Bonus", "Bonus Card [6].png")));
        var scanned = new CardDirectoryScanner().Scan(result.Directory, HostConfiguration.Defaults);
        Assert.True(scanned.IsValid);
        Assert.Equal(6, scanned.Definitions.Count);
        Assert.Contains(scanned.Definitions, x => x.Rarity == Rarity.Special);
        Assert.Contains(scanned.Definitions, x => x.Rarity == Rarity.Bonus);
        Assert.All(handler.Requests, request => Assert.Equal("DraftSimulator/1.0", request.Headers.UserAgent.ToString()));

        var requestsBeforeReuse = handler.Requests.Count;
        var reused = await importer.ImportAsync("zen", temporary.Path, false, 100, 2_097_152);

        Assert.True(reused.ReusedExistingDirectory);
        Assert.Equal(result.Directory, reused.Directory);
        Assert.Equal(requestsBeforeReuse, handler.Requests.Count);

        var forced = await importer.ImportAsync("zen", temporary.Path, true, 100, 2_097_152);
        Assert.False(forced.ReusedExistingDirectory);
        Assert.Equal(result.Directory, forced.Directory);
        Assert.Equal(6, forced.ImportedCards);
        Assert.True(handler.Requests.Count > requestsBeforeReuse);
    }

    [Fact]
    public async Task FollowsNextPageAndWaitsOneSecondBetweenCardPages()
    {
        using var temporary = new TemporaryDirectory();
        var cardPages = MakeCards(("common", "First", "1"), ("bonus", "Second", "2"));
        var handler = CreateHandler(cardPages, pageSize: 1);
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(handler);
        using var importer = new ScryfallSetImporter(client, (duration, _) => { delays.Add(duration); return Task.CompletedTask; });

        var result = await importer.ImportAsync("Zendikar", temporary.Path, false, 100, 2_097_152);

        Assert.Equal(2, result.ImportedCards);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
        Assert.True(File.Exists(Path.Combine(result.Directory, "Bonus", "Second [2].png")));
    }

    [Fact]
    public async Task ForceFetchFailurePreservesPreviousDirectoryAndCleansStaging()
    {
        using var temporary = new TemporaryDirectory();
        var cards = MakeCards(("rare", "Card", "1"));
        var failImages = false;
        var handler = CreateHandler(cards, imageFailure: () => failImages);
        using var client = new HttpClient(handler);
        using var importer = new ScryfallSetImporter(client, static (_, _) => Task.CompletedTask, TimeSpan.Zero);
        var imported = await importer.ImportAsync("Zendikar", temporary.Path, false, 100, 2_097_152);
        var originalFile = Path.Combine(imported.Directory, "Rare", "Card [1].png");
        var originalBytes = await File.ReadAllBytesAsync(originalFile);
        failImages = true;

        await Assert.ThrowsAsync<ScryfallImportException>(() => importer.ImportAsync("Zendikar", temporary.Path, true, 100, 2_097_152));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(originalFile));
        Assert.True(File.Exists(Path.Combine(imported.Directory, ".scryfall-import.json")));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Path).Where(path => Path.GetFileName(path).Contains("staging-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RejectsSetAboveConfiguredCardLimitBeforeDownloadingImages()
    {
        using var temporary = new TemporaryDirectory();
        var cards = MakeCards(("common", "First", "1"), ("rare", "Second", "2"));
        var handler = CreateHandler(cards);
        using var client = new HttpClient(handler);
        using var importer = new ScryfallSetImporter(client, static (_, _) => Task.CompletedTask, TimeSpan.Zero);

        var exception = await Assert.ThrowsAsync<ScryfallImportException>(() =>
            importer.ImportAsync("Zendikar", temporary.Path, false, 1, 2_097_152));

        Assert.Contains("configured limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.Host == "cards.scryfall.io");
    }

    [Fact]
    public async Task EnforcesSourceImageByteLimitWithoutCommittingEmptyImport()
    {
        using var temporary = new TemporaryDirectory();
        var handler = CreateHandler(MakeCards(("common", "Large image", "1")));
        using var client = new HttpClient(handler);
        using var importer = new ScryfallSetImporter(client, static (_, _) => Task.CompletedTask, TimeSpan.Zero);

        await Assert.ThrowsAsync<ScryfallImportException>(() =>
            importer.ImportAsync("Zendikar", temporary.Path, false, 100, 1));

        Assert.False(Directory.Exists(Path.Combine(temporary.Path, "Zendikar")));
        Assert.Empty(Directory.EnumerateDirectories(temporary.Path).Where(path => Path.GetFileName(path).Contains("staging-", StringComparison.Ordinal)));
    }

    private static StubHttpHandler CreateHandler(
        IReadOnlyList<object> cards,
        int pageSize = int.MaxValue,
        Func<bool>? imageFailure = null)
    {
        var pageOne = cards.Take(pageSize).ToArray();
        var pageTwo = cards.Skip(pageSize).ToArray();
        var handler = new StubHttpHandler(request =>
        {
            var uri = request.RequestUri!;
            if (uri.Host == "api.scryfall.com" && uri.AbsolutePath == "/sets")
                return JsonResponse(new { data = new[] { new { code = "zen", name = "Zendikar", card_count = cards.Count } } });
            if (uri.Host == "api.scryfall.com" && uri.AbsolutePath == "/cards/search")
            {
                if (uri.Query.Contains("page=2", StringComparison.Ordinal))
                    return JsonResponse(new { objectType = "list", data = pageTwo, has_more = false, total_cards = cards.Count });
                var hasMore = pageTwo.Length > 0;
                return JsonResponse(new
                {
                    data = pageOne,
                    has_more = hasMore,
                    next_page = hasMore ? "https://api.scryfall.com/cards/search?page=2" : null,
                    total_cards = cards.Count,
                });
            }
            if (uri.Host.EndsWith("scryfall.io", StringComparison.Ordinal))
            {
                if (imageFailure?.Invoke() == true)
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                var content = new ByteArrayContent(CreatePng());
                content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        return handler;
    }

    private static IReadOnlyList<object> MakeCards(params (string Rarity, string Name, string CollectorNumber)[] cards) =>
        cards.Select(value => (object)new
        {
            id = Guid.NewGuid(),
            name = value.Name,
            collector_number = value.CollectorNumber,
            rarity = value.Rarity,
            image_uris = new { png = $"https://cards.scryfall.io/png/front/{value.CollectorNumber}.png" },
        }).ToArray();

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
    };

    private static byte[] CreatePng()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(10, 10));
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> RequestQueue { get; } = new();
        public IReadOnlyList<HttpRequestMessage> Requests => RequestQueue.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestQueue.Enqueue(request);
            return Task.FromResult(response(request));
        }
    }
}
