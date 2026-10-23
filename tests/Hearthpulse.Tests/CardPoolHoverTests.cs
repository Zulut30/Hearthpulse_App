using System.IO.Compression;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Tests;

/// <summary>
/// The card under the mouse is read from the game only while a hover can show something: a card with a pool in your
/// hand (legal review L-m1: memory reads only when needed).
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
}
