using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Cards that look back at this game ("a friendly minion that died this game", "spells you've cast this game"): the rule
/// read from the English card text (texts of the card-pool fixture, October 2026) and the list of this game's cards
/// built from what the tracker recorded in a Power.log.
/// </summary>
public sealed class MatchListTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"JAIL_940","dbfId":130001,"name":"Undeath Sentence","type":"SPELL","cardClass":"DEATHKNIGHT","cost":2,
           "text":"Trigger the <b>Deathrattle</b> of a random friendly minion that died this game."},
          {"id":"DR_1","dbfId":130002,"name":"Loot Hoarder","type":"MINION","cardClass":"NEUTRAL","cost":2,"rarity":"COMMON","mechanics":["DEATHRATTLE"]},
          {"id":"DR_2","dbfId":130003,"name":"Abomination","type":"MINION","cardClass":"NEUTRAL","cost":5,"rarity":"RARE","mechanics":["DEATHRATTLE","TAUNT"]},
          {"id":"PLAIN","dbfId":130004,"name":"Chillwind Yeti","type":"MINION","cardClass":"NEUTRAL","cost":4,"rarity":"COMMON"},
          {"id":"UNDEAD","dbfId":130005,"name":"Risen Groundskeeper","type":"MINION","cardClass":"NEUTRAL","cost":3,"races":["UNDEAD"]},
          {"id":"FIRE","dbfId":130006,"name":"Fireball","type":"SPELL","cardClass":"MAGE","cost":4},
          {"id":"FEL","dbfId":130007,"name":"Fel Barrage","type":"SPELL","cardClass":"DEMONHUNTER","cost":2}
        ]
        """u8.ToArray()));

    [Theory]
    // The owner's example: Undeath Sentence ("Посмертный приговор").
    [InlineData("Trigger the Deathrattle of a random friendly minion that died this game.", MatchEvent.Died, MatchSide.Friendly, "DEATHRATTLE", null, null, "MINION", null)]
    [InlineData("Battlecry: Trigger the Deathrattles of 5 friendly minions that died this game.", MatchEvent.Died, MatchSide.Friendly, "DEATHRATTLE", null, null, "MINION", null)]
    [InlineData("Battlecry: Resurrect your highest-Cost minion that died this game.", MatchEvent.Died, MatchSide.Friendly, null, null, null, "MINION", null)]
    [InlineData("Summon the highest Cost friendly Undead that died this game.", MatchEvent.Died, MatchSide.Friendly, null, "UNDEAD", null, "MINION", null)]
    [InlineData("Resurrect 2 friendly Taunt minions.", MatchEvent.Died, MatchSide.Friendly, "TAUNT", null, null, "MINION", null)]
    [InlineData("Resurrect a 1, 2, and 3-Cost minion. Give them Reborn.", MatchEvent.Died, MatchSide.Friendly, null, null, null, "MINION", null)]
    [InlineData("Summon a copy of 3 minions that died this game.", MatchEvent.Died, MatchSide.Both, null, null, null, "MINION", null)]
    [InlineData("Deal 2 damage for each enemy minion that died this game.", MatchEvent.Died, MatchSide.Enemy, null, null, null, "MINION", null)]
    [InlineData("Costs (1) less for each Fel spell you've cast this game.", MatchEvent.Cast, MatchSide.Friendly, null, null, 7, "SPELL", null)]
    [InlineData("Summon each 1-Cost minion you've played this game.", MatchEvent.Played, MatchSide.Friendly, null, null, null, "MINION", 1)]
    [InlineData("Old God Cost (1) less for each Deathrattle minion you've summoned this game.", MatchEvent.Summoned, MatchSide.Friendly, "DEATHRATTLE", null, null, "MINION", null)]
    [InlineData("Deathrattle: Draw another copy of cards you've played this game. ( (card,cards))", MatchEvent.Played, MatchSide.Friendly, null, null, null, null, null)]
    public void A_card_text_names_what_it_looks_back_at(string text, MatchEvent kind, MatchSide side, string? mechanic, string? race, int? school, string? type, int? cost) =>
        Assert.Equal(new MatchRule(kind, side, mechanic, race, school, type, cost), MatchLists.Parse(text));

    [Theory]
    // Counts the card itself, a name that is no minion type, a condition the list cannot check, nothing this game.
    [InlineData("Taunt. Deathrattle: Summon a random Dragon for each time Ysondre has died this game.", "Ysondre")]
    [InlineData("Summon two 2/2 Treants. They gain +1/+1 for each friendly Treant that died this game. ( )", "Splintered Reality")]
    [InlineData("Taunt. Costs (1) less for each card you played this game that didn't start in your deck.", "Techysaurus")]
    [InlineData("Battlecry: If 5 friendly minions died this turn, your Rush minions have Charge this game.", "Broodqueen Sha'kar")]
    [InlineData("Discover a minion. Deal 3 damage.", "Some Card")]
    public void Other_texts_have_no_list(string text, string name) => Assert.Null(MatchLists.Parse(text, name));

    [Fact]
    public void Undeath_sentence_lists_your_dead_deathrattle_minions_with_their_count_and_nothing_hidden()
    {
        var previous = Loc.Instance.Language;
        try
        {
            Loc.Instance.SetLanguage(Loc.English);
            var game = Play(
                [
                    .. Minion(40, "DR_1", 1), .. Die(40, "DR_1", 1),
                    .. Minion(41, "DR_1", 1), .. Die(41, "DR_1", 1),
                    .. Minion(42, "DR_2", 1), .. Die(42, "DR_2", 1),
                    .. Minion(43, "PLAIN", 1), .. Die(43, "PLAIN", 1),
                    // The opponent's Deathrattle minion died too: not "friendly".
                    .. Minion(50, "DR_2", 2), .. Die(50, "DR_2", 2),
                    // Still alive: not in the list.
                    .. Minion(44, "DR_1", 1),
                ], out var tracker);

            var rule = MatchLists.Parse(Cards.Get("JAIL_940")!.Text, "Undeath Sentence")!;
            var list = MatchLists.Build("JAIL_940", rule, game, tracker.Counters, Cards);

            Assert.Equal("Undeath Sentence", list.SourceName);
            Assert.Equal("Your minions that died · Deathrattle", list.Title);
            Assert.Equal(3, list.Total);
            Assert.Equal([("DR_1", "Loot Hoarder", 2, 2), ("DR_2", "Abomination", 5, 1)], list.Rows.Select(r => (r.CardId, r.Name, r.Cost, r.Count)));

            // Every minion that died (both sides), and only the opponent's.
            Assert.Equal(5, MatchLists.Build("X", new MatchRule(MatchEvent.Died, MatchSide.Both, CardType: "MINION"), game, tracker.Counters, Cards).Total);
            Assert.Equal(["DR_2"], MatchLists.Build("X", new MatchRule(MatchEvent.Died, MatchSide.Enemy, CardType: "MINION"), game, tracker.Counters, Cards).Rows.Select(r => r.CardId));
        }
        finally
        {
            Loc.Instance.SetLanguage(previous);
        }
    }

    [Fact]
    public void Spells_you_cast_count_by_school_and_minion_types_by_race()
    {
        var game = Play(
            [
                .. Cast(60, "FIRE", 1, "SPELL", "tag=SPELL_SCHOOL value=2"),
                .. Cast(61, "FEL", 1, "SPELL", "tag=SPELL_SCHOOL value=7"),
                .. Cast(62, "FEL", 1, "SPELL", "tag=SPELL_SCHOOL value=7"),
                // The opponent's spell is not "you've cast".
                .. Cast(70, "FEL", 2, "SPELL", "tag=SPELL_SCHOOL value=7"),
                .. Minion(45, "UNDEAD", 1), .. Die(45, "UNDEAD", 1),
                .. Minion(46, "PLAIN", 1), .. Die(46, "PLAIN", 1),
            ], out var tracker);

        var fel = MatchLists.Build("X", MatchLists.Parse("Costs (1) less for each Fel spell you've cast this game.")!, game, tracker.Counters, Cards);
        Assert.Equal([("FEL", 2)], fel.Rows.Select(r => (r.CardId, r.Count)));
        var spells = MatchLists.Build("X", new MatchRule(MatchEvent.Cast, MatchSide.Friendly, CardType: "SPELL"), game, tracker.Counters, Cards);
        Assert.Equal(3, spells.Total);
        var undead = MatchLists.Build("X", MatchLists.Parse("Summon the highest Cost friendly Undead that died this game.")!, game, tracker.Counters, Cards);
        Assert.Equal(["UNDEAD"], undead.Rows.Select(r => r.CardId));
    }

    [Fact]
    public void The_list_follows_the_hovered_card_in_your_hand_and_stands_beside_it()
    {
        var game = Play([.. Minion(40, "DR_1", 1)], out _);
        var hover = new CardPoolTracker();

        hover.SetHovered("JAIL_940", DateTime.UtcNow);
        Assert.Equal("JAIL_940", hover.HoveredInHand(game));
        Assert.Equal(new CardPoolAnchor(PoolAnchorZone.Hand, 0, 1), CardPoolTracker.HandAnchor(game, "JAIL_940"));

        // A card on the board is not in hand: no list for it.
        hover.SetHovered("DR_1", DateTime.UtcNow);
        Assert.Null(hover.HoveredInHand(game));
        Assert.Null(CardPoolTracker.HandAnchor(game, "DR_1"));
    }

    private static GameState Play(string[] bodies, out ConstructedTracker tracker)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 28));
        tracker = new ConstructedTracker();
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        string[] setup =
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
            "DebugPrintPower() - FULL_ENTITY - Creating ID=20 CardID=HERO_09",
            "DebugPrintPower() -     tag=ZONE value=PLAY",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            // A known card in hand tells the parser which player is local, as in every real game.
            "DebugPrintPower() - FULL_ENTITY - Creating ID=99 CardID=JAIL_940",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=1",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
        ];
        foreach (var body in setup.Concat(bodies))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
        return parser.Game!;
    }

    private static string[] Minion(int id, string cardId, int controller) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        "DebugPrintPower() -     tag=ZONE value=PLAY",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        "DebugPrintPower() -     tag=CARDTYPE value=MINION",
    ];

    private static string[] Die(int id, string cardId, int controller) =>
        [$"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=PLAY zonePos=1 cardId={cardId} player={controller}] tag=ZONE value=GRAVEYARD "];

    private static string[] Cast(int id, string cardId, int controller, string type, string tag) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        "DebugPrintPower() -     tag=ZONE value=HAND",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -     tag=CARDTYPE value={type}",
        "DebugPrintPower() -     " + tag,
        $"DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId={cardId} player={controller}] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
        $"DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId={cardId} player={controller}] tag=ZONE value=PLAY ",
        "DebugPrintPower() - BLOCK_END",
    ];
}
