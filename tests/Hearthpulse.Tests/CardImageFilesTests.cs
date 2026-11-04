using System.Net;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Tests;

/// <summary>
/// The files behind card images both UIs show (Etalon Q5d, D8): where each kind comes from and is kept, the download
/// that keeps web pages out of the cache, the fallback source, addresses without art, and the render cache's trim.
/// </summary>
public sealed class CardImageFilesTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 1, 2];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1, 1];
    private static readonly byte[] WebP = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8];
    private static readonly byte[] Page = [.. "<!doctype html><html>"u8];

    [Theory]
    [InlineData(ArtKind.Tile, "tiles/EX1_001.png", "tile/EX1_001.png")]
    [InlineData(ArtKind.Portrait, "256x/EX1_001.jpg", "portrait/EX1_001.jpg")]
    [InlineData(ArtKind.Splash, "512x/EX1_001.jpg", "splash/EX1_001.jpg")]
    [InlineData(ArtKind.Card, "render/latest/ruRU/256x/EX1_001.png", "card/ruRU/EX1_001.png")]
    [InlineData(ArtKind.BattlegroundsCard, "bgs/latest/ruRU/256x/EX1_001.png", "battlegroundscard/EX1_001.png")]
    public void Each_kind_comes_from_its_address_and_lives_in_its_folder(ArtKind kind, string url, string file)
    {
        var (u, f) = CardImageFiles.Art(kind, "EX1_001", "ruRU", Path.Combine("cache", "images"));
        Assert.Equal("https://art.hearthstonejson.com/v1/" + url, u);
        Assert.Equal(Path.Combine("cache", "images", file.Replace('/', Path.DirectorySeparatorChar)), f);
        Assert.Equal($"{kind}:EX1_001", CardImageFiles.Key(kind, "EX1_001"));
    }

    [Fact]
    public void A_render_for_the_pool_browser_keeps_a_folder_per_locale()
    {
        var (url, file) = CardImageFiles.Render("CS2_029", "deDE", "images");
        Assert.Equal("https://art.hearthstonejson.com/v1/render/latest/deDE/256x/CS2_029.png", url);
        Assert.Equal(Path.Combine("images", "render-deDE", "CS2_029.png"), file);
    }

    [Fact]
    public void Only_png_jpeg_and_webp_count_as_images_and_a_render_must_be_png()
    {
        Assert.True(CardImageFiles.LooksLikeImage(Png));
        Assert.True(CardImageFiles.LooksLikeImage(Jpeg));
        Assert.True(CardImageFiles.LooksLikeImage(WebP));
        Assert.False(CardImageFiles.LooksLikeImage(Page));
        // Too short to be an image at all.
        Assert.False(CardImageFiles.LooksLikeImage(Png.AsSpan(0, 12)));

        Assert.True(CardImageFiles.LooksLikePng(Png));
        Assert.True(CardImageFiles.LooksLikePng(Png.AsSpan(0, 8)));
        Assert.False(CardImageFiles.LooksLikePng(Png.AsSpan(0, 7)));
        Assert.False(CardImageFiles.LooksLikePng(Jpeg));
    }

    [Fact]
    public async Task Art_is_downloaded_once_and_a_file_on_disk_is_not_asked_for()
    {
        var folder = TestDataFolder.Scratch("art");
        var site = new FakeArt { ["https://a.test/tile.png"] = (HttpStatusCode.OK, Png) };
        using var http = new HttpClient(site);
        var files = new CardImageFiles(downloads: 2);
        var file = Path.Combine(folder, "tile", "EX1_001.png");

        await files.EnsureArtAsync(http, "https://a.test/tile.png", file, null);
        Assert.Equal(Png, await File.ReadAllBytesAsync(file));
        Assert.False(File.Exists(file + ".tmp"));

        await files.EnsureArtAsync(http, "https://a.test/tile.png", file, () => throw new InvalidOperationException("not asked"));
        Assert.Equal(["https://a.test/tile.png"], site.Asked);
    }

    [Fact]
    public async Task Without_art_at_the_first_address_the_fallback_is_asked_and_a_404_is_not_asked_again()
    {
        var folder = TestDataFolder.Scratch("art");
        var site = new FakeArt
        {
            ["https://a.test/missing.png"] = (HttpStatusCode.NotFound, []),
            ["https://b.test/BG_001.png"] = (HttpStatusCode.OK, WebP),
        };
        using var http = new HttpClient(site);
        var files = new CardImageFiles(downloads: 1);
        var file = Path.Combine(folder, "battlegroundscard", "BG_001.png");

        await files.EnsureArtAsync(http, "https://a.test/missing.png", file, () => "https://b.test/BG_001.png");
        Assert.Equal(WebP, await File.ReadAllBytesAsync(file));

        // Another card at the same missing address: the 404 is remembered, and no fallback means no file.
        var other = Path.Combine(folder, "battlegroundscard", "BG_002.png");
        await files.EnsureArtAsync(http, "https://a.test/missing.png", other, null);
        Assert.False(File.Exists(other));
        Assert.Equal(["https://a.test/missing.png", "https://b.test/BG_001.png"], site.Asked);
    }

    [Fact]
    public async Task A_web_page_or_a_failed_request_leaves_no_file_and_is_logged()
    {
        var folder = TestDataFolder.Scratch("art");
        var site = new FakeArt
        {
            ["https://a.test/portal.jpg"] = (HttpStatusCode.OK, Page),
            ["https://a.test/busy.jpg"] = (HttpStatusCode.ServiceUnavailable, []),
        };
        using var http = new HttpClient(site);
        var files = new CardImageFiles(downloads: 1);

        var portal = Path.Combine(folder, "portal.jpg");
        await files.EnsureArtAsync(http, "https://a.test/portal.jpg", portal, null);
        Assert.False(File.Exists(portal));
        Assert.Contains("Art download was not an image (https://a.test/portal.jpg)", TestDataFolder.ReadLog(), StringComparison.Ordinal);

        // A 503 is not remembered as missing: the next try asks again.
        var busy = Path.Combine(folder, "busy.jpg");
        await files.EnsureArtAsync(http, "https://a.test/busy.jpg", busy, null);
        await files.EnsureArtAsync(http, "https://a.test/busy.jpg", busy, null);
        Assert.False(File.Exists(busy));
        Assert.Equal(2, site.Asked.Count(u => u == "https://a.test/busy.jpg"));

        // The network itself failing.
        var down = Path.Combine(folder, "down.jpg");
        await files.EnsureArtAsync(http, "https://a.test/down.jpg", down, null);
        Assert.False(File.Exists(down));
        Assert.Contains("Art download failed (https://a.test/down.jpg)", TestDataFolder.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_render_is_kept_only_as_png_and_a_network_failure_is_the_callers()
    {
        var folder = TestDataFolder.Scratch("render");
        var site = new FakeArt
        {
            ["https://a.test/r1.png"] = (HttpStatusCode.OK, Png),
            ["https://a.test/r2.png"] = (HttpStatusCode.OK, Jpeg),
            ["https://a.test/r3.png"] = (HttpStatusCode.NotFound, []),
        };
        using var http = new HttpClient(site);
        var files = new CardImageFiles(downloads: 1);

        Assert.True(await files.DownloadRenderAsync(http, "https://a.test/r1.png", Path.Combine(folder, "r1.png")));
        Assert.Equal(Png, await File.ReadAllBytesAsync(Path.Combine(folder, "r1.png")));
        Assert.False(await files.DownloadRenderAsync(http, "https://a.test/r2.png", Path.Combine(folder, "r2.png")));
        Assert.Contains("Card render download was not a PNG (https://a.test/r2.png)", TestDataFolder.ReadLog(), StringComparison.Ordinal);
        Assert.False(await files.DownloadRenderAsync(http, "https://a.test/r3.png", Path.Combine(folder, "r3.png")));
        Assert.False(File.Exists(Path.Combine(folder, "r2.png")));
        await Assert.ThrowsAsync<HttpRequestException>(() => files.DownloadRenderAsync(http, "https://a.test/down.png", Path.Combine(folder, "r4.png")));

        // The download slot is given back after a failure: the next render still downloads.
        Assert.True(await files.DownloadRenderAsync(http, "https://a.test/r1.png", Path.Combine(folder, "r5.png")));
    }

    [Fact]
    public void The_render_folder_keeps_its_newest_files_once_a_session()
    {
        var folder = TestDataFolder.Scratch("render-trim");
        Directory.CreateDirectory(folder);
        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(folder, $"c{i}.png");
            File.WriteAllBytes(path, Png);
            File.SetLastWriteTimeUtc(path, start.AddHours(i));
        }

        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not a render");
        var files = new CardImageFiles(downloads: 1);
        files.TrimOnce(folder, 3);
        Assert.Equal(["c2.png", "c3.png", "c4.png", "notes.txt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // Once a session: files added later stay until the next one.
        File.WriteAllBytes(Path.Combine(folder, "c5.png"), Png);
        files.TrimOnce(folder, 3);
        Assert.Equal(4, Directory.GetFiles(folder, "*.png").Length);

        // A folder not there yet is no error.
        files.TrimOnce(Path.Combine(folder, "none"), 3);
    }

    [Fact]
    public void An_unreadable_file_is_deleted_and_a_missing_one_is_no_error()
    {
        var file = TestDataFolder.Scratch("bad") + ".png";
        File.WriteAllBytes(file, Page);
        CardImageFiles.TryDelete(file);
        Assert.False(File.Exists(file));
        CardImageFiles.TryDelete(file);
    }

    /// <summary>Answers each known address with its status and bytes; any other address fails as a network error.</summary>
    private sealed class FakeArt : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, byte[] Body)> _answers = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = [];

        public (HttpStatusCode, byte[]) this[string url]
        {
            set => _answers[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (Asked)
            {
                Asked.Add(url);
            }

            if (!_answers.TryGetValue(url, out var answer))
            {
                throw new HttpRequestException("no route to the art server in a test");
            }

            return Task.FromResult(new HttpResponseMessage(answer.Status) { Content = new ByteArrayContent(answer.Body) });
        }
    }
}
