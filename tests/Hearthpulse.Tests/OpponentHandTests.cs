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
    public void The_opening_hand_and_a_skinned_coin_are_marked_in_the_real_order()
    {
        // Real logs set TURN=1 before the deal; the Coin is a skin (EDR_COIN1) marked COIN_CARD=1 and dealt with the hand.
        var hand = Hand(
        [
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=1 ",
            .. Card(40, "", "DECK", 2, "tag=CARDTYPE value=MINION"),
            .. Card(41, "", "DECK", 2, "tag=CARDTYPE value=MINION"),
            ToHand(40, "DECK"),
            .. Card(77, "EDR_COIN1", "HAND", 2, "tag=CARDTYPE value=SPELL", "tag=COIN_CARD value=1"),
            "DebugPrintPower() - TAG_CHANGE Entity=3 tag=MULLIGAN_STATE value=INPUT ",
            "DebugPrintPower() - TAG_CHANGE Entity=3 tag=MULLIGAN_STATE value=DONE ",
        ],
        ToHand(41, "DECK"));

        var dealt = hand.Single(h => h.EntityId == 40);
        Assert.Equal(HandCardOrigin.Mulligan, dealt.Origin);
        Assert.True(dealt.FromMulligan);
        var coin = hand.Single(h => h.EntityId == 77);
        Assert.True(coin.IsCoin);
        Assert.Equal(HandCardOrigin.Coin, coin.Origin);
        var drawn = hand.Single(h => h.EntityId == 41);
        Assert.Equal(HandCardOrigin.Drawn, drawn.Origin);
        Assert.False(drawn.FromMulligan);
        Assert.Equal(3, drawn.Turn);
    }

    [Fact]
    public void A_deck_card_that_transformed_in_hand_still_left_the_deck()
    {
        // Real log 2026-10-23 18:29: BE_781 drawn, then CHANGE_ENTITY to BE_781t4 in hand.
        var deck = new DeckDefinition("Priest", 1, "code", "FT_STANDARD", [813], new Dictionary<int, int> { [120003] = 2 });
        var view = View(new ConstructedTracker { Deck = deck }, "FT_STANDARD",
        [
            .. Card(30, "", "DECK", 1, "tag=CARDTYPE value=MINION"),
            .. Card(31, "", "DECK", 1, "tag=CARDTYPE value=MINION"),
        ],
        "DebugPrintPower() - SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=30 zone=DECK zonePos=0 cardId= player=1] CardID=BE_012",
        "DebugPrintPower() -     tag=ZONE value=HAND",
        "DebugPrintPower() - CHANGE_ENTITY - Updating Entity=[entityName=Dark Deacon id=30 zone=HAND zonePos=2 cardId=BE_012 player=1] CardID=BE_014",
        "DebugPrintPower() -     tag=CARDTYPE value=MINION");

        var row = view.Deck.Single(r => r.CardId == "BE_012");
        Assert.Equal(1, row.Remaining);
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
    public void A_game_keeps_its_formats_stats_whatever_format_was_loaded_last()
    {
        // The Wild page loads Wild stats after Standard ones: a Standard game still has its archetype, and a Wild game
        // gets the Wild one. A format without stats is asked for once per game.
        Core.Constructed.ConstructedMeta Meta(string format, string name) => new(format, "legend", "past-7", DateTime.UtcNow, 5000,
            [new Core.Constructed.MetaArchetype(1, "cthun", name, "priest", 5000, 2700, 0.54, 0.05, 0, 0, 0, 0, ["BE_012", "BE_014"], [])], []);
        var deck = new DeckDefinition("Priest", 1, "code", "FT_STANDARD", [813], new Dictionary<int, int> { [120003] = 2, [120005] = 2 });
        var tracker = new ConstructedTracker { Deck = deck };
        var asked = new List<string>();
        tracker.MetaNeeded += asked.Add;

        Assert.Null(View(tracker, "FT_STANDARD", []).DeckMeta);
        Assert.Null(View(tracker, "FT_WILD", []).DeckMeta);
        Assert.Equal(["standard", "wild"], asked);

        tracker.Meta = Meta("standard", "Standard Priest");
        tracker.Meta = Meta("wild", "Wild Priest");
        Assert.Equal("Standard Priest", View(tracker, "FT_STANDARD", []).DeckMeta?.Archetype);
        Assert.Equal("Wild Priest", View(tracker, "FT_WILD", []).DeckMeta?.Archetype);
        Assert.Equal("standard", tracker.MetaFor("FT_STANDARD")?.Format);
        Assert.Null(tracker.MetaFor("FT_TWIST"));
        Assert.Equal(2, asked.Count);
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
    public void Marks_follow_the_fan_evenly_around_its_centre(int count)
    {
        var positions = OpponentHandFan.Positions(count);

        Assert.Equal(count, positions.Count);
        Assert.InRange(positions[^1].X - positions[0].X, 0, OpponentHandFan.MaxSpan);
        Assert.Equal(OpponentHandFan.FanCentre, (positions[0].X + positions[^1].X) / 2, 6);
        for (var i = 1; i < count; i++)
        {
            var pitch = positions[i].X - positions[i - 1].X;
            Assert.InRange(pitch, 0.001, OpponentHandFan.CardPitch + 0.001);
            Assert.Equal(positions[1].X - positions[0].X, pitch, 6);
        }

        // The card edges hang from the top of the window: lowest near the middle, rising to both ends, the left end highest.
        Assert.All(positions, p => Assert.InRange(p.Y, 0, OpponentHandFan.EdgeDepth));
        var lowest = positions.ToList().FindIndex(p => p.Y == positions.Max(q => q.Y));
        Assert.InRange(lowest, (count - 1) / 2, (count + 1) / 2);
        for (var i = 0; i < count - 1; i++)
        {
            Assert.True(i < lowest ? positions[i].Y <= positions[i + 1].Y : positions[i].Y >= positions[i + 1].Y);
        }

        Assert.True(positions[0].Y <= positions[^1].Y);
    }

    [Fact]
    public void A_full_hand_squeezes_and_a_small_one_keeps_the_pitch()
    {
        var full = OpponentHandFan.Positions(10);
        var small = OpponentHandFan.Positions(3);

        // Ten cards span about a third of the window height; three keep the wide pitch.
        Assert.InRange(full[^1].X - full[0].X, 325, 340);
        Assert.Equal(OpponentHandFan.CardPitch, small[1].X - small[0].X, 6);
        // Each added card narrows the pitch, so marks never drift apart from the cards as the hand grows.
        for (var count = 3; count < 10; count++)
        {
            Assert.True(OpponentHandFan.Span(count + 1) / count < OpponentHandFan.Span(count) / (count - 1));
            Assert.True(OpponentHandFan.Span(count + 1) > OpponentHandFan.Span(count));
        }

        Assert.Empty(OpponentHandFan.Positions(0));
    }
}
