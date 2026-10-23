using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Marks under the opponent's hand: how each card got there (drawn, created, shuffled in by you, returned, the Coin),
/// the hover text, and where each mark sits under the fan.
/// </summary>
public sealed class OpponentHandTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"CORE_CS2_004","dbfId":120002,"name":"Power Word: Shield","type":"SPELL","cardClass":"PRIEST","cost":1},
          {"id":"BE_012","dbfId":120003,"name":"Dark Deacon","type":"MINION","cardClass":"PRIEST","cost":3},
          {"id":"BE_014","dbfId":120005,"name":"Spawn of C'Thun","type":"MINION","cardClass":"NEUTRAL","cost":4},
          {"id":"TLC_515","dbfId":120008,"name":"Cultist Map","type":"SPELL","cardClass":"ROGUE","cost":1},
          {"id":"GAME_005","dbfId":1746,"name":"The Coin","type":"SPELL","cardClass":"NEUTRAL","cost":0}
        ]
        """u8.ToArray()));

    /// <summary>
    /// A Ranked game with velox as the local player 1 and an opponent as player 2: <paramref name="atStart"/> runs while the
    /// game is created (the starting decks), then the game moves to turn 5 (player turn 3) and <paramref name="bodies"/> run.
    /// </summary>
    private static IReadOnlyList<HandSlot> Hand(string[] atStart, params string[] bodies) =>
        View(new ConstructedTracker(), "FT_STANDARD", atStart, bodies).OpponentHand;

    private static ConstructedView View(ConstructedTracker tracker, string format, string[] atStart, params string[] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        parser.AddListener(tracker);
        string[] setup =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=20 CardID=HERO_09",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintGame() - GameType=GT_RANKED",
            $"DebugPrintGame() - FormatType={format}",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            "DebugPrintPower() - FULL_ENTITY - Creating ID=99 CardID=CORE_CS2_004",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        ];
        foreach (var body in setup.Concat(atStart).Append("DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=5 ").Concat(bodies))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
        return tracker.BuildView(parser.Game!, Cards);
    }

    private static string[] Card(int id, string cardId, string zone, int controller, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    private static string ToHand(int id, string zone, string cardId = "") =>
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone={zone} zonePos=0 cardId={cardId} player=2] tag=ZONE value=HAND ";

    [Fact]
    public void A_card_drawn_from_their_deck_shows_the_turn()
    {
        var slot = Assert.Single(Hand(Card(50, "", "DECK", 2, "tag=CARDTYPE value=MINION"), ToHand(50, "DECK")));

        Assert.Equal(HandCardOrigin.Drawn, slot.Origin);
        Assert.Equal(3, slot.Turn);
        Assert.Null(slot.CardId);
        Assert.Equal("3", OpponentHandFan.Label(slot));
        Assert.Equal("Drawn on turn 3.", OpponentHandFan.Hint(slot));
    }

    [Fact]
    public void A_card_you_shuffled_into_their_deck_stays_a_card_back_when_drawn()
    {
        // The log keeps the id of the card you shuffled in, but after the deck is shuffled the player cannot tell
        // which drawn card it is: the mark must not say (no hidden information).
        var slot = Assert.Single(Hand(
        [],
        [
            .. Card(60, "BE_014", "DECK", 2, "tag=CARDTYPE value=MINION", "tag=CREATOR value=99"),
            ToHand(60, "DECK", "BE_014"),
        ]));

        Assert.Equal(HandCardOrigin.Drawn, slot.Origin);
        Assert.Null(slot.CardId);
        Assert.Null(slot.CardName);
        Assert.Null(slot.CreatedById);
        Assert.Null(slot.CreatedBy);
        Assert.Equal("Drawn on turn 3.", OpponentHandFan.Hint(slot));
    }

    [Fact]
    public void A_drawn_card_the_game_shows_again_is_known()
    {
        var slot = Assert.Single(Hand(
        [],
        [
            .. Card(60, "BE_014", "DECK", 2, "tag=CARDTYPE value=MINION", "tag=CREATOR value=99"),
            ToHand(60, "DECK", "BE_014"),
            "DebugPrintPower() - SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=60 zone=HAND zonePos=1 cardId= player=2] CardID=BE_014",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
        ]));

        Assert.Equal(HandCardOrigin.Shuffled, slot.Origin);
        Assert.Equal("BE_014", slot.CardId);
        Assert.True(slot.CreatorIsYours);
        Assert.Equal("Spawn of C'Thun\nShuffled into their deck by your Power Word: Shield, drawn on turn 3.", OpponentHandFan.Hint(slot));
    }

    [Fact]
    public void A_card_created_in_hand_names_its_creator()
    {
        var hand = Hand(
        [],
        [
            .. Card(80, "BE_012", "PLAY", 2, "tag=CARDTYPE value=MINION"),
            .. Card(70, "", "HAND", 2, "tag=CREATOR value=80"),
        ]);

        var slot = Assert.Single(hand);
        Assert.Equal(HandCardOrigin.Created, slot.Origin);
        Assert.Equal("BE_012", slot.CreatedById);
        Assert.False(slot.CreatorIsYours);
        Assert.Equal("Created by Dark Deacon on turn 3.", OpponentHandFan.Hint(slot));
    }

    [Fact]
    public void A_hidden_creator_is_not_named_through_its_dbf_id()
    {
        // CREATOR_DBID names the creator even when the player only sees a card back.
        var slot = Assert.Single(Hand([], Card(71, "", "HAND", 2, "tag=CREATOR_DBID value=120008")));

        Assert.Equal(HandCardOrigin.Created, slot.Origin);
        Assert.Null(slot.CreatedById);
        Assert.Null(slot.CreatedBy);
        Assert.Equal("Created on turn 3.", OpponentHandFan.Hint(slot));
    }

    [Fact]
    public void A_creator_still_in_their_hand_is_not_named()
    {
        var hand = Hand(
        [],
        [
            .. Card(81, "", "HAND", 2),
            .. Card(72, "", "HAND", 2, "tag=CREATOR value=81", "tag=CREATOR_DBID value=120003"),
        ]);

        Assert.All(hand, slot => Assert.Null(slot.CreatedById));
        Assert.Equal(HandCardOrigin.Created, hand.Single(h => h.EntityId == 72).Origin);
    }

    [Fact]
    public void A_coin_made_mid_game_is_a_created_card()
    {
        var slot = Assert.Single(Hand([], Card(6, "GAME_005", "HAND", 2, "tag=CARDTYPE value=SPELL")));

        Assert.False(slot.IsCoin);
        Assert.Equal(HandCardOrigin.Created, slot.Origin);
        Assert.Equal("3", OpponentHandFan.Label(slot));
    }

    [Fact]
    public void Meta_win_rate_comes_only_from_stats_of_the_game_format()
    {
        var archetype = new Core.Constructed.MetaArchetype(1, "cthun", "C'Thun Priest", "priest", 5000, 2700, 0.54, 0.05, 0, 0, 0, 0,
            ["BE_012", "BE_014"], []);
        var meta = new Core.Constructed.ConstructedMeta("standard", "legend", "past-7", DateTime.UtcNow, 5000, [archetype], []);
        var deck = new DeckDefinition("Priest", 1, "code", "FT_STANDARD", [813], new Dictionary<int, int> { [120003] = 2, [120005] = 2 });

        var standard = View(new ConstructedTracker { Meta = meta, Deck = deck }, "FT_STANDARD", []);
        var wild = View(new ConstructedTracker { Meta = meta, Deck = deck }, "FT_WILD", []);

        Assert.Equal("C'Thun Priest", standard.DeckMeta?.Archetype);
        Assert.Equal("Standard", standard.DeckMeta?.Format);
        Assert.Null(wild.DeckMeta);
    }

    [Fact]
    public void A_bounced_minion_is_known_and_marked_returned()
    {
        var slot = Assert.Single(Hand(Card(90, "BE_014", "PLAY", 2, "tag=CARDTYPE value=MINION"), ToHand(90, "PLAY", "BE_014")));

        Assert.Equal(HandCardOrigin.Returned, slot.Origin);
        Assert.Equal("BE_014", slot.CardId);
        Assert.Equal("Spawn of C'Thun\nReturned to their hand on turn 3.", OpponentHandFan.Hint(slot));
    }

    [Fact]
    public void The_coin_has_no_turn_label()
    {
        var slot = Assert.Single(Hand(Card(5, "GAME_005", "HAND", 2, "tag=CARDTYPE value=SPELL")));

        Assert.Equal(HandCardOrigin.Coin, slot.Origin);
        Assert.Equal(string.Empty, OpponentHandFan.Label(slot));
        Assert.Equal("The Coin: they went second.", OpponentHandFan.Hint(slot));
    }

    [Fact]
    public void Opening_hand_cards_read_M()
    {
        var slot = new HandSlot(1, 0, null, null, true, false, null) { Origin = HandCardOrigin.Mulligan };

        Assert.Equal("M", OpponentHandFan.Label(slot));
        Assert.Equal("In their opening hand since the mulligan.", OpponentHandFan.Hint(slot));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(10)]
    public void Marks_spread_under_the_fan_symmetrically(int count)
    {
        var positions = OpponentHandFan.Positions(count);

        Assert.Equal(count, positions.Count);
        Assert.InRange(positions[^1].X - positions[0].X, 0, OpponentHandFan.MaxSpan + 0.001);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(-positions[i].X, positions[count - 1 - i].X, 6);
            Assert.Equal(positions[i].Y, positions[count - 1 - i].Y, 6);
        }

        for (var i = 1; i < count; i++)
        {
            Assert.True(positions[i].X > positions[i - 1].X);
            Assert.True(positions[i].X - positions[i - 1].X <= OpponentHandFan.CardPitch + 0.001);
        }

        // The middle of the fan hangs lowest.
        Assert.Equal(positions.Max(p => p.Y), positions[(count - 1) / 2].Y, 6);
    }

    [Fact]
    public void A_full_hand_fills_the_whole_fan_and_a_small_one_keeps_the_pitch()
    {
        var full = OpponentHandFan.Positions(10);
        var small = OpponentHandFan.Positions(3);

        Assert.Equal(OpponentHandFan.MaxSpan, full[^1].X - full[0].X, 6);
        Assert.Equal(OpponentHandFan.CardPitch, small[1].X - small[0].X, 6);
        Assert.Empty(OpponentHandFan.Positions(0));
    }
}
