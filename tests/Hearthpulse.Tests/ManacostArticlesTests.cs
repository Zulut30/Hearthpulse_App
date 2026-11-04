using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Tests;

/// <summary>
/// The Tavern Brawl article of hs-manacost.ru (<c>docs/brawl.md</c>, package B2): the week's post picked from the site's
/// WordPress answer (fixture of 2026-11-04), its mid-size cover, plain text, the 6-hour cache and the offline fallback,
/// nothing current after 14 days, only links on the site.
/// </summary>
public sealed class ManacostArticlesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-manacost-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The temp folder takes what is left.
        }
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Manacost", name));

    /// <summary>The site: the posts list and the one media answer, or nothing at all (offline).</summary>
    private sealed class Site(bool online = true) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Asked.Add(url);
            if (!online)
            {
                throw new HttpRequestException("offline");
            }

            if (url.EndsWith(".jpg", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xD9]) });
            }

            var body = url.Contains("/posts?", StringComparison.Ordinal) ? Fixture("posts.json")
                : url.Contains("/media/319837", StringComparison.Ordinal) ? Fixture("media-319837.json")
                : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private ManacostArticles Articles(HttpMessageHandler site) => new(new HttpClient(site), _dir) { Clock = () => Now };

    [Fact]
    public async Task The_weeks_post_comes_with_its_mid_size_cover_and_plain_text()
    {
        var site = new Site();
        var articles = await Articles(site).LoadAsync();

        var main = Assert.IsType<ManacostArticle>(articles.Main);
        Assert.StartsWith("Потасовка недели: Битва зомбозверей", main.Title, StringComparison.Ordinal);
        Assert.Equal(ManacostArticles.Host, main.Link.Host);
        Assert.Equal("https://hs-manacost.ru/wp-content/uploads/2025/10/zombeast-brawl-cover-768x390.jpg", main.Cover?.AbsoluteUri);
        Assert.DoesNotContain("<", main.Excerpt, StringComparison.Ordinal);
        Assert.DoesNotContain("&", main.Excerpt, StringComparison.Ordinal);
        // The deck collections of mid-October are over 10 days old by now.
        Assert.Null(articles.Decks);
        // Only the chosen post's cover is asked for, and downloaded into the cache folder.
        Assert.Equal(3, site.Asked.Count);
        Assert.True(File.Exists(main.CoverFile));
        Assert.StartsWith(_dir, main.CoverFile, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_list_is_cached_for_six_hours_and_stands_in_offline()
    {
        Assert.NotNull((await Articles(new Site()).LoadAsync()).Main);

        var offline = new Site(online: false);
        Assert.NotNull((await Articles(offline).LoadAsync()).Main);
        Assert.Empty(offline.Asked);

        File.SetLastWriteTimeUtc(Path.Combine(_dir, ManacostArticles.CacheName), Now - ManacostArticles.MaxAge - TimeSpan.FromMinutes(1));
        var stale = await Articles(offline).LoadAsync();
        Assert.NotEmpty(offline.Asked);
        Assert.NotNull(stale.Main);
    }

    [Fact]
    public async Task Nothing_current_after_two_weeks_and_nothing_without_the_site()
    {
        var late = new ManacostArticles(new HttpClient(new Site()), _dir) { Clock = () => Now.AddDays(20) };
        Assert.Same(BrawlArticles.Empty, await late.LoadAsync());

        var nothing = await new ManacostArticles(new HttpClient(new Site(online: false)), Path.Combine(_dir, "empty")) { Clock = () => Now }.LoadAsync();
        Assert.Same(BrawlArticles.Empty, nothing);
    }

    [Fact]
    public void Posts_off_the_site_are_dropped_and_entities_decoded()
    {
        using var doc = JsonDocument.Parse("""
            [
              {"id":1,"date":"2026-11-01T10:00:00","link":"https://evil.example/x","title":{"rendered":"Потасовка недели: чужая"},"excerpt":{"rendered":""},"featured_media":0},
              {"id":2,"date":"2026-11-01T11:00:00","link":"https://hs-manacost.ru/a/","title":{"rendered":"Потасовка недели: &#171;Огонь&#187; &amp; лёд"},"excerpt":{"rendered":"<p>Коротко&nbsp;о главном [&hellip;]</p>"},"featured_media":0}
            ]
            """);
        var post = Assert.Single(ManacostArticles.ParsePosts(doc.RootElement)).Article;
        Assert.Equal("Потасовка недели: «Огонь» & лёд", post.Title);
        Assert.Equal("Коротко о главном …", post.Excerpt);
    }

    [Theory]
    [InlineData("https://hs-manacost.ru/pot/", true)]
    [InlineData("https://www.hs-manacost.ru/pot/", true)]
    [InlineData("http://hs-manacost.ru/pot/", false)]
    [InlineData("https://hs-manacost.ru.evil.example/", false)]
    [InlineData("https://evil.example/hs-manacost.ru", false)]
    [InlineData("javascript:alert(1)", false)]
    public void Only_https_links_on_the_site_are_kept(string url, bool kept) =>
        Assert.Equal(kept, ManacostArticles.OnSite(url) is not null);

    [Fact]
    public void The_deck_collection_is_the_newest_one_of_the_same_week_and_a_brawl_name_picks_the_post()
    {
        static ManacostArticle Post(string title, int day) =>
            new(title, string.Empty, new DateTime(2026, 11, day, 10, 0, 0, DateTimeKind.Utc), new Uri("https://hs-manacost.ru/" + day), null);
        ManacostArticle[] posts =
        [
            Post("Потасовка недели: Битва зомбозверей", 4),
            Post("Потасовка недели: Огненный фестиваль", 3),
            Post("12 колод для Потасовки", 5),
            Post("Старые колоды", 1),
        ];
        var now = new DateTime(2026, 11, 6, 0, 0, 0, DateTimeKind.Utc);

        var picked = ManacostArticles.Select(posts, null, now);
        Assert.Equal("Потасовка недели: Битва зомбозверей", picked.Main?.Title);
        Assert.Equal("12 колод для Потасовки", picked.Decks?.Title);

        Assert.Equal("Потасовка недели: Огненный фестиваль", ManacostArticles.Select(posts, "Огненный фестиваль", now).Main?.Title);
    }
}
