using System.Net;
using System.Text;
using Hearthpulse.Core.Constructed;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Russian archetype names from api.blizzcore.ru: matching by English name, the fallback to built-in labels and English,
/// the request (one, with a limit above the default 200), the disk cache and the offline fallback. No network: a fake
/// handler answers.
/// </summary>
public sealed class ArchetypeTranslationsTests : IDisposable
{
    private const string Answer = """
        {"count": 4, "success": true, "items": [
          {"id": 252, "name_en": "AYAYA Rogue", "name_ru": "Айя Разбойник", "created_at": "2026-07-10T21:57:25+00:00"},
          {"id": 178, "name_en": "Animancer Warlock", "name_ru": "Анимансер Чернокнижник"},
          {"id": 90, "name_en": "Mecha’thun Druid", "name_ru": "Меха'тун Друид"},
          {"id": 91, "name_en": "Empty Name", "name_ru": ""}
        ]}
        """;

    private readonly string _cache = Path.Combine(TestDataFolder.Path, "blizzcore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public void Names_match_by_English_name_ignoring_case_spacing_and_apostrophe_style()
    {
        var names = ArchetypeTranslations.Parse(Answer);

        Assert.Equal(3, names.Count);
        Assert.Equal("Айя Разбойник", names.Russian("Ayaya Rogue"));
        Assert.Equal("Анимансер Чернокнижник", names.Russian("  animancer   WARLOCK "));
        Assert.Equal("Меха'тун Друид", names.Russian("Mecha'thun Druid"));
        Assert.Null(names.Russian("Empty Name"));
        Assert.Null(names.Russian("Zoolock"));
    }

    [Fact]
    public void Missing_names_fall_back_to_the_built_in_label_then_English()
    {
        var names = ArchetypeTranslations.Parse(Answer);

        Assert.Equal("Айя Разбойник", names.RussianLabel("AYAYA Rogue"));
        Assert.Equal(ArchetypeNames.RussianLabel("Aggro Paladin"), names.RussianLabel("Aggro Paladin"));
        Assert.Equal("Агро Паладин", names.RussianLabel("Aggro Paladin"));
        Assert.Equal("Totally New Mage", names.RussianLabel("Totally New Mage"));
        // The tests run in English: the interface shows the English name.
        Assert.Equal("AYAYA Rogue", names.Display("AYAYA Rogue"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"items\": 5}")]
    [InlineData("{\"count\": 0}")]
    public void Unreadable_answers_give_no_names(string text) => Assert.Equal(0, ArchetypeTranslations.Parse(text).Count);

    [Fact]
    public void A_bare_list_is_read_too() =>
        Assert.Equal("Айя Разбойник", ArchetypeTranslations.Parse("""[{"name_en": "AYAYA Rogue", "name_ru": "Айя Разбойник"}]""").Russian("AYAYA Rogue"));

    [Fact]
    public async Task One_request_with_a_limit_then_the_cache_answers()
    {
        var server = new FakeServer(HttpStatusCode.OK, Answer);
        using var http = new HttpClient(server);

        var first = await ArchetypeTranslations.LoadAsync(http, _cache);
        var second = await ArchetypeTranslations.LoadAsync(http, _cache);

        Assert.Equal(3, first.Count);
        Assert.Equal(3, second.Count);
        var request = Assert.Single(server.Requests);
        Assert.Equal("https://api.blizzcore.ru/archetypes?limit=1000", request);
        Assert.True(File.Exists(Path.Combine(_cache, "blizzcore-archetypes.json")));
    }

    [Fact]
    public async Task Offline_the_old_list_is_used()
    {
        using (var http = new HttpClient(new FakeServer(HttpStatusCode.OK, Answer)))
        {
            await ArchetypeTranslations.LoadAsync(http, _cache);
        }

        File.SetLastWriteTimeUtc(Path.Combine(_cache, "blizzcore-archetypes.json"), DateTime.UtcNow - ArchetypeTranslations.MaxAge - TimeSpan.FromHours(1));
        var offline = new FakeServer(null, string.Empty);
        using var client = new HttpClient(offline);

        var names = await ArchetypeTranslations.LoadAsync(client, _cache);

        Assert.Single(offline.Requests);
        Assert.Equal("Айя Разбойник", names.Russian("AYAYA Rogue"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"detail\": \"down\"}")]
    [InlineData(HttpStatusCode.OK, "<html>maintenance</html>")]
    [InlineData(null, "")]
    public async Task A_failed_first_load_gives_no_names_and_writes_no_cache(HttpStatusCode? status, string body)
    {
        using var http = new HttpClient(new FakeServer(status, body));

        var names = await ArchetypeTranslations.LoadAsync(http, _cache);

        Assert.Equal(0, names.Count);
        Assert.False(File.Exists(Path.Combine(_cache, "blizzcore-archetypes.json")));
    }

    [Fact]
    public async Task A_fresh_but_broken_cache_is_fetched_again()
    {
        Directory.CreateDirectory(_cache);
        await File.WriteAllTextAsync(Path.Combine(_cache, "blizzcore-archetypes.json"), "{\"items\": [");
        var server = new FakeServer(HttpStatusCode.OK, Answer);
        using var http = new HttpClient(server);

        var names = await ArchetypeTranslations.LoadAsync(http, _cache);

        Assert.Single(server.Requests);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public async Task After_a_failure_the_next_starts_wait_before_asking_again()
    {
        var offline = new FakeServer(null, string.Empty);
        using var http = new HttpClient(offline);

        await ArchetypeTranslations.LoadAsync(http, _cache);
        await ArchetypeTranslations.LoadAsync(http, _cache);
        Assert.Single(offline.Requests);

        // An hour after the first failure the wait is over; the second failure doubles it.
        var marker = Path.Combine(_cache, "blizzcore-archetypes.failed");
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - TimeSpan.FromMinutes(61));
        await ArchetypeTranslations.LoadAsync(http, _cache);
        Assert.Equal(2, offline.Requests.Count);
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - TimeSpan.FromMinutes(61));
        await ArchetypeTranslations.LoadAsync(http, _cache);
        Assert.Equal(2, offline.Requests.Count);

        // A success clears the count.
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow - TimeSpan.FromHours(3));
        using var online = new HttpClient(new FakeServer(HttpStatusCode.OK, Answer));
        Assert.Equal(3, (await ArchetypeTranslations.LoadAsync(online, _cache)).Count);
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(4, 6)]
    [InlineData(20, 6)]
    public void Retries_back_off_from_one_to_six_hours(int failures, int hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), ArchetypeTranslations.RetryAfter(failures));

