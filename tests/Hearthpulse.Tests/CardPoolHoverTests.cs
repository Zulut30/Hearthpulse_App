using System.IO.Compression;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using static Hearthpulse.Tests.EctoplasmListTests;

namespace Hearthpulse.Tests;

/// <summary>
/// The card under the mouse is read from the game only while a hover can show something: a card with a pool in your
/// hand (legal review L-m1: memory reads only when needed). The hovered card's pool on the way from the card to its
/// panel (TASK-2026-11-09-card-pool-related A1) and the pool engine's caches (B1), on synthetic card data and catalog.
/// </summary>
public sealed class CardPoolHoverTests
{
    private static readonly Lazy<CardPoolCatalog> Catalog = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card-pool.json.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return CardPoolCatalog.Read(gz)!;
    });

    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_08","dbfId":637,"name":"Jaina","type":"HERO","cardClass":"MAGE"},
          {"id":"HERO_01","dbfId":7,"name":"Garrosh","type":"HERO","cardClass":"WARRIOR"}
        ]
        """u8.ToArray()));

    [Fact]
    public void Only_a_hand_with_a_pool_card_wants_the_card_under_the_mouse()
    {
        var tracker = new CardPoolTracker { Engine = new CardPoolEngine(Catalog.Value, Cards) };
        var parser = Game(tracker, "GT_RANKED", ("CS2_106", "WEAPON"));
        Assert.False(tracker.HandHasPool(parser.Game!, Cards));

        // Blazing Invocation (Discover a Battlecry minion) joins the hand.
        Feed(parser, [
            "DebugPrintPower() - FULL_ENTITY - Creating ID=51 CardID=CORE_GIL_836",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        ]);
        Assert.True(tracker.HandHasPool(parser.Game!, Cards));

        // Played: nothing in the hand has a pool any more.
        Feed(parser, ["DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=51 zone=HAND zonePos=2 cardId=CORE_GIL_836 player=1] tag=ZONE value=PLAY "]);
        Assert.False(tracker.HandHasPool(parser.Game!, Cards));
    }

    [Fact]
    public void Without_an_engine_or_in_Battlegrounds_nothing_is_read()
    {
        var withoutEngine = new CardPoolTracker();
        Assert.False(withoutEngine.HandHasPool(Game(withoutEngine, "GT_RANKED", ("CORE_GIL_836", "SPELL")).Game!, Cards));

        var battlegrounds = new CardPoolTracker { Engine = new CardPoolEngine(Catalog.Value, Cards) };
        Assert.False(battlegrounds.HandHasPool(Game(battlegrounds, "GT_BATTLEGROUNDS", ("CORE_GIL_836", "SPELL")).Game!, Cards));
    }

    private static readonly CardDb SynCards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":1,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"DR_2","dbfId":2,"name":"Marker","type":"MINION","cost":1,"set":"X","collectible":true},
          {"id":"GEN_A","dbfId":10,"name":"Beast Caller","type":"SPELL","cost":2,"set":"X","collectible":true},
          {"id":"GEN_B","dbfId":11,"name":"Beast Seeker","type":"SPELL","cost":2,"set":"X","collectible":true},
          {"id":"PLAIN","dbfId":12,"name":"Chillwind Yeti","type":"MINION","cost":4,"set":"X","collectible":true},
          {"id":"LISTED","dbfId":13,"name":"Listed","type":"MINION","cost":3,"set":"X","collectible":true},
          {"id":"BEAST_1","dbfId":20,"name":"Wolf","type":"MINION","cost":1,"set":"X","collectible":true},
          {"id":"BEAST_2","dbfId":21,"name":"Bear","type":"MINION","cost":3,"set":"X","collectible":true}
        ]
        """u8.ToArray()));

    private static PoolCard Pool(string id, string name, string text, string type = "SPELL", params string[] races) =>
        new(id, 0, name, text, type, "COMMON", 1, type == "MINION" ? 1 : null, type == "MINION" ? 1 : null, ["NEUTRAL"], races, null, [], [], "X", true);

    private static readonly CardPoolCatalog SynCatalog = new(
    [
        Pool("GEN_A", "Beast Caller", "Discover a Beast."),
        Pool("GEN_B", "Beast Seeker", "Discover a Beast."),
        Pool("PLAIN", "Chillwind Yeti", "A big yeti.", "MINION"),
        Pool("LISTED", "Listed", "A minion.", "MINION"),
        Pool("BEAST_1", "Wolf", "A wolf.", "MINION", "BEAST"),
        Pool("BEAST_2", "Bear", "A bear.", "MINION", "BEAST"),
        .. Enumerable.Range(1, 8).Select(i => Pool($"NONE_{i}", $"Dragon Caller {i}", "Discover a Dragon.")),
    ], "test", DateTime.UtcNow);

    private static (GameState Game, CardPoolTracker Tracker) Hand(params string[] hand)
    {
        var (game, _) = Play([[.. hand.SelectMany((id, i) => EctoplasmListTests.Card(40 + i, id, "HAND", 1, "SPELL"))]]);
        return (game, new CardPoolTracker { Engine = new CardPoolEngine(SynCatalog, SynCards) });
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>AC1: crossing a neighbour with nothing to show on the way to the panel keeps the generator's pool.</summary>
    [Fact]
    public void A_neighbour_without_a_pool_or_list_keeps_the_left_cards_pool_until_its_linger_ends()
    {
        var (game, tracker) = Hand("GEN_A", "PLAIN");
        var now = DateTime.UtcNow;

        tracker.SetHovered("GEN_A", now - Ms(700));
        Assert.Equal("GEN_A", tracker.BuildView(game, SynCards)?.SourceCardId);

        // Left the generator 500 ms ago over the neighbour, then reached the panel (nothing under the mouse).
        tracker.SetHovered("PLAIN", now - Ms(500));
        Assert.Equal("GEN_A", tracker.BuildView(game, SynCards)?.SourceCardId);
        tracker.SetHovered(null, now - Ms(250));
        Assert.Equal("GEN_A", tracker.BuildView(game, SynCards)?.SourceCardId);
    }

    /// <summary>AC1: the linger runs from leaving the generator; crossing a card with nothing to show does not restart it.</summary>
    [Fact]
    public void The_linger_counts_from_leaving_the_generator_not_from_the_neighbour()
    {
        var (game, tracker) = Hand("GEN_A", "PLAIN");
        var now = DateTime.UtcNow;

        tracker.SetHovered("GEN_A", now - Ms(1200));
        Assert.NotNull(tracker.BuildView(game, SynCards));
        tracker.SetHovered("PLAIN", now - Ms(900));
        tracker.SetHovered(null, now - Ms(50));

        Assert.Null(tracker.BuildView(game, SynCards));
        // The ended linger is reported, so the overlay hides the panel.
        Assert.True(tracker.SetHovered(null, now));
    }

    /// <summary>AC1: a neighbour with a pool of its own, or with a list, takes the panel as before.</summary>
    [Fact]
    public void A_neighbour_with_its_own_pool_or_list_takes_the_panel()
    {
        var (game, tracker) = Hand("GEN_A", "GEN_B", "LISTED");
        var now = DateTime.UtcNow;

        tracker.SetHovered("GEN_A", now - Ms(400));
        Assert.Equal("GEN_A", tracker.BuildView(game, SynCards)?.SourceCardId);
        tracker.SetHovered("GEN_B", now - Ms(300));
        Assert.Equal("GEN_B", tracker.BuildView(game, SynCards)?.SourceCardId);

        // A card with only a list ("this game" or related cards): the session's predicate says so.
        bool Shows(string id) => id == "LISTED" || tracker.Engine!.Build(id, CardPoolTracker.ContextOf(game, SynCards), PoolTrigger.Hover) is not null;
        tracker.SetHovered("LISTED", now - Ms(200));
        Assert.Equal("LISTED", tracker.HoverCard(game, Shows));
        Assert.Null(tracker.BuildView(game, SynCards, "LISTED"));

        // Leaving the listed card for nothing lingers on it, not on the generators before it.
        tracker.SetHovered(null, now - Ms(100));
        Assert.Equal("LISTED", tracker.HoverCard(game, Shows));
    }

    /// <summary>AC1/AC10: a lingering card that left your hand (played) shows nothing.</summary>
    [Fact]
    public void A_lingering_card_that_is_not_in_your_hand_shows_nothing()
    {
        var (game, tracker) = Hand("PLAIN");
        tracker.SetHovered("GEN_A", DateTime.UtcNow);
        Assert.Null(tracker.BuildView(game, SynCards));
        tracker.SetHovered(null, DateTime.UtcNow);
        Assert.Null(tracker.BuildView(game, SynCards));
    }

    /// <summary>AC1: the mouse on a card outside your hand (the board) ends the linger; it does not come back after.</summary>
    [Fact]
    public void A_card_on_the_board_ends_the_linger()
    {
        var (game, _) = Play([[.. EctoplasmListTests.Card(40, "GEN_A", "HAND", 1, "SPELL"), .. Minion(41, "PLAIN", 1)]]);
        var tracker = new CardPoolTracker { Engine = new CardPoolEngine(SynCatalog, SynCards) };
        var now = DateTime.UtcNow;

        tracker.SetHovered("GEN_A", now - Ms(300));
        Assert.NotNull(tracker.BuildView(game, SynCards));
        tracker.SetHovered("PLAIN", now - Ms(200));
        Assert.Null(tracker.BuildView(game, SynCards));
        tracker.SetHovered(null, now - Ms(100));
        Assert.Null(tracker.BuildView(game, SynCards));
    }

    /// <summary>
    /// AC4: a hand of 10 with generators whose pool matches no catalog card, 100 publishes without a change: the catalog
    /// is walked at most once per (card, context, wiki state).
    /// </summary>
    [Fact]
    public void Generators_without_a_pool_walk_the_catalog_once_over_many_publishes()
    {
        var (game, tracker) = Hand("PLAIN", "NONE_1", "NONE_2", "NONE_3", "NONE_4", "NONE_5", "NONE_6", "NONE_7", "NONE_8", "GEN_A");
        var engine = tracker.Engine!;
        tracker.SetHovered("NONE_3", DateTime.UtcNow);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(tracker.HandHasPool(game, SynCards));
            Assert.Null(tracker.BuildView(game, SynCards));
        }

        // NONE_1..8 and GEN_A, one context: 9 walks at most.
        Assert.InRange(engine.CatalogWalks, 1, 9);
    }

    /// <summary>AC4: a built view does not build the wiki lists again; a new wiki answer makes a new view once.</summary>
    [Fact]
    public async Task A_built_view_does_not_build_its_wiki_lists_again()
    {
        var folder = Path.Combine(TestDataFolder.Path, "wiki-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "card-wiki-BE_298.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CardWiki", "BE_298.json"), file);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        var wiki = new WikiPoolSource(new HearthpulseData(new HearthpulseApi(new HttpClient(new NoNetwork())), folder));
        await wiki.LoadAsync("BE_298");
        using var gzFile = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card-pool.json.gz"));
        using var gz = new GZipStream(gzFile, CompressionMode.Decompress);
        var engine = new CardPoolEngine(CardPoolCatalog.Read(gz)!, CardDb.Empty, wiki: wiki);
        var context = new PoolContext("FT_STANDARD", "DEATHKNIGHT", "WARRIOR") { GameType = "GT_RANKED" };

        var first = engine.Build("BE_298", context, PoolTrigger.Hover)!;
        for (var i = 0; i < 100; i++)
        {
            Assert.Same(first, engine.Build("BE_298", context, PoolTrigger.Hover));
        }

        Assert.Equal(WikiPools.Source, first.Source);
        Assert.Equal(1, engine.WikiListBuilds);
        Assert.Equal(1, engine.CatalogWalks);

        // Another trigger of the same pool reuses it.
        Assert.Same(first.Cards, engine.Build("BE_298", context, PoolTrigger.Choice)!.Cards);
        Assert.Equal(1, engine.WikiListBuilds);
    }

    private static PowerParser Game(CardPoolTracker tracker, string gameType, (string CardId, string Type) inHand)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        parser.AddListener(tracker);
        Feed(parser, [
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
            $"DebugPrintGame() - GameType={gameType}",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            $"DebugPrintPower() - FULL_ENTITY - Creating ID=50 CardID={inHand.CardId}",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            $"DebugPrintPower() -     tag=CARDTYPE value={inHand.Type}",
        ]);
        return parser;
    }

    private static void Feed(PowerParser parser, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        parser.Flush();
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"A test reached the network: {request.RequestUri}");
    }
}
