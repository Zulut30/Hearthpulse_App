using System.Net;
using System.Text;
using Hearthpulse.Core.Pool;

namespace Hearthpulse.Tests;

/// <summary>
/// <see cref="AnalyticsClient.LoadBgHeroesAsync"/> against the C5 response of TASK-2026-11-09 (fixture
/// Fixtures/Analytics/bg-heroes.json): the request, the parsed groups and rows, "not available" answers, and every
/// failure ending in null. No network: a fake handler answers.
/// </summary>
public sealed class AnalyticsClientTests
{
    private const string Endpoint = "https://pool.example/stats";

    private static string Fixture() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Analytics", "bg-heroes.json"));

    [Fact]
    public async Task Published_snapshot_is_requested_with_get_and_parsed_into_groups_and_rows()
    {
        var server = new FakeServer(HttpStatusCode.OK, Fixture());
        using var http = new HttpClient(server);

        var result = await AnalyticsClient.LoadBgHeroesAsync(http, Endpoint + "/");

        Assert.Equal(["GET https://pool.example/stats/v2/analytics/bg/heroes?mode=solo"], server.Requests);
        Assert.NotNull(result);
        Assert.True(result.Available);
        Assert.Null(result.Reason);
        Assert.Equal(("bg.hero.offer_pick_placement", "bg-hero-1", "solo"), (result.Metric, result.RulesVersion, result.Mode));
        Assert.Equal((1762646400000L, false, 20371, 20401), (result.Published, result.Stale, result.FromDay, result.ToDay));
        Assert.Equal(2, result.Groups.Count);
        var group = result.Groups[0];
        Assert.Equal((223456, 120, 14, 3), (group.Build, group.Captures, group.Sources, group.Suppressed));
        var hero = group.Heroes[0];
        Assert.Equal(("TB_BaconShop_HERO_xx", 40, 12, "ok"), (hero.HeroCardId, hero.Offered, hero.Picked, hero.Quality));
        Assert.Equal(new AnalyticsRate(12, 40, 0.3, 0.181, 0.454), hero.PickRate);
        Assert.Equal((12, 3.92, 3.05, 4.79), (hero.Placement.Count, hero.Placement.Mean, hero.Placement.Low, hero.Placement.High));
        Assert.Equal([2, 1, 2, 2, 1, 2, 1, 1], hero.Placement.Distribution);
        Assert.Equal(new AnalyticsRate(7, 12, 0.583, 0.320, 0.807), hero.Top4);
        Assert.Equal(new AnalyticsRate(2, 12, 0.167, 0.047, 0.448), hero.First);
        var small = group.Heroes[1];
        Assert.Equal("small_sample", small.Quality);
        Assert.Equal(new AnalyticsRate(1, 6, null, null, null), small.PickRate);
        Assert.Null(small.Placement.Mean);
        Assert.Null(result.Groups[1].Build);
        Assert.Empty(result.Groups[1].Heroes);
    }

    [Theory]
    [InlineData("not_published")]
    [InlineData("rebuilding")]
    [InlineData("unsupported_mode")]
    public async Task Unavailable_snapshot_is_an_object_with_its_reason(string reason)
    {
        var body = $$"""
            {"v":2,"metric":"bg.hero.offer_pick_placement","rulesVersion":"bg-hero-1","mode":"solo","available":false,
             "reason":"{{reason}}","published":null,"stale":false,"source":"source_capture","period":null,"groups":[]}
            """;
        using var http = new HttpClient(new FakeServer(HttpStatusCode.OK, body));

        var result = await AnalyticsClient.LoadBgHeroesAsync(http, Endpoint);

        Assert.NotNull(result);
        Assert.False(result.Available);
        Assert.Equal(reason, result.Reason);
        Assert.Empty(result.Groups);
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"ok\":false}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"ok\":false,\"error\":\"invalid_query\"}")]
    [InlineData(HttpStatusCode.OK, "{\"v\":2,\"available\":tru")]
    [InlineData(HttpStatusCode.OK, "<html>maintenance</html>")]
    [InlineData(HttpStatusCode.OK, "{\"v\":2,\"available\":true}")]
    [InlineData(HttpStatusCode.OK, "{\"v\":1}")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task Network_error_failure_status_or_broken_json_is_null_without_an_exception(HttpStatusCode? status, string body)
    {
        using var http = new HttpClient(new FakeServer(status, body));

        Assert.Null(await AnalyticsClient.LoadBgHeroesAsync(http, Endpoint));
    }

    [Theory]
    [InlineData("http://pool.example/stats")]
    [InlineData("")]
    [InlineData("pool.example/stats")]
    [InlineData("https://user:secret@pool.example/stats")]
    public async Task Endpoint_other_than_plain_https_is_null_and_sends_nothing(string endpoint)
    {
        var server = new FakeServer(HttpStatusCode.OK, Fixture());
        using var http = new HttpClient(server);

        Assert.Null(await AnalyticsClient.LoadBgHeroesAsync(http, endpoint));
        Assert.Empty(server.Requests);
    }

    /// <summary>Answers every request with one status and body; a null status throws like a machine without network.</summary>
    private sealed class FakeServer(HttpStatusCode? status, string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method} {request.RequestUri}");
            return status is { } code
                ? Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") })
                : throw new HttpRequestException("No route to host");
        }
    }
}
