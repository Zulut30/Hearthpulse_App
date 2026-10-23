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

    private static BgGameDetails Details() => new(
        14,
        6,
        2,
        "BG_ANOMALY",
        ["MURLOC", "BEAST", "DEMON"],
        [
            new BgMinion("BEAST_A", "A", 40, 30, true, 4, []),
            new BgMinion("BEAST_B", "B", 10, 10, false, 3, []),
            new BgMinion("AMALGAM", "C", 5, 5, false, 2, []),
            new BgMinion("BEAST_C", "D", 7, 7, false, 5, []),
            new BgMinion("SPELL", "E", 0, 0, false, 1, []) { CardType = "BATTLEGROUND_SPELL" },
        ],
        ["TRK_1", "TRK_2"],
        ["TRK_2", "TRK_9"],
        [new TierStep(1, 1), new TierStep(3, 2), new TierStep(3, 2), new TierStep(6, 3)],
        [
            new CombatRecord(2, 5, "BG_HERO_OTHER", "Win", 0, 3, new Hearthpulse.Core.Battlegrounds.CombatOdds(0.6, 0.1, 0.3, 0)),
            new CombatRecord(3, 6, "BG_HERO_OTHER", "Tie", 0, 0),
        ])
    {
        Choices =
        [
            new BgChoice(3, "discover", "TB_BaconShop_Triples_01", ["A", "B", "C"], ["B"]),
            new BgChoice(9, "trinket", null, ["TRK_1", "TRK_2"], []),
        ],
        Plays = [new BgPlay(1, "BEAST_B"), new BgPlay(2, "SPELL")],
    };

    private static IReadOnlyList<string> Races(string id) => id switch
    {
        "AMALGAM" => ["ALL"],
        "BEAST_A" or "BEAST_B" or "BEAST_C" => ["BEAST"],
        _ => [],
    };

    [Fact]
    public void SendsDetailsOnlyWhenAgreedTo()
    {
        var outcome = new GameOutcome("Battlegrounds", "Win", 1, 7000, null, "BG_HERO_1") { Details = Details() };

        Assert.Null(PoolGame.From(outcome, Base, Races, details: false)!.Details);
        var d = PoolGame.From(outcome, Base, Races, details: true)!.Details!;

        Assert.Equal(4, d.Board.Count);
        Assert.DoesNotContain(d.Board, m => m.Card == "SPELL");
        Assert.Equal("BEAST", d.Comp);
        Assert.Equal(["BEAST", "DEMON", "MURLOC"], d.Tribes);
        Assert.Equal([2, 3], d.TierUps.Select(t => t.Tier));
        Assert.Equal(["W", "T"], d.Combats.Select(c => c.Result));
        Assert.Equal(0.6, d.Combats[0].Win);
        Assert.Null(d.Combats[1].Win);
        Assert.Equal(["TRK_1", "TRK_2", "TRK_9"], d.TrinketsOffered);
        Assert.Equal(["TRK_2", "TRK_9"], d.Trinkets);
    }

    [Fact]
    public void NeverSendsOtherPlayers()
    {
        var game = PoolGame.From(new GameOutcome("Battlegrounds", "Win", 1, 7000, null, "BG_HERO_1") { Details = Details() }, Base, Races, details: true)!;
        var json = JsonSerializer.Serialize(PoolClient.Body([game]), JsonSerializerOptions.Web);

        Assert.StartsWith("{\"v\":3,\"games\":[{", json);
        Assert.Contains("\"tierups\":[[3,2],[6,3]]", json);
        Assert.Contains("\"combats\":[[2,\"W\",0,3,0.6],[3,\"T\",0,0,null]]", json);
        Assert.Contains("\"choices\":[[3,\"discover\",\"TB_BaconShop_Triples_01\",[\"A\",\"B\",\"C\"],[\"B\"]],[9,\"trinket\",null,[\"TRK_1\",\"TRK_2\"],[]]]", json);
        Assert.Contains("\"plays\":[[1,\"BEAST_B\"],[2,\"SPELL\"]]", json);
        Assert.DoesNotContain("BG_HERO_OTHER", json);
        Assert.DoesNotContain("\"turns\"", JsonSerializer.Serialize(PoolClient.Body([game with { Details = null }]), JsonSerializerOptions.Web));
    }

    [Fact]
    public void PicksAndPlaysNeedTheNewestConsent()
    {
        var game = PoolGame.From(new GameOutcome("Battlegrounds", "Win", 1, 7000, null, "BG_HERO_1") { Details = Details() }, Base, Races, details: true)!;
        var older = new Hearthpulse.Core.AppSettings { PoolConsent = true, PoolConsentVersion = 2, PoolEndpoint = "https://pool.example" };
        var newest = new Hearthpulse.Core.AppSettings { PoolConsent = true, PoolConsentVersion = PoolClient.ConsentVersion, PoolEndpoint = "https://pool.example" };

        Assert.Empty(PoolClient.Limit(game, older).Details!.Choices);
        Assert.Empty(PoolClient.Limit(game, older).Details!.Plays);
        Assert.Equal(2, PoolClient.Limit(game, newest).Details!.Choices.Count);
    }

    [Fact]
    public void TheSameGameKeepsItsIdButRevealsNothing()
    {
        var settings = new Hearthpulse.Core.AppSettings();
        var secret = PoolClient.Secret(settings);
        var outcome = new GameOutcome("Battlegrounds", "Win", 2, 7000, null, "BG_HERO_1") { Key = "20261023120000|Player#1234|GT_BATTLEGROUNDS" };

        Assert.Equal(64, secret.Length);
        Assert.Same(secret, PoolClient.Secret(settings));
        var id = PoolGame.From(outcome, Base, secret: secret)!.GameId;
        Assert.Equal(id, PoolGame.From(outcome, Base, secret: secret)!.GameId);
        Assert.NotEqual(id, PoolGame.From(outcome, Base, secret: PoolClient.Secret(new Hearthpulse.Core.AppSettings()))!.GameId);
        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.DoesNotContain("1234", id);
    }

    [Fact]
    public void EarlierGamesComeFromHistory()
    {
        var record = new Hearthpulse.Core.Data.GameRecord
        {
            Key = "20261020120000|Player#1234|GT_BATTLEGROUNDS",
            StartedAt = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc),
            Mode = "Battlegrounds",
            HeroCardId = "BG_HERO_1_SKIN",
            Placement = 3,
            RatingBefore = 6789,
            Turns = 13,
            TavernTier = 5,
            Races = "BEAST,DEMON,MURLOC",
            FinalBoard = "[{\"id\":\"BEAST_A\",\"atk\":20,\"hp\":10,\"gold\":true},{\"id\":\"BEAST_B\",\"atk\":3,\"hp\":4,\"gold\":false}]",
        };

        var game = PoolGame.FromHistory(record, "s3cret", Base, Races)!;

        Assert.Equal("history", game.Source);
        Assert.Equal((int)(record.StartedAt - DateTime.UnixEpoch).TotalDays, game.Day);
        Assert.Equal("BG_HERO_1", game.Picked);
        Assert.Equal(6500, game.Bracket);
        Assert.Equal(2, game.Details!.Board.Count);
        Assert.True(game.Details.Board[0].Golden);
        Assert.Equal("MIXED", game.Details.Comp);
        Assert.Equal(["BEAST", "DEMON", "MURLOC"], game.Details.Tribes);
        Assert.Null(PoolGame.FromHistory(record with { Mode = "Ranked" }, "s3cret", Base));
        Assert.Null(PoolGame.FromHistory(record with { TavernTier = 0 }, "s3cret", Base)!.Details);
    }

    [Fact]
    public void MixedBoardsHaveNoMainTribe()
    {
        var board = new[] { new PoolMinion("BEAST_A", false, 1, 1), new PoolMinion("X", false, 1, 1), new PoolMinion("Y", false, 1, 1) };
        Assert.Equal("MIXED", PoolDetails.MainTribe(board, Races));
        Assert.Null(PoolDetails.MainTribe([], Races));
    }

    [Fact]
    public void SharesNothingUntilAPoolIsSetUp()
    {
        // Hearthpulse ships without a pool address: consent alone sends nothing anywhere.
        var settings = new Hearthpulse.Core.AppSettings { PoolConsent = true, PoolConsentVersion = PoolClient.ConsentVersion };
        Assert.False(PoolClient.Available(settings));
        Assert.False(PoolClient.Sharing(settings));
    }

    [Fact]
    public void AsksAgainWhenTheListGrows()
    {
        var settings = new Hearthpulse.Core.AppSettings { PoolConsent = true, PoolConsentVersion = 1, PoolEndpoint = "https://pool.example" };
        Assert.True(PoolClient.Sharing(settings));
        Assert.False(PoolClient.SharingDetails(settings));
        settings.PoolConsentVersion = 2;
        Assert.True(PoolClient.SharingDetails(settings));
        Assert.False(PoolClient.SharingAll(settings));
        settings.PoolConsentVersion = PoolClient.ConsentVersion;
        Assert.True(PoolClient.SharingAll(settings));
        settings.PoolConsent = false;
        Assert.False(PoolClient.Sharing(settings));
    }

    [Fact]
    public void ReadsCardsTrinketsAndTribes()
    {
        using var doc = JsonDocument.Parse("""
            { "heroes": [],
              "cards": [ { "card": "BG_CARD_A", "games": 250, "avgPlacement": 3.2 } ],
              "trinkets": [ { "card": "TRK_2", "offered": 100, "picked": 60, "avgPlacement": 3.9 } ],
              "comps": [ { "comp": "BEAST", "games": 40, "avgPlacement": 3.5, "top": 0.6, "first": 0.2 },
                         { "comp": "MIXED", "games": 5, "avgPlacement": 5.5, "top": 0.2, "first": 0 } ] }
            """);
        var meta = PoolClient.Parse(doc.RootElement)!;

        Assert.Equal(250, meta.Card("BG_CARD_A")!.DataPoints);
        Assert.Equal((3.9, 0.6, 60), meta.Trinket("TRK_2", 100));
        var tribe = Assert.Single(meta.Tribes);
        Assert.Equal("BEAST", tribe.Tribe);
        Assert.Equal(0.6, tribe.Top4Rate);
    }
}