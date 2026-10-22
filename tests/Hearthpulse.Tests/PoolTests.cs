using System.Text.Json;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

public sealed class PoolTests
{
    // Skins map to their base hero, like CardDb.BaseHeroId does.
    private static string Base(string id) => id.EndsWith("_SKIN", StringComparison.Ordinal) ? id[..^5] : id;

    [Fact]
    public void SharesOnlyWhatTheBannerPromises()
    {
        var outcome = new GameOutcome("Battlegrounds", "Win", 3, 6789, null, "BG_HERO_1_SKIN", ["BG_HERO_1", "BG_HERO_2", "BG_HERO_3", "BG_HERO_4"]);
        var game = PoolGame.From(outcome, Base)!;

        Assert.Equal("solo", game.Mode);
        Assert.Equal(6500, game.Bracket);
        Assert.Equal("BG_HERO_1", game.Picked);
        Assert.Equal(["BG_HERO_1", "BG_HERO_2", "BG_HERO_3", "BG_HERO_4"], game.Offered);
        Assert.Equal(3, game.Placement);
        Assert.Matches("^[0-9a-f]{32}$", game.GameId);
        Assert.NotEqual(game.GameId, PoolGame.From(outcome, Base)!.GameId);
    }

    [Fact]
    public void SkipsGamesThePoolCannotUse()
    {
        Assert.Null(PoolGame.From(new GameOutcome("Ranked", "Win", 0, 1000, null, "HERO_01"), Base));
        Assert.Null(PoolGame.From(new GameOutcome("Battlegrounds", "Loss", 0, 6000, null, "BG_HERO_1"), Base));
        Assert.Null(PoolGame.From(new GameOutcome("Battlegrounds", "Loss", 5, 6000, null, ""), Base));
    }

    [Fact]
    public void DropsOffersThatDoNotIncludeThePick()
    {
        var game = PoolGame.From(new GameOutcome("Duos", "Win", 2, null, null, "BG_HERO_9", ["BG_HERO_1", "BG_HERO_2"]), Base)!;

        Assert.Equal("duos", game.Mode);
        Assert.Equal(0, game.Bracket);
        Assert.Empty(game.Offered);
    }

    [Fact]
    public void ReadsHeroTotalsAndHidesSmallSamples()
    {
        using var doc = JsonDocument.Parse("""
            { "games": 140, "updated": "2026-10-23T10:00:00.000Z", "heroes": [
              { "hero": "BG_HERO_1", "games": 120, "avgPlacement": 3.9, "top": 0.55, "first": 0.15, "offered": 200, "picked": 100 },
              { "hero": "BG_HERO_2", "games": 19, "avgPlacement": 2.0, "top": 0.9, "first": 0.5, "offered": 30, "picked": 19 },
              { "hero": 5, "games": 500 } ] }
            """);
        var meta = PoolClient.Parse(doc.RootElement)!;

        var hero = Assert.Single(meta.HeroesByBracket[100]).Value;
        Assert.Equal("BG_HERO_1", hero.HeroCardId);
        Assert.Equal(120, hero.DataPoints);
        Assert.Equal(3.9, hero.AveragePosition);
        Assert.Equal(0.5, hero.PickRate);
        Assert.Equal(0.55, hero.Top4Rate);
        Assert.Equal(0.15, hero.FirstRate);
        Assert.Same(hero, meta.Hero("BG_HERO_1", 25));
        Assert.Empty(meta.Comps);
    }

    [Fact]
    public void IgnoresMalformedResponses()
    {
        using var doc = JsonDocument.Parse("""{ "heroes": "nope" }""");
        Assert.Null(PoolClient.Parse(doc.RootElement));
    }

    [Fact]
    public void StaysOffUntilAnEndpointIsSet()
    {
        Assert.Equal(PoolClient.DefaultEndpoint.Length > 0, PoolClient.Available(new Hearthpulse.Core.AppSettings()));
        Assert.True(PoolClient.Available(new Hearthpulse.Core.AppSettings { PoolEndpoint = "https://pool.example.workers.dev/" }));
        Assert.Equal("https://pool.example.workers.dev", PoolClient.Endpoint(new Hearthpulse.Core.AppSettings { PoolEndpoint = "https://pool.example.workers.dev/" }));
        Assert.False(PoolClient.Available(new Hearthpulse.Core.AppSettings { PoolEndpoint = "http://insecure.example" }));
    }
}