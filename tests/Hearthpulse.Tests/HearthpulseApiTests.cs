using System.Text.Json;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Tests;

/// <summary>
/// The Hearthpulse API client: error envelopes parse into codes, and the community stats parse from the shapes the API
/// serves. Accounts (sign-in through hearthpulse.net) are covered by <see cref="AccountTests"/>.
/// </summary>
public sealed class HearthpulseApiTests
{
    [Theory]
    [InlineData(401, """{"detail":{"code":"INVALID_TOKEN","message":"Missing or invalid API token"}}""", "INVALID_TOKEN")]
    [InlineData(404, """{"detail":"Not Found"}""", "NOT_AVAILABLE")]
    [InlineData(422, """{"detail":[{"loc":["body","email"],"msg":"field required"}]}""", "VALIDATION_ERROR")]
    [InlineData(503, "<html>down</html>", "SERVER_ERROR")]
    public void Errors_parse_from_every_envelope(int status, string body, string code)
    {
        var result = HearthpulseApi.Parse(status, body);

        Assert.False(result.Ok);
        Assert.Equal(code, result.Error!.Code);
    }

    [Fact]
    public void Success_unwraps_data()
    {
        var result = HearthpulseApi.Parse(200, """{"data":[1,2],"meta":{}}""");

        Assert.True(result.Ok);
        Assert.Equal(2, result.Data.GetArrayLength());
    }

    [Fact]
    public void Heroes_parse_from_the_battlegrounds_endpoint()
    {
        using var doc = JsonDocument.Parse("""
            [{"hero":"Lord Jaraxxus","dbfId":59807,"pick_rate":"55.07%","avg_placement":3.702,"tier":"S","id":"TB_BaconShop_HERO_37",
              "pick_rate_value":55.07,"placement_distribution":["22.00%","18.23%","13.63%","11.11%","9.30%","8.56%","8.80%","8.37%"]},
             {"hero":"Broken","id":"X","avg_placement":null}]
            """);

        var heroes = HearthpulseData.ParseHeroes(doc.RootElement);

        var hero = Assert.Single(heroes.Values);
        Assert.Equal(3.702, hero.AveragePosition, 3);
        Assert.Equal(0.5507, hero.PickRate, 4);
        Assert.Equal(0.6497, hero.Top4Rate, 4);
        Assert.Equal(0.22, hero.FirstRate, 4);
        Assert.Equal(0, hero.DataPoints);
    }

    [Fact]
    public void Minions_and_archetypes_parse()
    {
        using var minions = JsonDocument.Parse("""
            [{"card_id":"BG35_883","tavern_tier":6,"normal":{"impact":-0.4,"avg_placement_with":3.9,"games_with_minion":12034},"golden":{}}]
            """);
        using var archetypes = JsonDocument.Parse("""
            {"total":1,"archetypes":[{"archetype_id":56,"name":"Quest Priest","slug":"quest-priest","player_class":"PRIEST",
              "total_games":19342,"win_rate":53.59,"pct_of_total":11.04}]}
            """);

        var card = HearthpulseData.ParseMinions(minions.RootElement)["BG35_883"];
        var archetype = Assert.Single(HearthpulseData.ParseArchetypes(archetypes.RootElement));

        Assert.Equal(3.9, card.AveragePlacement);
        Assert.Equal(12034, card.DataPoints);
        Assert.Equal("PRIEST", archetype.Class);
        Assert.Equal(0.5359, archetype.WinRate, 4);
        Assert.Equal(10365, archetype.Wins);
        Assert.Equal(0.1104, archetype.Share, 4);
    }

    [Theory]
    [InlineData("legend", "LEGEND")]
    [InlineData("top-500-legend", "LEGEND")]
    [InlineData("diamond", "DIAMOND_THROUGH_LEGEND")]
    [InlineData("all", "ALL")]
    public void Ranks_map_to_hsreplay_ranges(string rank, string expected) =>
        Assert.Equal(expected, HearthpulseData.RankRange(rank));

    [Fact]
    public async Task An_answer_in_an_unknown_charset_is_a_failed_request_not_an_exception()
    {
        // A captive portal or a proxy answering with a charset .NET does not know: reading the text throws.
        var api = new HearthpulseApi(new HttpClient(new Portal()), "https://api.test/");

        var result = await api.GetAsync("api/v1/meta");

        Assert.False(result.Ok);
    }

    private sealed class Portal : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent("<html>log in</html>"u8.ToArray());
            content.Headers.TryAddWithoutValidation("Content-Type", "text/html; charset=no-such-charset");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}
