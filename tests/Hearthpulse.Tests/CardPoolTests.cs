using System.IO.Compression;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Card pools of Discover/random generators. The fixture is the real card catalog of 2026-10-23
/// (Hearthpulse API constructed-cards, Wild with formats, HearthstoneJSON minion types; English text kept for Standard
/// cards only). Expected pool sizes were counted by hand from the same API data with a separate script, one filter per
/// card written from its text.
/// </summary>
public sealed class CardPoolTests
{
    private static readonly Lazy<CardPoolCatalog> Fixture = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card-pool.json.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return CardPoolCatalog.Read(gz)!;
    });

    private static CardPoolEngine Engine() => new(Fixture.Value, CardDb.Empty);

    private static PoolContext Standard(string ownClass) => new("FT_STANDARD", ownClass, "WARRIOR");

    [Theory]
    [InlineData("CORE_GIL_836", "SHAMAN", 156)] // Blazing Invocation: Discover a Battlecry minion
    [InlineData("CORE_ONY_022", "PALADIN", 25)] // Battle Vicar: Discover a Holy spell
    [InlineData("CORE_KAR_057", "PALADIN", 48)] // Ivory Knight: Discover a spell
    [InlineData("CORE_EDR_001", "MAGE", 52)] // Babbling Bookcase: Add 2 random Mage spells
    [InlineData("CORE_DRG_024", "WARRIOR", 16)] // Sky Raider: Add a random Pirate (any class)
    [InlineData("DINO_422", "HUNTER", 28)] // Ankylodon: two random 3-Cost Beasts
    [InlineData("DINO_433", "WARRIOR", 44)] // Guard Duty: a random 6, 4, and 2-Cost Taunt minion
    [InlineData("CATA_484", "MAGE", 83)] // Winterspring Whelp: a 1-Cost spell from any class
    [InlineData("CORE_TID_931", "ROGUE", 85)] // Jackpot!: spells from other classes that cost (5) or more
    [InlineData("CORE_KAR_069", "ROGUE", 936)] // Swashburglar: a card from another class
    [InlineData("TIME_711", "ROGUE", 282)] // Flashback: 1-Cost minions from the past (Wild-only sets)
    [InlineData("DINO_430", "HUNTER", 31)] // Beast Speaker Taka: a Legendary Beast from any class (dual types count)
    [InlineData("CATA_556", "MAGE", 21)] // Carrier Whelp: a Dragon that costs (3) or less
    [InlineData("JAIL_806", "SHAMAN", 90)] // Hexmarshal: a spell that costs (5) or more
    [InlineData("TLC_824", "HUNTER", 41)] // Odd Map: an odd-Attack Beast
    [InlineData("FIR_920", "ROGUE", 173)] // Smoke Bomb: a Combo, Battlecry, or Stealth minion with a Dark Gift
    [InlineData("EDR_273", "DRUID", 10)] // Symbiosis: a Choose One card from another class
    [InlineData("CORE_EX1_189", "MAGE", 196)] // Brightwing: a random Legendary minion
    [InlineData("JAIL_321", "MAGE", 6)] // Tricksy Improviser: two random Mage Secrets
    [InlineData("TLC_814", "PRIEST", 114)] // Twilight Mender: a random Holy and Shadow spell
    [InlineData("DINO_412", "SHAMAN", 61)] // Tortotem: a minion with multiple minion types
    [InlineData("TIME_446", "DEMONHUNTER", 17)] // The Eternal Hold: any Demon that costs (5) or more
    [InlineData("CORE_LOE_039", "MAGE", 24)] // Gorillabot A-3 (neutral) played by a Mage: Discover a Mech
    [InlineData("TLC_334", "MAGE", 17)] // Relic of Kings: a spell from any class that costs (8) or more
    [InlineData("JAIL_878", "HUNTER", 16)] // Guard Dog: a random 1-Cost Deathrattle minion
    [InlineData("TIME_016", "PALADIN", 27)] // Neon Innovation: a Paladin Mech from the past
    public void Real_standard_generators_have_the_pools_counted_by_hand(string cardId, string ownClass, int expected)
    {
        var pool = Engine().Pool(cardId, Standard(ownClass));

        Assert.NotNull(pool);
        Assert.Equal(expected, pool.Count);
    }

    [Fact]
    public void Every_standard_card_is_read_or_explained()
    {
        var engine = Engine();
        var standard = Fixture.Value.Cards.Where(c => c.Standard).ToList();
        var parsed = standard.Select(c => (Card: c, Parse: engine.Parse(c.Id))).ToList();

        Assert.Equal(1322, standard.Count);
        Assert.Empty(parsed.Where(p => p.Parse.Status == PoolParseStatus.Unrecognized).Select(p => $"{p.Card.Id} {p.Parse.Detail}"));
        Assert.True(parsed.Count(p => p.Parse.Status == PoolParseStatus.Pool) >= 170);
    }

    [Fact]
    public void Format_and_classes_change_the_pool()
    {
        var engine = Engine();

        Assert.Equal(4273, engine.Pool("CORE_KAR_069", new PoolContext("FT_WILD", "ROGUE", null))!.Count);
        Assert.Null(engine.Pool("CORE_KAR_069", new PoolContext("FT_TWIST", "ROGUE", null)));
        // Discover: your class and neutral. A Mage casting Blazing Invocation sees Mage cards, not Shaman ones.
        var mage = engine.Pool("CORE_GIL_836", Standard("MAGE"))!;
        Assert.Equal(156, mage.Count);
        Assert.All(mage, c => Assert.True(c.IsNeutral || c.HasClass("MAGE")));
        Assert.DoesNotContain(mage, c => c.HasClass("SHAMAN") && !c.HasClass("MAGE"));
    }

    [Theory]
    [InlineData("Deal 2 damage to a random enemy minion.")]
    [InlineData("Draw a card.")]
    [InlineData("After you Discover a card, reduce its Cost by (1).")]
    [InlineData("Quest: Discover 8 cards. Reward: The Origin Stone.")]
    [InlineData("Deathrattle: Give this minion's Attack to a random friendly minion.")]
    [InlineData("Each minion gets shuffled into a random player's deck.")]
    public void Texts_without_card_generation_are_not_generators(string text) =>
        Assert.Equal(PoolParseStatus.NotGenerator, PoolTextParser.Parse(text).Status);

    [Theory]
    [InlineData("Discover a card from your deck.")]
    [InlineData("Battlecry: Summon a random Demon from your hand and deck.")]
    [InlineData("Discover a copy of a card in your opponent's hand.")]
    [InlineData("Discover a friendly Dragon that died this game. Resummon it.")]
    [InlineData("Deathrattle: Get a copy of a random different Deathrattle minion in your hand.")]
    public void Generators_outside_the_catalog_are_not_pools(string text) =>
        Assert.Equal(PoolParseStatus.NotPool, PoolTextParser.Parse(text).Status);

    [Theory]
    [InlineData("Battlecry and Deathrattle: Summon a random Dormant Dreadseed.")]
    [InlineData("Battlecry: Summon a random minion with Cost equal to your hand size.")]
    [InlineData("Destroy a minion. Summon a random minion of the same Cost to replace it.")]
    public void Unknown_words_leave_the_pool_unrecognized(string text)
    {
        var parse = PoolTextParser.Parse(text);

        Assert.Equal(PoolParseStatus.Unrecognized, parse.Status);
        Assert.Null(parse.Generator);
        Assert.False(string.IsNullOrEmpty(parse.Detail));
    }

    [Fact]
    public void The_parser_reads_counts_costs_keywords_schools_and_class_clauses()
    {
        var guard = PoolTextParser.Parse("Summon a random 6, 4, and 2-Cost Taunt minion.").Generator!;
        Assert.Equal(PoolVerb.Random, guard.Verb);
        Assert.Equal([2, 4, 6], guard.Query.Costs.Order());
        Assert.Equal(["TAUNT"], guard.Query.Mechanics.Single());
        Assert.Equal(["MINION"], guard.Query.Types);

        var jackpot = PoolTextParser.Parse("Add two random spells from other classes that cost (5) or more to your hand.").Generator!;
        Assert.Equal(2, jackpot.Count);
        Assert.Equal(ClassScope.Other, jackpot.Query.Scope);
        Assert.Equal(5, jackpot.Query.MinCost);
        Assert.Equal("Add two random spells from other classes that cost (5) or more", jackpot.Phrase);

        var smoke = PoolTextParser.Parse("Discover a Combo, Battlecry, or Stealth minion with a Dark Gift.").Generator!;
        Assert.Equal(PoolVerb.Discover, smoke.Verb);
        Assert.Equal(["BATTLECRY", "COMBO", "STEALTH"], smoke.Query.Mechanics.Single().Order());

        Assert.Equal(["HOLY", "SHADOW"], PoolTextParser.Parse("Deathrattle: Get a random Holy and Shadow spell.").Generator!.Query.Schools.Order());
        Assert.True(PoolTextParser.Parse("Summon two random 1-Cost minions from the past.").Generator!.Query.Past);
        Assert.Equal(ClassScope.Any, PoolTextParser.Parse("Rewind Discover a spell from ANY class.").Generator!.Query.Scope);
        var map = PoolTextParser.Parse("Discover an odd-Attack Beast. If you play it this turn, also pick one of the others.").Generator!.Query;
        Assert.Equal(("odd", "Attack"), (map.Parity, map.ParityStat));
        Assert.Equal(["BEAST"], map.Races);
        Assert.Equal(["MAGE"], PoolTextParser.Parse("Battlecry: Add 2 random Mage spells to your hand.").Generator!.Query.Classes);
    }

    [Fact]
    public void Histograms_bucket_the_tail_and_mark_the_median()
    {
        var odd = PoolStats.Histogram("Cost", [1, 2, 3, 3, 9], 0, 7);
        Assert.Equal(3, odd.Median);
        Assert.Equal("7+", odd.Bars[^1].Label);
        Assert.Equal(1, odd.Bars[^1].Count);
        Assert.Equal("3", odd.Bars.Single(b => b.IsMedian).Label);
        Assert.Equal(1.0, odd.Bars[3].Height);

        var even = PoolStats.Histogram("Health", [2, 3], 1, 10);
        Assert.Equal("2.5", even.MedianText);
        Assert.Equal("2", even.Bars.Single(b => b.IsMedian).Label);
        Assert.Equal("10+", even.Bars[^1].Label);
    }

    [Fact]
    public void Text_rules_find_card_draw_face_damage_and_healing()
    {
        Assert.True(PoolStats.DrawsCards("Battlecry: Draw a card."));
        Assert.True(PoolStats.DrawsCards("Your opponent draws 2 cards."));
        Assert.False(PoolStats.DrawsCards("Casts When Drawn. Summon a 1/1."));
        Assert.True(PoolStats.DealsFaceDamage("Deal 3 damage."));
        Assert.True(PoolStats.DealsFaceDamage("Deal 2 damage to all enemies."));
        Assert.True(PoolStats.DealsFaceDamage("Deal 6 damage randomly split among all enemies."));
        Assert.False(PoolStats.DealsFaceDamage("Deal 3 damage to an enemy minion."));
        Assert.False(PoolStats.DealsFaceDamage("Deal 2 damage to all minions."));
        Assert.True(PoolStats.Heals("Restore 5 Health to your hero."));
        Assert.False(PoolStats.Heals("Gain 5 Armor."));
    }

    [Fact]
    public void The_view_leaves_out_the_filter_mechanic_and_highlights_the_most_played_card()
    {
        var meta = new Dictionary<string, int> { ["BE_014"] = 900, ["CORE_EX1_284"] = 50 };
        var view = new CardPoolEngine(Fixture.Value, CardDb.Empty, meta).Build("CORE_GIL_836", Standard("SHAMAN"), PoolTrigger.Choice)!;

        Assert.Equal(156, view.Count);
        Assert.DoesNotContain(view.Mechanics, m => m.Tag == "BATTLECRY");
        Assert.Equal(view.Mechanics.OrderByDescending(m => m.Count).Select(m => m.Count), view.Mechanics.Select(m => m.Count));
        Assert.InRange(view.Mechanics.Count, 1, PoolStats.MaxMechanics);
        Assert.Equal("BE_014", view.Featured!.CardId);
        Assert.Equal("Discover a Battlecry minion", view.Phrase);
        Assert.Equal("Shaman + Neutral · Standard", view.Description);
        Assert.Equal(156, view.Cost.Count);
        Assert.Equal(view.Cards.OrderBy(c => c.Cost).Select(c => c.Cost), view.Cards.Select(c => c.Cost));
        Assert.Null(new CardPoolEngine(Fixture.Value, CardDb.Empty).Build("EDR_841", Standard("WARLOCK"), PoolTrigger.Played));

        // The browser's filter chips: every mechanic some but not all cards have; each card knows its own.
        Assert.True(view.Filters.Count >= view.Mechanics.Count);
        Assert.All(view.Filters, f => Assert.InRange(f.Count, 1, view.Count - 1));
        var taunt = view.Filters.Single(f => f.Tag == "TAUNT");
        Assert.Equal(taunt.Count, view.Cards.Count(c => c.Mechanics.Contains("TAUNT")));
        Assert.Equal(
            view.Cards.Count(c => c.Mechanics.Contains("TAUNT") && c.Mechanics.Contains(PoolStats.CardDrawTag)),
            Fixture.Value.Cards.Count(c => view.Cards.Any(e => e.CardId == c.Id) && PoolStats.Has(c, "TAUNT") && PoolStats.DrawsCards(c.Text)));
    }

    [Fact]
    public void The_same_pool_comes_back_as_the_same_view()
    {
        var engine = Engine();
        var first = engine.Build("JAIL_706", Standard("ROGUE"), PoolTrigger.Choice)!;

        Assert.Same(first, engine.Build("JAIL_706", Standard("ROGUE"), PoolTrigger.Choice));
        var requested = engine.Build("JAIL_706", Standard("ROGUE"), PoolTrigger.Request)!;
        Assert.Equal(PoolTrigger.Request, requested.Trigger);
        Assert.Same(first.Cards, requested.Cards);
        Assert.Equal(51, first.Count);
    }

    [Fact]
    public void The_tracker_follows_the_play_the_choice_and_requests()
    {
        var cards = CardDb.Parse("enUS", new MemoryStream("""
            [
              {"id":"HERO_08","dbfId":637,"name":"Jaina","type":"HERO","cardClass":"MAGE"},
              {"id":"HERO_01","dbfId":7,"name":"Garrosh","type":"HERO","cardClass":"WARRIOR"}
            ]
            """u8.ToArray()));
        var tracker = new CardPoolTracker { Engine = new CardPoolEngine(Fixture.Value, cards) };
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        parser.AddListener(tracker);
        string[] lines =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=5",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=21",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=20 CardID=HERO_08",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=21 CardID=HERO_01",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=50 CardID=CORE_GIL_836",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
            "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=X id=50 zone=HAND zonePos=1 cardId=CORE_GIL_836 player=1] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
            "DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id=50 zone=HAND zonePos=1 cardId=CORE_GIL_836 player=1] tag=ZONE value=PLAY ",
            "DebugPrintPower() - BLOCK_END",
        ];
        foreach (var line in lines)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        var game = parser.Game!;
        var logTime = game.LastActivity;

        var played = tracker.BuildView(game, cards, logTime.AddSeconds(1))!;
        Assert.Equal(PoolTrigger.Played, played.Trigger);
        Assert.Equal(156, played.Count);
        Assert.Equal("Mage + Neutral · Standard", played.Description);
        Assert.Null(tracker.BuildView(game, cards, logTime + CardPoolTracker.PlayedFor + TimeSpan.FromSeconds(1)));

        foreach (var line in new[]
                 {
                     "DebugPrintEntityChoices() - id=5 Player=velox#21276 TaskList=570 ChoiceType=GENERAL CountMin=1 CountMax=1",
                     "DebugPrintEntityChoices() -   Source=[entityName=X id=50 zone=PLAY zonePos=0 cardId=CORE_GIL_836 player=1]",
                     "DebugPrintEntityChoices() -   Entities[0]=[entityName=Y id=60 zone=SETASIDE zonePos=0 cardId=BE_014 player=1]",
                 })
        {
            parser.Feed("D 12:30:01.0000000 GameState." + line);
        }

        parser.Flush();
        var choice = tracker.BuildView(game, cards, logTime.AddMinutes(1))!;
        Assert.Equal(PoolTrigger.Choice, choice.Trigger);
        Assert.Contains(choice.Cards, c => c.CardId == "BE_014");

        tracker.Request("CORE_KAR_069");
        Assert.Equal(PoolTrigger.Choice, tracker.BuildView(game, cards, logTime.AddMinutes(1))!.Trigger);
        tracker.OnChoiceMade(game);
        var requested = tracker.BuildView(game, cards, logTime.AddMinutes(1))!;
        Assert.Equal(PoolTrigger.Request, requested.Trigger);
        Assert.Equal("CORE_KAR_069", requested.SourceCardId);
        tracker.Request(null);
        Assert.Null(tracker.BuildView(game, cards, logTime.AddMinutes(1)));
    }
}
