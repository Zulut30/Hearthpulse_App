using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// The Arena mulligan fallback (<see cref="MulliganArena"/>): an Arena deck has no mulligan stats of its own, so the
/// Arena cards' win rates when drawn speak, against the class's average card; the call is against the average card left
/// in the deck. Synthetic Power.log lines and card numbers; no network.
/// </summary>
public sealed class MulliganArenaTests
{
    [Fact]
    public void Drawn_and_played_win_rates_parse_from_the_real_answer()
    {
        var card = HearthpulseData.ParseArenaCards(Fixture()).Cards.Single(c => c.CardId == "BE_090");

        Assert.Equal(0.645, card.DrawnWinRate!.Value, 4);
        Assert.Equal(0.689, card.PlayedWinRate!.Value, 4);
    }

    [Fact]
    public void The_cache_keeps_the_drawn_and_played_win_rates()
    {
        var small = JsonSerializer.SerializeToElement(HearthpulseData.ShrinkArenaCards(Fixture()));

        var card = HearthpulseData.ParseArenaCards(small).Cards.Single(c => c.CardId == "BE_090");

        Assert.Equal(0.645, card.DrawnWinRate!.Value, 4);
        Assert.Equal(0.689, card.PlayedWinRate!.Value, 4);
    }

    [Fact]
    public void An_arena_deck_without_mulligan_stats_gets_the_arena_cards_win_rates()
    {
        var view = Mulligan.Build(Game("GT_ARENA"), Cards, Deck, null, "WARLOCK", arena: Meta())!;

        Assert.Null(view.NoStats);
        Assert.Equal(ArenaCardBasis.Drawn, view.ArenaBasis);
        Assert.Equal(MulliganArena.SourceOf(ArenaCardBasis.Drawn), view.Source);
        // Warlock and neutral cards, 1000 games each: (0.60 + 0.56 + 0.48 + 0.52) / 4; the Mage card is left out.
        Assert.Equal(0.54, view.CardBaseline, 6);
        Assert.Equal(["BE_090", "BE_085", "BE_193"], view.Cards.Select(c => c.CardId));
        Assert.Equal([0.60, 0.56, 0.48], view.Cards.Select(c => c.WinRate!.Value));
        Assert.All(view.Cards, c => Assert.Null(c.KeepRate));
        // The deck after the hand: one more BE_085 (+2) and two TIME_063 (−2 each) → a replacement is worth −0.67.
        Assert.Equal(-0.02 / 3, view.ReplaceDelta, 6);
        Assert.Equal([MulliganCall.Keep, MulliganCall.Keep, MulliganCall.Mulligan], view.Cards.Select(c => c.Call));
        Assert.Equal(0.06 + 0.02 / 3, view.Cards[0].Edge!.Value, 6);
        Assert.Equal(["BE_090", "BE_085"], view.Recommended.Select(id => view.Cards.Single(c => c.EntityId == id).CardId));
        // The draft tier letter as on the draft screen.
        Assert.Equal(("S", 55.0), (view.Cards[0].PickTier, view.Cards[0].PickScore));
        Assert.Equal("D", view.Cards[2].PickTier);
        // The data has no split by the coin: the strip does not claim one.
        Assert.Null(view.GoingFirst);
    }

    [Fact]
    public void An_arena_game_ignores_constructed_numbers()
    {
        var view = Mulligan.Build(Game("GT_ARENA"), Cards, Deck, null, "WARLOCK", ConstructedGuide(), Meta())!;

        Assert.Equal(ArenaCardBasis.Drawn, view.ArenaBasis);
        Assert.Equal(0.60, view.Cards[0].WinRate!.Value, 6);
    }

    [Fact]
    public void A_constructed_deck_keeps_its_own_mulligan_stats_first()
    {
        var view = Mulligan.Build(Game("GT_RANKED"), Cards, Deck, null, "WARLOCK", ConstructedGuide(), Meta())!;

        Assert.Null(view.ArenaBasis);
        Assert.Equal("Guide", view.Source);
        Assert.Equal(0.40, view.Cards[0].WinRate!.Value, 6);
    }

    [Fact]
    public void Without_drawn_win_rates_the_deck_win_rate_speaks()
    {
        // An older cache: only the deck win rate per card.
        var meta = new ArenaMeta(Meta().Cards.Select(c => c with { DrawnWinRate = null, PlayedWinRate = null }), []);

        var view = Mulligan.Build(Game("GT_UNDERGROUND_ARENA"), Cards, Deck, null, "WARLOCK", arena: meta)!;

        Assert.Equal(ArenaCardBasis.Deck, view.ArenaBasis);
        Assert.Equal(MulliganArena.SourceOf(ArenaCardBasis.Deck), view.Source);
        Assert.Equal(0.51, view.Cards[0].WinRate!.Value, 6);
    }

