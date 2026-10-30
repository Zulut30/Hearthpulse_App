using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.Tests;

/// <summary>
/// Board damage, from synthetic Power.log lines written the way the real logs of October 2026 set the tags. A minion
/// played without Charge or Rush enters EXHAUSTED. A turn start clears EXHAUSTED for its controller only. An attack
/// raises NUM_ATTACKS_THIS_TURN, and the last attack sets EXHAUSTED. A Rush minion enters not EXHAUSTED but hits only
/// minions that turn.
/// </summary>
public sealed class BoardDamageTests
{
    private static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"HERO_09","dbfId":813,"name":"Anduin","type":"HERO","cardClass":"PRIEST"},
          {"id":"HERO_08","dbfId":637,"name":"Jaina","type":"HERO","cardClass":"MAGE"},
          {"id":"CORE_CS2_004","dbfId":120002,"name":"Power Word: Shield","type":"SPELL","cardClass":"PRIEST","cost":1},
          {"id":"M_A","dbfId":130001,"name":"Ready Minion","type":"MINION","cardClass":"NEUTRAL","cost":2},
          {"id":"W_A","dbfId":130002,"name":"Some Axe","type":"WEAPON","cardClass":"WARRIOR","cost":2}
        ]
        """u8.ToArray()));

    /// <summary>
    /// Player 1 (local, hero #20) and player 2 (hero #30); the game starts on turn 3, player 1 to move. Bodies are fed
    /// in order, so minions created before a TURN change entered play on an earlier turn.
    /// </summary>
    private static (GameState Game, ConstructedTracker Tracker) Play(params string[][] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 30));
        var tracker = new ConstructedTracker();
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        string[] setup =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=3",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
            "DebugPrintPower() -         tag=CONTROLLER value=1",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=20",
            "DebugPrintPower() -         tag=CURRENT_PLAYER value=1",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
            "DebugPrintPower() -         tag=CONTROLLER value=2",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -         tag=HERO_ENTITY value=30",
            "DebugPrintPower() -         tag=CURRENT_PLAYER value=0",
            .. Card(20, "HERO_09", "PLAY", 1, "HERO"),
            .. Card(30, "HERO_08", "PLAY", 2, "HERO"),
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            // A known card in hand tells the parser which player is local, as in every real game.
            .. Card(99, "CORE_CS2_004", "HAND", 1, "SPELL"),
        ];
        foreach (var line in setup.Concat(bodies.SelectMany(b => b)))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + line);
        }

        return (parser.Game!, tracker);
    }

    private static string[] Card(int id, string cardId, string zone, int controller, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -     tag=CARDTYPE value={type}",
        .. tags.Select(t => "DebugPrintPower() -     tag=" + t),
    ];

    /// <summary>A minion already on the board (created in play), with its attack and tags.</summary>
    private static string[] Minion(int id, int controller, int attack, params string[] tags) =>
        Card(id, "M_A", "PLAY", controller, "MINION", [$"ATK value={attack}", .. tags]);

    /// <summary>A minion played from hand now, as the game logs it: into play, then EXHAUSTED unless it has Charge or Rush.</summary>
    private static string[] Played(int id, int controller, int attack, params string[] tags)
    {
        var lines = new List<string>(Card(id, "M_A", "HAND", controller, "MINION", [$"ATK value={attack}", .. tags]))
        {
            $"DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId=M_A player={controller}] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
            $"DebugPrintPower() -     TAG_CHANGE Entity=[entityName=X id={id} zone=HAND zonePos=1 cardId=M_A player={controller}] tag=ZONE value=PLAY ",
        };
        if (!tags.Any(t => t.StartsWith("CHARGE", StringComparison.Ordinal) || t.StartsWith("RUSH", StringComparison.Ordinal)))
        {
            lines.Add(Tag(id, "EXHAUSTED", 1));
        }

        lines.Add("DebugPrintPower() - BLOCK_END");
        return [.. lines];
    }

    private static string Tag(int id, string tag, object value) =>
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=PLAY zonePos=1 cardId=M_A player=1] tag={tag} value={value} ";

    private static string Game(string tag, object value) => $"DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag={tag} value={value} ";

    /// <summary>The turn passes to <paramref name="next"/>: TURN, CURRENT_PLAYER, and EXHAUSTED and attack counts cleared as the game clears them.</summary>
    private static string[] NextTurn(int turn, int next, params int[] readied) =>
    [
        Game("TURN", turn),
        $"DebugPrintPower() - TAG_CHANGE Entity={(next == 1 ? 3 : 2)} tag=CURRENT_PLAYER value=0 ",
        $"DebugPrintPower() - TAG_CHANGE Entity={(next == 1 ? 2 : 3)} tag=CURRENT_PLAYER value=1 ",
        .. readied.SelectMany(id => new[] { Tag(id, "EXHAUSTED", 0), Tag(id, "NUM_ATTACKS_THIS_TURN", 0) }),
    ];

    private static BoardDamage.Result Damage((GameState Game, ConstructedTracker Tracker) state, int playerId) => BoardDamage.Of(state.Game, playerId);

    // Player 1's board on turn 3 (all ready, entered earlier) and player 2's, then turn 5 with player 1 to move again.
    private static readonly string[][] Board =
    [
        Minion(40, 1, 3),                                              // ready: 3
        Minion(41, 1, 4, "WINDFURY value=1"),                          // Windfury: 8
        Minion(42, 1, 5, "FROZEN value=1"),                            // Frozen: 0
        Minion(43, 1, 6, "CANT_ATTACK value=1"),                       // can't attack: 0
        Minion(44, 1, 2),                                              // attacks below: 0 left
        Minion(45, 1, 3, "WINDFURY value=1"),                          // attacks once below: 3 left
        Minion(46, 1, 1, "MEGA_WINDFURY value=1"),                     // Mega-Windfury: 4
        Minion(47, 1, 7, "DORMANT value=1"),                           // Dormant: 0
        Minion(48, 1, 2, "RUSH value=1"),                              // Rush, on the board since an earlier turn: 2
        Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=3"),
        Minion(70, 2, 3, "EXHAUSTED value=1"),                         // attacked on its last turn: 3 next turn
        Minion(71, 2, 2, "FROZEN value=1"),                            // frozen on our turn: stays frozen through its next
        Minion(72, 2, 4, "WINDFURY value=1"),                          // 8 next turn
        Card(73, "M_A", "HAND", 2, "MINION", "ATK value=9"),           // in hand: never counted
        Card(80, "W_A", "PLAY", 2, "WEAPON", "ATK value=2"),
        [Game("TURN", 5)],
        [Tag(20, "ATK", 3)],                                           // the weapon's attack on the hero
        [Tag(44, "NUM_ATTACKS_THIS_TURN", 1), Tag(44, "EXHAUSTED", 1)],
        [Tag(45, "NUM_ATTACKS_THIS_TURN", 1)],
        Played(50, 1, 2),                                              // summoned now: 0
        Played(51, 1, 4, "RUSH value=1"),                              // Rush on its first turn: minions only, 0
        Played(52, 1, 2, "CHARGE value=1"),                            // Charge: 2
    ];

    [Fact]
    public void The_player_to_move_sees_what_is_left_this_turn_and_the_other_what_their_board_deals_next_turn()
    {
        var state = Play(Board);

        var mine = Damage(state, 1);
        Assert.True(mine.ThisTurn);
        Assert.Equal(3 + 8 + 3 + 4 + 2 + 2, mine.Minions);
        Assert.Equal(3, mine.Hero);
        Assert.Equal(25, mine.Total);

        // The opponent's board as it will be on their turn: everything ready again except what stays frozen, the
        // weapon on the hero. The minion in hand is not on the board and not seen.
        var theirs = Damage(state, 2);
        Assert.False(theirs.ThisTurn);
        Assert.Equal(3 + 8, theirs.Minions);
        Assert.Equal(2, theirs.Hero);
    }

    [Fact]
    public void When_the_turn_passes_each_side_switches_between_this_turn_and_the_next()
    {
        // The opponent's turn: its turn start readies its board, its hero takes the weapon's attack.
        var state = Play([.. Board, NextTurn(6, 2, 70, 72), [Tag(30, "ATK", 2)]]);

        // Ours now counts for our next turn: every minion ready again, Rush and Charge alike, but not the frozen one,
        // the one that cannot attack or the dormant one.
        var mine = Damage(state, 1);
        Assert.False(mine.ThisTurn);
        Assert.Equal(3 + 8 + 2 + 6 + 4 + 2 + 2 + 4 + 2, mine.Minions);
        Assert.Equal(3, mine.Hero);

        var theirs = Damage(state, 2);
        Assert.True(theirs.ThisTurn);
        Assert.Equal(3 + 8, theirs.Minions);
        Assert.Equal(2, theirs.Hero);

        // They attack with the hero: no more hero damage this turn.
        var attacked = Play([.. Board, NextTurn(6, 2, 70, 72), [Tag(30, "ATK", 2), Tag(30, "NUM_ATTACKS_THIS_TURN", 1), Tag(30, "EXHAUSTED", 1)]]);
        Assert.Equal(0, Damage(attacked, 2).Hero);
    }

    [Fact]
    public void A_windfury_weapon_swings_twice_and_a_hero_buff_counts_only_on_its_turn()
    {
        // Our turn: a Windfury weapon of 2 and a hero buff of +1 this turn (hero ATK 3).
        var state = Play(
            Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=2", "WINDFURY value=1"),
            [Game("TURN", 5), Tag(20, "ATK", 3)]);
        Assert.Equal(6, Damage(state, 1).Hero);

        // One swing taken: one left.
        var once = Play(
            Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=2", "WINDFURY value=1"),
            [Game("TURN", 5), Tag(20, "ATK", 3), Tag(20, "NUM_ATTACKS_THIS_TURN", 1)]);
        Assert.Equal(3, Damage(once, 1).Hero);

        // Off our turn the buff has ended (ATK back to the weapon's): next turn is the weapon twice.
        var later = Play(
            Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=2", "WINDFURY value=1"),
            [Game("TURN", 5), Tag(20, "ATK", 3)],
            NextTurn(6, 2),
            [Tag(20, "ATK", 2)]);
        Assert.Equal(4, Damage(later, 1).Hero);

        // A frozen hero swings neither now nor on its next turn; no weapon and no buff is no hero damage.
        var frozen = Play(Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=2"), [Game("TURN", 5), Tag(20, "ATK", 2), Tag(20, "FROZEN", 1)]);
        Assert.Equal(0, Damage(frozen, 1).Hero);
        Assert.Equal(0, Damage(Play([Game("TURN", 5)]), 1).Hero);
    }

    [Fact]
    public void Mega_windfury_written_as_windfury_3_swings_four_times()
    {
        // No kept log had Mega-Windfury: whether the client writes MEGA_WINDFURY or WINDFURY=3, both count 4 attacks.
        Assert.Equal(8, Damage(Play(Minion(40, 1, 2, "WINDFURY value=3"), [Game("TURN", 5)]), 1).Minions);
        Assert.Equal(8, Damage(Play(Minion(40, 1, 2, "MEGA_WINDFURY value=1"), [Game("TURN", 5)]), 1).Minions);
        Assert.Equal(4, Damage(Play(Minion(40, 1, 2, "WINDFURY value=1"), [Game("TURN", 5)]), 1).Minions);
    }

    [Fact]
    public void After_a_reconnect_on_your_turn_the_board_still_counts()
    {
        // A reconnect replays the game from CREATE_GAME: every minion is created in play on the current turn. Those that
        // are not EXHAUSTED can attack; only a Rush minion created this turn (summoned by an effect, or seen again after
        // the reconnect) waits for its next turn.
        var state = Play([Game("TURN", 7)], Minion(40, 1, 3), Minion(41, 1, 4), Minion(42, 1, 5, "RUSH value=1"), Minion(43, 1, 6, "EXHAUSTED value=1"));
        Assert.Equal(7, Damage(state, 1).Minions);
    }

    [Fact]
    public void A_weapon_gives_its_attack_only_while_it_lasts()
    {
        // Our turn: a Windfury weapon of 3 with one use left (DURABILITY 2, DAMAGE 1): one swing of 3, then it breaks.
        var windfury = Play(
            Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=3", "DURABILITY value=2", "DAMAGE value=1", "WINDFURY value=1"),
            [Game("TURN", 5), Tag(20, "ATK", 3)]);
        Assert.Equal(3, Damage(windfury, 1).Hero);
        // The same weapon with both uses left swings twice.
        var full = Play(
            Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=3", "DURABILITY value=2", "WINDFURY value=1"),
            [Game("TURN", 5), Tag(20, "ATK", 3)]);
        Assert.Equal(6, Damage(full, 1).Hero);

        // Azshara (CATA_151) gives the hero Windfury; with a 4/1 weapon the second swing has no weapon: 4. With a +2
        // buff this turn as well (ATK 6): 6, then 2.
        var azshara = Play(Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=4", "DURABILITY value=1"), [Game("TURN", 5), Tag(20, "ATK", 4), Tag(20, "WINDFURY", 1)]);
        Assert.Equal(4, Damage(azshara, 1).Hero);
        var buffed = Play(Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=4", "DURABILITY value=1"), [Game("TURN", 5), Tag(20, "ATK", 6), Tag(20, "WINDFURY", 1)]);
        Assert.Equal(8, Damage(buffed, 1).Hero);

        // The opponent on their next turn: a Windfury weapon of 3 with one use left (DURABILITY 3, DAMAGE 2): 3.
        var theirs = Play(Card(80, "W_A", "PLAY", 2, "WEAPON", "ATK value=3", "DURABILITY value=3", "DAMAGE value=2", "WINDFURY value=1"));
        Assert.Equal(3, Damage(theirs, 2).Hero);
    }

    [Fact]
    public void Characters_that_cannot_attack_heroes_add_nothing()
    {
        // Air Support (CATA_564): Mega-Windfury, and it cannot attack heroes. Without the tag the same minion is 12.
        Assert.Equal(12, Damage(Play(Minion(40, 1, 3), [Game("TURN", 5), Tag(40, "MEGA_WINDFURY", 1)]), 1).Minions);
        var air = Play(Minion(40, 1, 3), [Game("TURN", 5), Tag(40, "MEGA_WINDFURY", 1), Tag(40, "CANNOT_ATTACK_HEROES", 1)]);
        Assert.Equal(0, Damage(air, 1).Minions);

        // Icehowl (AT_125): Charge, played this turn, and it cannot attack heroes.
        var charge = Play([Game("TURN", 5)], Played(41, 1, 10, "CHARGE value=1", "CANNOT_ATTACK_HEROES value=1"));
        Assert.Equal(0, Damage(charge, 1).Minions);

        // A weapon that cannot attack heroes (END_012): the hero adds nothing with it, this turn or the next.
        var weapon = Play(
            Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=3", "DURABILITY value=3", "CANNOT_ATTACK_HEROES value=1"),
            [Game("TURN", 5), Tag(20, "ATK", 3)]);
        Assert.Equal(0, Damage(weapon, 1).Hero);
        var later = Play(
            Card(60, "W_A", "PLAY", 1, "WEAPON", "ATK value=3", "DURABILITY value=3", "CANNOT_ATTACK_HEROES value=1"),
            [Game("TURN", 5)], NextTurn(6, 2));
        Assert.Equal(0, Damage(later, 1).Hero);
    }

    [Fact]
    public void The_counter_shows_both_sides_with_the_parts_and_the_attackers()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var state = Play(Board);
        var view = state.Tracker.BuildView(state.Game, Cards);

        var mine = view.Counters.Single(c => c.Id == "board-damage" && !c.Opponent);
        Assert.Equal("25", mine.Value);
        Assert.Equal("Board damage", mine.Title);
        Assert.True(mine.Relevant);
        Assert.Equal(new[] { "This turn: minions 22, hero 3" }, mine.Details);
        // Six minions that can still hit the hero, then the weapon.
        Assert.Equal(6, mine.Counted.Single(c => c.CardId == "M_A").Count);
        Assert.Equal(1, mine.Counted.Single(c => c.CardId == "W_A").Count);

        var theirs = view.Counters.Single(c => c.Id == "board-damage" && c.Opponent);
        Assert.Equal("13", theirs.Value);
        Assert.Equal(new[] { "Next turn: minions 11, hero 2" }, theirs.Details);

        // An empty board: zero, and not shown in Auto.
        var empty = Play([Game("TURN", 5)]);
        var none = empty.Tracker.BuildView(empty.Game, Cards).Counters.Single(c => c.Id == "board-damage" && !c.Opponent);
        Assert.Equal("0", none.Value);
        Assert.False(none.Relevant);
        Assert.Contains(Counters.All, c => c.Id == "board-damage");
    }
}