    [Fact]
    public async Task A_host_that_never_answers_is_given_up_quickly()
    {
        var old = ArchetypeTranslations.FetchTimeout;
        ArchetypeTranslations.FetchTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            using var http = new HttpClient(new FakeServer(HttpStatusCode.OK, Answer) { Delay = TimeSpan.FromSeconds(30) });
            var watch = System.Diagnostics.Stopwatch.StartNew();

            var names = await ArchetypeTranslations.LoadAsync(http, _cache);

            Assert.Equal(0, names.Count);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        }
        finally
        {
            ArchetypeTranslations.FetchTimeout = old;
        }
    }

    [Fact]
    public void Odd_names_are_dropped_and_long_ones_cut()
    {
        var names = ArchetypeTranslations.Parse("""
            {"items": [
              {"name_en": "Long Rogue", "name_ru": "Очень длинное название архетипа, которое не помещается в строку"},
              {"name_en": "Html Mage", "name_ru": "<b>Маг</b>"},
              {"name_en": "Digits Hunter", "name_ru": "12345"},
              {"name_en": "Spaced Druid", "name_ru": "  Друид   на   драконах "}
            ]}
            """);

        var cut = names.Russian("Long Rogue")!;
        Assert.Equal(ArchetypeTranslations.MaxNameLength, cut.Length);
        Assert.EndsWith("…", cut, StringComparison.Ordinal);
        Assert.Null(names.Russian("Html Mage"));
        Assert.Null(names.Russian("Digits Hunter"));
        Assert.Equal("Друид на драконах", names.Russian("Spaced Druid"));
    }

    /// <summary>Answers every request with one status and body; a null status throws like a machine without network.</summary>
    private sealed class FakeServer(HttpStatusCode? status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        /// <summary>How long the answer takes (it honours cancellation, like a real connection).</summary>
        public TimeSpan Delay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            return status is { } code
                ? new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") }
                : throw new HttpRequestException("No route to host");
        }
    }
}
