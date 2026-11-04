using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hearthpulse.Core.Api;

/// <summary>One article of hs-manacost.ru: plain text title and teaser, the post's date, its link and cover (both on the site).</summary>
public sealed record ManacostArticle(string Title, string Excerpt, DateTime Date, Uri Link, Uri? Cover)
{
    /// <summary>The cover downloaded into the cache folder (the views show a local file), or null.</summary>
    public string? CoverFile { get; init; }
}

/// <summary>The site's article on this week's Tavern Brawl and, when there is one, its deck collection (docs/brawl.md).</summary>
public sealed record BrawlArticles(ManacostArticle? Main, ManacostArticle? Decks)
{
    public static BrawlArticles Empty { get; } = new(null, null);

    public bool HasAny => Main is not null || Decks is not null;
}

/// <summary>
/// The Tavern Brawl articles of hs-manacost.ru (the owner's site; WordPress REST, category "Потасовка", id 1036): the
/// newest "Потасовка недели" post as the week's article, the newest deck collection beside it. Cached on disk for
/// <see cref="MaxAge"/>; offline, the cached list stands in. Only links and covers on the site itself are kept. Never
/// throws.
/// </summary>
/// <param name="cacheDirectory">Where the list is cached; the app's API cache folder by default (tests pass a scratch one).</param>
public sealed partial class ManacostArticles(HttpClient http, string? cacheDirectory = null)
{
    /// <summary>The site; every link and cover must be on it.</summary>
    public const string Host = "hs-manacost.ru";

    internal const string PostsUrl = "https://hs-manacost.ru/wp-json/wp/v2/posts?categories=1036&per_page=10&_fields=id,date,link,title,excerpt,featured_media";

    internal const string MediaUrl = "https://hs-manacost.ru/wp-json/wp/v2/media/{0}?_fields=source_url,media_details";

    internal const string CacheName = "manacost-brawl.json";

    /// <summary>How long the cached list is fresh.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>A week's article older than this is not this week's.</summary>
    public static readonly TimeSpan MaxCurrent = TimeSpan.FromDays(14);

    /// <summary>A deck collection older than this is not shown.</summary>
    public static readonly TimeSpan MaxDecks = TimeSpan.FromDays(10);

    /// <summary>One request's limit: the page must not wait on a slow site.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string WeekPrefix = "Потасовка недели";

    /// <summary>The largest cover kept (a mid-size JPEG is about 60 KB).</summary>
    internal const int MaxCover = 1024 * 1024;

