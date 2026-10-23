using Hearthlens.Core.Cards;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Power;

namespace Hearthlens.Tests;

public sealed class ParserTests
{
    private static PowerParser Feed(params string[] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 20));
        foreach (var body in bodies)
        {
            parser.Feed("D 16:44:59.2148622 GameState." + body);
        }

        parser.Flush();
        return parser;
    }

    [Fact]
    public void Builds_entities_players_and_resolves_references()
    {
        var parser = Feed(
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=4 CardID=CORE_CS2_029",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=5 CardID=",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            "DebugPrintPower() - TAG_CHANGE Entity=Rival#1234 tag=MULLIGAN_STATE value=INPUT ",
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=5 zone=HAND zonePos=1 cardId= player=2] tag=ZONE value=DECK ");

        var game = parser.Game!;
        Assert.Equal("GT_RANKED", game.GameType);
        Assert.Equal(1, game.LocalPlayerId);
        Assert.Equal("Rival#1234", game.OpponentPlayer!.Name);
        Assert.Equal("INPUT", game.Entities[3].GetString(GameTag.MulliganState));
        Assert.True(game.Entities[5].Mulliganed);
        Assert.True(game.Entities[4].IsOriginalDeckCard);
    }

    [Fact]
    public void Show_entity_reveals_card_before_zone_change_events()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 20));
        var listener = new ZoneRecorder();
        parser.AddListener(listener);
        foreach (var body in new[]
                 {
                     "DebugPrintPower() - CREATE_GAME",
                     "DebugPrintPower() -     GameEntity EntityID=1",
                     "DebugPrintPower() - FULL_ENTITY - Creating ID=9 CardID=",
                     "DebugPrintPower() -     tag=ZONE value=DECK",
                     "DebugPrintPower() -     tag=CONTROLLER value=2",
                     "DebugPrintPower() - SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=9 zone=DECK zonePos=0 cardId= player=2] CardID=EX1_001",
                     "DebugPrintPower() -     tag=ZONE value=PLAY",
                     "DebugPrintPower() - BLOCK_END",
                 })
        {
            parser.Feed("D 10:00:00.0000000 GameState." + body);
        }

        Assert.Equal(("EX1_001", "DECK", "PLAY"), listener.Changes.Single());
    }

    [Fact]
    public void Card_names_fall_back_to_the_name_the_client_logged()
    {
        using var json = new MemoryStream("""[{"id":"BG_KNOWN","name":"Known Card"}]"""u8.ToArray());
        var cards = CardDb.Parse("enUS", json);
        Assert.Equal("Known Card", cards.NameOr("BG_KNOWN", "Logged"));
        Assert.Equal("Logged Name", cards.NameOr("BG_MISSING", "Logged Name"));
        Assert.Equal("BG_MISSING", cards.NameOr("BG_MISSING", ""));
    }

    [Fact]
    public void Deck_codes_round_trip()
    {
        // Version 1, Standard, one hero (dbf 7), cards: 1x dbf 1, 2x dbf 300, 3x dbf 70000.
        byte[] payload = [0, 1, 2, 1, 7, 1, 1, 1, 0xAC, 0x02, 1, 0xF0, 0xA2, 0x04, 3];
        var deck = DeckString.TryDecode(Convert.ToBase64String(payload), "Test", 42);
        Assert.NotNull(deck);
        Assert.Equal("Standard", deck!.Format);
        Assert.Equal([7], deck.HeroDbfIds);
        Assert.Equal(1, deck.Cards[1]);
        Assert.Equal(2, deck.Cards[300]);
        Assert.Equal(3, deck.Cards[70000]);
        Assert.Equal(6, deck.CardCount);
    }

    [Theory]
    [InlineData(1, 0, "Bronze 10")]
    [InlineData(10, 0, "Bronze 1")]
    [InlineData(41, 0, "Diamond 10")]
    [InlineData(50, 0, "Diamond 1")]
    [InlineData(51, 1204, "Legend 1,204")]
    public void Rank_display(int starLevel, int legend, string expected)
    {
        var rank = new RankInfo("Standard", 5, starLevel, legend, 140);
        Assert.Equal(expected, rank.Display.Replace('.', ','));
    }

    private sealed class ZoneRecorder : IPowerListener
    {
        public List<(string, string, string)> Changes { get; } = new();

        public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
        {
            if (tag == GameTag.Zone)
            {
                Changes.Add((entity.CardId, previous, value));
            }
        }
    }
}