    [Fact]
    public void No_data_is_said_only_when_there_is_none()
    {
        var notLoaded = Mulligan.Build(Game("GT_ARENA"), Cards, Deck, null, "WARLOCK")!;
        var otherClass = Mulligan.Build(Game("GT_ARENA"), Cards, Deck, null, "PRIEST", arena: new ArenaMeta(Meta().Cards.Where(c => c.CardId == "CS2_MAGE"), []))!;

        Assert.NotNull(notLoaded.NoStats);
        Assert.NotNull(otherClass.NoStats);
        Assert.NotEqual(notLoaded.NoStats, otherClass.NoStats);
        Assert.All(notLoaded.Cards, c => Assert.Equal(MulliganCall.NoData, c.Call));
    }

    /// <summary>
    /// Drawn win rates 0.60, 0.56, 0.48, 0.52 for the Warlock deck's cards and 0.70 for a Mage card; the deck win rates
    /// are lower so the basis shows in the numbers.
    /// </summary>
    private static ArenaMeta Meta() => new(
    [
        Score("BE_090", 129824, 55, 0.60, 0.51),
        Score("BE_085", 129793, 40, 0.56, 0.50),
        Score("BE_193", 129901, 12, 0.48, 0.47),
        Score("TIME_063", 120001, 30, 0.52, 0.49),
        Score("CS2_MAGE", 120003, 50, 0.70, 0.60),
    ], [], underground: false);

    private static ArenaCardScore Score(string id, int dbf, double score, double drawn, double deck) =>
        new(id, dbf, score, deck, 0.5, 1000) { DrawnWinRate = drawn, PlayedWinRate = drawn + 0.01 };

    /// <summary>A Constructed guide with its own (low) numbers for the same cards.</summary>
    private static GuideArchetype ConstructedGuide() => new(1, "Guide", "WARLOCK",
        Deck.Cards.Select(c => new GuideCard(Cards.Get(c.Key)!.Id, c.Key, Cards.Get(c.Key)!.Cost, 500, 0.5, 500, 0.40, null)).ToList(), [], "Guide");

    private static readonly DeckDefinition Deck = new("Arena", 0, string.Empty, "wild", [893],
        new Dictionary<int, int> { [129824] = 1, [129793] = 2, [129901] = 1, [120001] = 2 });

    /// <summary>The local player's mulligan with BE_090, BE_085 and BE_193 offered, going first.</summary>
    private static GameState Game(string gameType)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 28));
        var lines = new List<string>
        {
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=FIRST_PLAYER value=1",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
        };
        var id = 4;
        foreach (var card in new[] { "BE_090", "BE_085", "BE_193" })
        {
            lines.Add($"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={card}");
            lines.Add("DebugPrintPower() -     tag=ZONE value=HAND");
            lines.Add("DebugPrintPower() -     tag=CONTROLLER value=1");
            lines.Add($"DebugPrintPower() -     tag=ZONE_POSITION value={id - 3}");
            id++;
        }

        lines.Add($"DebugPrintGame() - GameType={gameType}");
        lines.Add("DebugPrintGame() - FormatType=FT_WILD");
        lines.Add("DebugPrintGame() - PlayerID=1, PlayerName=velox#21276");
        lines.Add("DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER");
        lines.Add("DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=MULLIGAN_STATE value=INPUT ");
        foreach (var line in lines)
        {
            parser.Feed("D 16:44:59.2148622 GameState." + line);
        }

        parser.Flush();
        return parser.Game!;
    }

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
        [
          {"id":"BE_090","dbfId":129824,"name":"Big Warlock Card","cost":6,"type":"MINION","cardClass":"WARLOCK","collectible":true},
          {"id":"BE_085","dbfId":129793,"name":"Small Warlock Spell","cost":2,"type":"SPELL","cardClass":"WARLOCK","collectible":true},
          {"id":"BE_193","dbfId":129901,"name":"Neutral One","cost":1,"type":"MINION","cardClass":"NEUTRAL","collectible":true},
          {"id":"TIME_063","dbfId":120001,"name":"Weak Neutral","cost":3,"type":"MINION","cardClass":"NEUTRAL","collectible":true},
          {"id":"CS2_MAGE","dbfId":120003,"name":"Mage Card","cost":2,"type":"SPELL","cardClass":"MAGE","collectible":true},
          {"id":"GAME_005","dbfId":1746,"name":"The Coin","cost":0,"type":"SPELL","cardClass":"NEUTRAL"},
          {"id":"HERO_07","dbfId":893,"name":"Gul'dan","type":"HERO","cardClass":"WARLOCK","collectible":true}
        ]
        """)));

    private static JsonElement Fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Arena", "arena-cards.json")));
        return doc.RootElement.GetProperty("data").Clone();
    }
}