    /// <summary>The longest teaser kept: one or two lines on the card.</summary>
    internal const int MaxExcerpt = 220;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.Cache, "api");

    /// <summary>Now (UTC), for the articles' age; tests set it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags { get; }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces { get; }

    /// <summary>
    /// The week's article and deck collection. <paramref name="brawlName"/>, when known, picks the week's post whose title
    /// shares the most words with it. <see cref="BrawlArticles.Empty"/> when the site and the cache have nothing current.
    /// </summary>
    public async Task<BrawlArticles> LoadAsync(string? brawlName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var file = Path.Combine(_cacheDirectory, CacheName);
            var info = new FileInfo(file);
            var cached = info.Exists ? ReadCache(file) : null;
            if (cached is not null && Clock() - info.LastWriteTimeUtc <= MaxAge)
            {
                return Select(cached, brawlName, Clock());
            }

            if (await FetchAsync(cancellationToken).ConfigureAwait(false) is { } fetched)
            {
                Directory.CreateDirectory(_cacheDirectory);
                await HearthpulseData.WriteCacheAsync(file, JsonSerializer.Serialize(fetched, Json), cancellationToken).ConfigureAwait(false);
                return Select(fetched, brawlName, Clock());
            }

            return cached is null ? BrawlArticles.Empty : Select(cached, brawlName, Clock());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diag.Info("hs-manacost.ru Brawl articles: " + ex.Message);
            return BrawlArticles.Empty;
        }
    }

    /// <summary>The posts with the covers of the two that will show, or null when the site did not answer.</summary>
    private async Task<List<ManacostArticle>?> FetchAsync(CancellationToken cancellationToken)
    {
        if (await GetAsync(PostsUrl, cancellationToken).ConfigureAwait(false) is not { } posts)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(posts);
        var list = ParsePosts(doc.RootElement);
        var chosen = Select(list.Select(p => p.Article).ToList(), null, Clock());
        var result = new List<ManacostArticle>(list.Count);
        foreach (var (article, media) in list)
        {
            var withCover = article;
            if (media > 0 && ReferenceEquals(article, chosen.Main)
                && await GetAsync(string.Format(CultureInfo.InvariantCulture, MediaUrl, media), cancellationToken).ConfigureAwait(false) is { } answer)
            {
                using var mediaDoc = JsonDocument.Parse(answer);
                var cover = ParseCover(mediaDoc.RootElement);
                withCover = article with { Cover = cover, CoverFile = cover is null ? null : await DownloadCoverAsync(cover, media, cancellationToken).ConfigureAwait(false) };
            }

            result.Add(withCover);
        }

        return result;
    }

    /// <summary>The cover as a file in the cache folder (kept between loads), or null when it could not be had.</summary>
    private async Task<string?> DownloadCoverAsync(Uri cover, int media, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(cover.AbsolutePath).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            extension = ".jpg";
        }

        var file = Path.Combine(_cacheDirectory, string.Create(CultureInfo.InvariantCulture, $"manacost-cover-{media}{extension}"));
        if (File.Exists(file))
        {
            return file;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(cover, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxCover)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxCover)
            {
                return null;
            }

            Directory.CreateDirectory(_cacheDirectory);
            // Two loads at once (start-up and a refresh) may fetch the same cover: written aside, then moved in; the
            // loser keeps the winner's file.
            var part = file + "." + Guid.NewGuid().ToString("N") + ".part";
            await File.WriteAllBytesAsync(part, bytes, timeout.Token).ConfigureAwait(false);
            try
            {
                File.Move(part, file);
            }
            catch (IOException) when (File.Exists(file))
            {
                File.Delete(part);
            }

            return file;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Diag.Info("hs-manacost.ru cover: " + ex.Message);
            return null;
        }
    }

    private async Task<string?> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.GetAsync(new Uri(url), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Diag.Info($"hs-manacost.ru: {(int)response.StatusCode} for {url}");
                return null;
            }

            return await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Diag.Info("hs-manacost.ru: " + ex.Message);
            return null;
        }
    }

    /// <summary>WordPress posts (<c>title.rendered</c>, <c>excerpt.rendered</c>, <c>date</c>, <c>link</c>, <c>featured_media</c>); posts off the site are dropped.</summary>
    internal static List<(ManacostArticle Article, int Media)> ParsePosts(JsonElement root)
    {
        var result = new List<(ManacostArticle, int)>();
        if (root.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var post in root.EnumerateArray())
        {
            if (post.ValueKind != JsonValueKind.Object
                || Rendered(post, "title") is not { Length: > 0 } title
                || OnSite(Text(post, "link")) is not { } link
                || !DateTime.TryParse(Text(post, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
            {
                continue;
            }

            var media = post.TryGetProperty("featured_media", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            result.Add((new ManacostArticle(title, Cut(Rendered(post, "excerpt") ?? string.Empty), date.ToUniversalTime(), link, null), media));
        }

        return result;
    }

    /// <summary>The cover in a mid size (<c>medium_large</c>, else <c>medium</c>), never the original; null when it is off the site.</summary>
    internal static Uri? ParseCover(JsonElement media)
    {
        if (media.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (media.TryGetProperty("media_details", out var details) && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("sizes", out var sizes) && sizes.ValueKind == JsonValueKind.Object)
        {
            foreach (var size in new[] { "medium_large", "td_696x0", "medium" })
            {
                if (sizes.TryGetProperty(size, out var s) && OnSite(Text(s, "source_url")) is { } url)
                {
                    return url;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The newest "Потасовка недели" post (with a Brawl name: the one sharing most words with it), when under
    /// <see cref="MaxCurrent"/> old; and the newest deck collection under <see cref="MaxDecks"/> old, not older than the
    /// week's post.
    /// </summary>
    internal static BrawlArticles Select(IReadOnlyList<ManacostArticle> posts, string? brawlName, DateTime now)
    {
        var weeks = posts.Where(p => p.Title.StartsWith(WeekPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Date)
            .ToList();
        var main = brawlName is { Length: > 0 } name && weeks.MaxBy(p => SharedWords(p.Title, name)) is { } named && SharedWords(named.Title, name) > 0
            ? named
            : weeks.FirstOrDefault();
        if (main is not null && now - main.Date > MaxCurrent)
        {
            main = null;
        }

        var decks = posts.Where(p => !ReferenceEquals(p, main) && p.Title.Contains("колод", StringComparison.OrdinalIgnoreCase)
                                     && now - p.Date <= MaxDecks && (main is null || p.Date >= main.Date.AddDays(-1)))
            .MaxBy(p => p.Date);
        return main is null && decks is null ? BrawlArticles.Empty : new BrawlArticles(main, decks);
    }

    /// <summary>A link on the site over https (the site or a subdomain), else null.</summary>
    public static Uri? OnSite(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase))
            ? uri
            : null;

    /// <summary>HTML to one line of plain text: tags out, entities decoded, spaces collapsed.</summary>
    internal static string Plain(string html) => Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html, " ")), " ").Trim();

    private static string Cut(string html)
    {
        var text = Plain(html).Replace("[…]", "…", StringComparison.Ordinal).Replace("[&hellip;]", "…", StringComparison.Ordinal);
        return text.Length <= MaxExcerpt ? text : string.Concat(text.AsSpan(0, MaxExcerpt - 1).TrimEnd(), "…");
    }

    private static int SharedWords(string title, string name)
    {
        static HashSet<string> Words(string s) => Plain(s).ToLowerInvariant()
            .Split([' ', ',', '.', ':', '!', '?', '«', '»', '"', '-', '—'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 4).ToHashSet(StringComparer.Ordinal);
        var words = Words(name);
        return Words(title).Count(words.Contains);
    }

    private static string? Rendered(JsonElement post, string name) =>
        post.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object && Text(e, "rendered") is { } html ? Plain(html) : null;

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<ManacostArticle>? ReadCache(string file)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<ManacostArticle>>(File.ReadAllText(file), Json);
            // The cache is ours, but a link is checked again before it is shown or opened.
            return list?.Where(a => a.Title is { Length: > 0 } && OnSite(a.Link?.AbsoluteUri) is not null)
                .Select(a => a with { Cover = OnSite(a.Cover?.AbsoluteUri), CoverFile = a.CoverFile is { } f && File.Exists(f) ? f : null })
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Diag.Info("hs-manacost.ru cache unreadable: " + ex.Message);
            return null;
        }
    }
}
