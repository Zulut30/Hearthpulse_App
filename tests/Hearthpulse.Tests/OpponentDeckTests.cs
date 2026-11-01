using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Tests.EctoplasmListTests;

namespace Hearthpulse.Tests;

/// <summary>
/// The opponent's likely deck (<c>docs/opponent-deck.md</c>): the leading archetype's most played meta list, played
/// copies counted, off-list cards apart; only face-up starting-deck cards count. Synthetic meta and Power.log lines.
/// </summary>
public sealed class OpponentDeckTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"P_A","dbfId":2001,"name":"Alpha","type":"SPELL","cost":1,"cardClass":"PRIEST","rarity":"COMMON"},
          {"id":"P_B","dbfId":2002,"name":"Bravo","type":"MINION","cost":2,"cardClass":"PRIEST","rarity":"RARE"},
          {"id":"P_C","dbfId":2003,"name":"Charlie","type":"MINION","cost":3,"cardClass":"PRIEST","rarity":"LEGENDARY"},
          {"id":"P_D","dbfId":2004,"name":"Delta","type":"MINION","cost":4,"cardClass":"PRIEST"},
          {"id":"P_E","dbfId":2005,"name":"Echo","type":"SPELL","cost":5,"cardClass":"PRIEST"},
          {"id":"N_T","dbfId":3001,"name":"Tech","type":"MINION","cost":2,"cardClass":"NEUTRAL"},
          {"id":"N_G","dbfId":3002,"name":"Generated","type":"SPELL","cost":1,"cardClass":"NEUTRAL"}
        ]
        """u8.ToArray()));

    private static MetaArchetype Archetype(int id, string name, int games, params string[] core) =>
        new(id, name, name, "PRIEST", games, games / 2, 0.5, 0.05, 0, 0, 0, 0, core, []);

    private static MetaDeck Deck(int archetype, int games, Dictionary<string, int> cards) =>
        new(DeckString.Encode(cards, "PRIEST", "FT_STANDARD", Cards)!, archetype, "x", "PRIEST", games, games / 2, 0.5, DateTime.UtcNow);

    /// <summary>Quest Priest: A×2, B×2, C (most played list) and a rarer one; Control Priest has no lists, only core cards.</summary>
    private static ConstructedMeta Meta() => new("standard", "legend", "past_week", DateTime.UtcNow, 3000,
        [Archetype(1, "Quest Priest", 2000), Archetype(2, "Control Priest", 1000, "P_D", "P_E")],
        [
            Deck(1, 900, new() { ["P_A"] = 2, ["P_B"] = 2, ["P_C"] = 1 }),
            Deck(1, 300, new() { ["P_A"] = 2, ["P_D"] = 2 }),
        ]);

    private static OpponentArchetypeView Odds(string key, double chance = 0.8) => new([new ArchetypeChance(key, key, chance, 0.5, 100)], [], 1, "test");

    [Fact]
    public void The_most_played_list_marks_played_copies_and_keeps_off_list_cards_apart()
    {
        var view = LikelyDeck.Build(Meta(), Odds("Quest Priest"), "PRIEST", new Dictionary<string, int> { ["P_A"] = 1, ["P_C"] = 1, ["N_T"] = 1 }, Cards)!;

        Assert.Equal("Quest Priest", view.ArchetypeKey);
        Assert.Equal(0.8, view.Chance);
        Assert.Equal(("legend", "past_week", 900, false), (view.Rank, view.Period, view.Games, view.CoreOnly));
        Assert.Equal([("P_A", 2, 1, false), ("P_B", 2, 0, false), ("P_C", 1, 1, true)], view.Rows.Select(r => (r.CardId, r.Copies, r.Played, r.AllPlayed)));
        Assert.Equal("LEGENDARY", view.Rows[2].Rarity);
        Assert.Equal([("N_T", 1, 1)], view.OffList.Select(r => (r.CardId, r.Copies, r.Played)));
    }

    [Fact]
    public void A_card_played_more_often_than_the_list_runs_it_counts_its_copies()
    {
        var view = LikelyDeck.Build(Meta(), Odds("Quest Priest"), "PRIEST", new Dictionary<string, int> { ["P_C"] = 2 }, Cards)!;

        var row = view.Rows.Single(r => r.CardId == "P_C");
        Assert.Equal((2, 2, true), (row.Copies, row.Played, row.AllPlayed));
    }

    [Fact]
    public void An_archetype_without_lists_shows_its_core_cards_once()
    {
        var view = LikelyDeck.Build(Meta(), Odds("Control Priest"), "PRIEST", new Dictionary<string, int> { ["P_E"] = 1 }, Cards)!;

        Assert.True(view.CoreOnly);
        Assert.Equal([("P_D", 1, 0), ("P_E", 1, 1)], view.Rows.Select(r => (r.CardId, r.Copies, r.Played)));
    }

    [Fact]
    public void No_view_without_odds_meta_or_the_archetype()
    {
        var played = new Dictionary<string, int> { ["P_A"] = 1 };
        Assert.Null(LikelyDeck.Build(Meta(), null, "PRIEST", played, Cards));
        Assert.Null(LikelyDeck.Build(null, Odds("Quest Priest"), "PRIEST", played, Cards));
        Assert.Null(LikelyDeck.Build(Meta(), Odds("Quest Priest"), "MAGE", played, Cards));
        Assert.Null(LikelyDeck.Build(Meta(), Odds("Nope Priest"), "PRIEST", played, Cards));
    }

    /// <summary>
    /// AC3: in a ranked game only cards the opponent played face up from its starting deck count; a card created during
    /// the game (generated in its hand, then played) and the cards in its hand do not, even with a known id.
    /// </summary>
    [Fact]
    public void Only_face_up_starting_deck_cards_mark_the_list_in_a_live_game()
    {
        var view = Live("GT_RANKED",
        [
            .. OpponentPlays(60, "P_A"),
            .. OpponentPlays(61, "P_A"),
            // Generated in the opponent's hand during the game, then played.
            .. Card(62, "P_B", "HAND", 2, "MINION", "tag=CREATOR value=3"),
            Zone(62, "P_B", "HAND", "PLAY"),
            .. Card(63, "N_G", "HAND", 2, "SPELL", "tag=CREATOR value=3"),
            Zone(63, "N_G", "HAND", "PLAY"),
            // Still in the opponent's hand, its id known (a card that went back to the hand): not played.
            .. Card(64, "P_D", "HAND", 2, "MINION"),
        ]).OpponentDeck!;

        Assert.Equal("Quest Priest", view.ArchetypeKey);
        Assert.Equal([("P_A", 2, 2), ("P_B", 2, 0), ("P_C", 1, 0)], view.Rows.Select(r => (r.CardId, r.Copies, r.Played)));
        Assert.Empty(view.OffList);
    }

    /// <summary>AC4: Arena, Brawl, AI and friendly games have their own decks: no meta list there.</summary>
    [Theory]
    [InlineData("GT_ARENA")]
    [InlineData("GT_TAVERNBRAWL")]
    [InlineData("GT_VS_AI")]
    [InlineData("GT_VS_FRIEND")]
    public void No_list_outside_ranked_and_casual(string gameType) =>
        Assert.Null(Live(gameType, [.. OpponentPlays(60, "P_A")]).OpponentDeck);

    [Fact]
    public void No_list_before_the_first_card_played_from_the_deck() =>
        Assert.Null(Live("GT_RANKED", [.. Card(64, "P_D", "HAND", 2, "MINION")]).OpponentDeck);

    /// <summary>A game at turn 0 (starting decks), the opponent a Priest, then turn 5 and <paramref name="lines"/>.</summary>
    private static ConstructedView Live(string gameType, string[] lines)
    {
        var parser = new PowerParser(new DateOnly(2026, 11, 1));
        var tracker = new ConstructedTracker { Meta = Meta() };
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        string[] setup =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            .. Card(20, "HERO_09", "PLAY", 1, "HERO"),
            .. Card(21, "HERO_09", "PLAY", 2, "HERO"),
            $"DebugPrintGame() - GameType={gameType}",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            .. Card(99, "P_E", "HAND", 1, "SPELL"),
            .. Card(60, string.Empty, "DECK", 2, "SPELL"),
            .. Card(61, string.Empty, "DECK", 2, "SPELL"),
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=5 ",
        ];
        foreach (var line in setup.Concat(lines))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        parser.Flush();
        return tracker.BuildView(parser.Game!, Cards);
    }

    /// <summary>A starting-deck card drawn face down, then played (the reveal names it).</summary>
    private static string[] OpponentPlays(int id, string cardId) =>
    [
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id={id} zone=DECK zonePos=0 cardId= player=2] tag=ZONE value=HAND ",
        $"DebugPrintPower() - SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id={id} zone=HAND zonePos=1 cardId= player=2] CardID={cardId}",
        "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        Zone(id, cardId, "HAND", "PLAY"),
    ];

    private static string Zone(int id, string cardId, string from, string to) =>
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone={from} zonePos=1 cardId={cardId} player=2] tag=ZONE value={to} ";
}
