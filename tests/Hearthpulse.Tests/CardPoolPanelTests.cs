using System.IO.Compression;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The pool panel's chart and placement: which cards a histogram column holds, the most played cards, where the panel
/// stands next to its card (hand, Discover options) and the tracker's anchor from the game state.
/// </summary>
public sealed class CardPoolPanelTests
{
    private static readonly Lazy<CardPoolCatalog> Fixture = new(() =>
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "card-pool.json.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return CardPoolCatalog.Read(gz)!;
    });

    private static PoolContext Standard(string ownClass) => new("FT_STANDARD", ownClass, "WARRIOR");

    [Theory]
    [InlineData("Cost")]
    [InlineData("Attack")]
    [InlineData("Health")]
    public void A_column_holds_exactly_the_cards_its_bar_counts(string stat)
    {
        var view = new CardPoolEngine(Fixture.Value, CardDb.Empty).Build("CORE_GIL_836", Standard("SHAMAN"), PoolTrigger.Choice)!;
        var histogram = stat switch { "Attack" => view.Attack!, "Health" => view.Health!, _ => view.Cost };

        for (var bar = 0; bar < histogram.Bars.Count; bar++)
        {
            Assert.Equal(histogram.Bars[bar].Count, PoolStats.CardsIn(view, stat, bar).Count);
        }

        Assert.Equal(histogram.Count, view.Cards.Count(c => PoolStats.Bucket(c, stat) is not null));
    }

    [Fact]
    public void Columns_and_top_cards_put_the_most_played_first()
    {
        var plain = new CardPoolEngine(Fixture.Value, CardDb.Empty).Build("CORE_GIL_836", Standard("SHAMAN"), PoolTrigger.Choice)!;
        var second = plain.Cards.First(c => c.CardId != "BE_014").CardId;
        var meta = new Dictionary<string, int> { ["BE_014"] = 900, [second] = 50, ["NOT_IN_POOL"] = 5000 };
        var view = new CardPoolEngine(Fixture.Value, CardDb.Empty, meta).Build("CORE_GIL_836", Standard("SHAMAN"), PoolTrigger.Choice)!;

        Assert.Equal(["BE_014", second], PoolStats.TopCards(view, 4).Select(c => c.CardId));
        var played = view.Cards.Single(c => c.CardId == "BE_014");
        Assert.Equal("BE_014", PoolStats.CardsIn(view, "Cost", PoolStats.Bucket(played, "Cost")!.Value)[0].CardId);
        // Without meta data nothing is "most played".
        Assert.Empty(PoolStats.TopCards(plain, 4));
        // A spell has no Attack column.
        Assert.Null(PoolStats.Bucket(new PoolEntry("X", "X", 3, null, null, "SPELL", "COMMON", "MAGE", 0), "Attack"));
        Assert.Equal(7, PoolStats.Bucket(new PoolEntry("X", "X", 12, 9, 15, "MINION", "COMMON", "MAGE", 0), "Cost"));
        Assert.Equal(9, PoolStats.Bucket(new PoolEntry("X", "X", 12, 9, 15, "MINION", "COMMON", "MAGE", 0), "Health"));
    }

    // The compact panel: 300 wide, about 400 high (more with the pick hint of an open choice).
    private const double PanelWidth = 300;
    private const double PanelHeight = 400;

    [Fact]
    public void The_panel_stands_beside_the_hovered_hand_card_and_above_the_hand()
    {
        const double w = PanelWidth, h = PanelHeight;
        var left = new CardPoolAnchor(PoolAnchorZone.Hand, 0, 6);
        var leftCard = 960 + PoolPlacement.SlotOffset(left);
        var (x, y) = PoolPlacement.Place(left, 1920, w, h);
        // Right of the game's enlarged preview (about 150 either side of the card), the hand below stays clear.
        Assert.True(x >= leftCard + 150);
        Assert.True(y + h <= 900);

        var right = new CardPoolAnchor(PoolAnchorZone.Hand, 5, 6);
        var rightCard = 960 + PoolPlacement.SlotOffset(right);
        (x, _) = PoolPlacement.Place(right, 1920, w, h);
        Assert.True(x + w <= rightCard - 150);
        Assert.True(rightCard > leftCard);

        // The hovered card itself stays clear: the panel is beside its preview, not over the card.
        for (var i = 0; i < 10; i++)
        {
            var anchor = new CardPoolAnchor(PoolAnchorZone.Hand, i, 10);
            var card = 960 + PoolPlacement.SlotOffset(anchor);
            (x, y) = PoolPlacement.Place(anchor, 1920, w, h);
            Assert.True(x >= card + 150 || x + w <= card - 150);
            Assert.InRange(y, 8, 900 - h);
        }
    }

    [Fact]
    public void Beside_the_Discover_options_and_always_on_screen()
    {
        var choice = new CardPoolAnchor(PoolAnchorZone.Choice, 0, 3);
        var (x, _) = PoolPlacement.Place(choice, 1920, PanelWidth, PanelHeight);
        Assert.True(x >= 960 + 450);
        Assert.True(x + PanelWidth <= 1920);

        // Five options on a narrow 4:3 screen leave no room on the right: left of the row, still on screen.
        foreach (var anchor in new[] { choice, new CardPoolAnchor(PoolAnchorZone.Choice, 0, 5), new CardPoolAnchor(PoolAnchorZone.Hand, 0, 10), new CardPoolAnchor(PoolAnchorZone.Hand, 9, 10) })
        {
            foreach (var height in new[] { PanelHeight, 560 })
            {
                var (nx, ny) = PoolPlacement.Place(anchor, 1440, PanelWidth, height);
                Assert.InRange(nx, 8, 1440 - 8 - PanelWidth);
                Assert.InRange(ny, 8, 1080 - 8 - height);
            }
        }

        // The overlay's own scale enlarges the panel but not the game's cards.
        var (sx, sy) = PoolPlacement.PlaceOnSurface(choice, 1920 / 1.25, 1.25, PanelWidth, PanelHeight);
        var (gx, gy) = PoolPlacement.Place(choice, 1920, PanelWidth * 1.25, PanelHeight * 1.25);
        Assert.Equal(gx / 1.25, sx, 6);
        Assert.Equal(gy / 1.25, sy, 6);
    }

    [Fact]
    public void The_tracker_anchors_the_pool_to_its_card()
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
        var lines = new List<string>
        {
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
        };
        // Hand: three cards, Blazing Invocation in the middle; board: Swashburglar (a generator) left of another minion.
        foreach (var (id, cardId, zone, type, position) in new[]
                 {
                     (60, "CS2_029", "HAND", "SPELL", 1), (61, "CORE_GIL_836", "HAND", "SPELL", 2), (62, "CS2_025", "HAND", "SPELL", 3),
                     (70, "CORE_KAR_069", "PLAY", "MINION", 1), (71, "CS2_182", "PLAY", "MINION", 2),
                 })
        {
            lines.Add($"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}");
            lines.Add($"DebugPrintPower() -     tag=ZONE value={zone}");
            lines.Add("DebugPrintPower() -     tag=CONTROLLER value=1");
            lines.Add($"DebugPrintPower() -     tag=CARDTYPE value={type}");
            lines.Add($"DebugPrintPower() -     tag=ZONE_POSITION value={position}");
        }

        foreach (var line in lines)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        parser.Flush();
        var game = parser.Game!;

        tracker.SetHovered("CORE_GIL_836", DateTime.UtcNow);
        var hand = tracker.BuildView(game, cards)!;
        Assert.Equal(PoolTrigger.Hover, hand.Trigger);
        Assert.Equal(new CardPoolAnchor(PoolAnchorZone.Hand, 1, 3), tracker.AnchorOf(game, hand));

        // A generator on your board shows no pool on hover; only the hand counts for memory reads too.
        tracker.SetHovered("CORE_KAR_069", DateTime.UtcNow);
        Assert.Null(tracker.BuildView(game, cards));
        Assert.True(tracker.HandHasPool(game, cards));

        // An open Discover choice comes first and stands beside the options.
        foreach (var line in new[]
                 {
                     "DebugPrintEntityChoices() - id=5 Player=velox#21276 TaskList=570 ChoiceType=GENERAL CountMin=1 CountMax=1",
                     "DebugPrintEntityChoices() -   Source=[entityName=X id=61 zone=PLAY zonePos=0 cardId=CORE_GIL_836 player=1]",
                     "DebugPrintEntityChoices() -   Entities[0]=[entityName=Y id=80 zone=SETASIDE zonePos=0 cardId=BE_014 player=1]",
                     "DebugPrintEntityChoices() -   Entities[1]=[entityName=Y id=81 zone=SETASIDE zonePos=0 cardId=CORE_EX1_284 player=1]",
                     "DebugPrintEntityChoices() -   Entities[2]=[entityName=Y id=82 zone=SETASIDE zonePos=0 cardId=CORE_CS2_117 player=1]",
                 })
        {
            parser.Feed("D 12:30:01.0000000 GameState." + line);
        }

        parser.Flush();
        var choice = tracker.BuildView(game, cards)!;
        Assert.Equal(PoolTrigger.Choice, choice.Trigger);
        Assert.Equal(new CardPoolAnchor(PoolAnchorZone.Choice, 0, 3), tracker.AnchorOf(game, choice));

        // A requested pool has no card on the screen: the panel keeps its own spot.
        tracker.OnChoiceMade(game);
        tracker.Request("CORE_KAR_069");
        Assert.Null(tracker.AnchorOf(game, tracker.BuildView(game, cards)));
    }

    [Fact]
    public void Memory_is_not_read_for_a_generator_that_is_only_on_the_board()
    {
        var cards = CardDb.Empty;
        var tracker = new CardPoolTracker { Engine = new CardPoolEngine(Fixture.Value, cards) };
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        parser.AddListener(tracker);
        foreach (var line in new[]
                 {
                     "DebugPrintPower() - CREATE_GAME",
                     "DebugPrintPower() -     GameEntity EntityID=1",
                     "DebugPrintPower() -         tag=CARDTYPE value=GAME",
                     "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
                     "DebugPrintPower() -         tag=CONTROLLER value=1",
                     "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
                     "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
                     "DebugPrintPower() -         tag=CONTROLLER value=2",
                     "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
                     "DebugPrintGame() - GameType=GT_RANKED",
                     "DebugPrintGame() - FormatType=FT_STANDARD",
                     "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
                     "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
                     "DebugPrintPower() - FULL_ENTITY - Creating ID=70 CardID=CORE_KAR_069",
                     "DebugPrintPower() -     tag=ZONE value=PLAY",
                     "DebugPrintPower() -     tag=CONTROLLER value=1",
                     "DebugPrintPower() -     tag=CARDTYPE value=MINION",
                     "DebugPrintPower() -     tag=ZONE_POSITION value=1",
                 })
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        parser.Flush();
        Assert.False(tracker.HandHasPool(parser.Game!, cards));
    }
}
