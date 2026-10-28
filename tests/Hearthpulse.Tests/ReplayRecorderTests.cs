using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>
/// The match replay recorder writes only what the local player could see: opponent hand and both decks are card backs,
/// a card crossing a hidden zone gets a new replay id, no creator of a hidden card. Negative tests first.
/// </summary>
public sealed class ReplayRecorderTests
{
    /// <summary>
    /// A Ranked game, local player 1 (velox) against player 2. <paramref name="bodies"/> run after the game has reached
    /// turn 5; returns the recorded events.
    /// </summary>
    internal static IReadOnlyList<string> Record(params string[] bodies) => Record(out _, bodies);

    internal static IReadOnlyList<string> Record(out ReplayRecorder recorder, params string[] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        recorder = new ReplayRecorder();
        parser.AddListener(recorder);
        Feed(parser, Setup.Concat(bodies));
        parser.Flush();
        return recorder.Events;
    }

    /// <summary>Feeds GameState lines (without their prefix) to a parser.</summary>
    internal static void Feed(PowerParser parser, IEnumerable<string> bodies)
    {
        foreach (var body in bodies)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }
    }

    /// <summary>The game <see cref="Record(string[])"/> starts from: a Ranked game at turn 5, local player 1.</summary>
    internal static string[] Setup =>
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
            "DebugPrintPower() -         tag=HERO_ENTITY value=21",
            .. Card(20, "HERO_09", "PLAY", 1, "HERO", "tag=HEALTH value=30"),
            .. Card(21, "HERO_08", "PLAY", 2, "HERO", "tag=HEALTH value=30"),
            .. Card(40, "", "DECK", 1, ""),
            .. Card(50, "", "DECK", 2, ""),
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - FormatType=FT_STANDARD",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=UNKNOWN HUMAN PLAYER",
            .. Card(99, "CORE_CS2_004", "HAND", 1, "SPELL"),
            "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=MULLIGAN_STATE value=DONE ",
            "DebugPrintPower() - TAG_CHANGE Entity=2 tag=CURRENT_PLAYER value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=5 ",
        ];

    internal static string[] Card(int id, string cardId, string zone, int controller, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        .. type.Length > 0 ? [$"DebugPrintPower() -     tag=CARDTYPE value={type}"] : Array.Empty<string>(),
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    internal static string Zone(int id, string value, string cardId = "", int player = 2) =>
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=X zonePos=0 cardId={cardId} player={player}] tag=ZONE value={value} ";

    internal static string Tag(int id, string tag, string value, int player = 2) =>
        $"DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id={id} zone=X zonePos=0 cardId= player={player}] tag={tag} value={value} ";

    internal static string[] Show(int id, string cardId, params string[] tags) =>
    [
        $"DebugPrintPower() - SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id={id} zone=HAND zonePos=1 cardId= player=2] CardID={cardId}",
        .. tags.Select(t => "DebugPrintPower() -     " + t),
    ];

    /// <summary>The replay id an E line gives (second token).</summary>
    private static string IdOf(string line) => line.Split(' ')[1];

    [Fact]
    public void Opponent_draw_stays_a_card_back_even_when_the_log_names_it()
    {
        var events = Record(
            [
                .. Card(60, "BE_014", "DECK", 2, "MINION", "tag=CREATOR_DBID value=1234", "tag=ATK value=4"),
                Zone(60, "HAND", "BE_014"),
            ]);

        ReplayChecks.AssertNothingHidden(events);
        Assert.DoesNotContain(events, e => e.Contains("BE_014", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("CREATOR", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" O HAND ") && e.EndsWith(" - - ORIGIN=drawn"));
    }

    [Fact]
    public void A_card_revealed_in_their_hand_stays_a_card_back()
    {
        // SHOW_ENTITY in hand (a log leak path): the card is still in the opponent's hand, so still a card back.
        var events = Record(
            [
                .. Card(61, "", "DECK", 2, ""),
                Zone(61, "HAND"),
                .. Show(61, "CS2_029", "tag=CARDTYPE value=SPELL", "tag=COST value=4"),
                Tag(61, "ZONE_POSITION", "2"),
            ]);

        ReplayChecks.AssertNothingHidden(events);
        Assert.DoesNotContain(events, e => e.Contains("CS2_029", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.StartsWith("V ", StringComparison.Ordinal) && e.Contains("COST"));
    }

    [Fact]
    public void A_played_card_gets_a_new_id_so_the_card_back_cannot_be_linked()
    {
        var events = Record(
            [
                .. Card(62, "", "DECK", 2, ""),
                Zone(62, "HAND"),
                "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=62 zone=HAND zonePos=1 cardId= player=2] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
                .. Show(62, "CS2_182", "tag=CARDTYPE value=MINION", "tag=ATK value=4", "tag=HEALTH value=5"),
                Zone(62, "PLAY", "CS2_182"),
                "DebugPrintPower() - BLOCK_END",
            ]);

        ReplayChecks.AssertNothingHidden(events);
        var inHand = events.Last(e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" O HAND "));
        var inPlay = Assert.Single(events, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" O PLAY ") && e.Contains("CS2_182"));
        Assert.NotEqual(IdOf(inHand), IdOf(inPlay));
        Assert.Contains($"X {IdOf(inHand)}", events);
        Assert.Contains($"P {IdOf(inPlay)} -", events);
        Assert.Contains("ATK=4", inPlay);
    }

    /// <summary>
    /// Their card <paramref name="sentBack"/> goes back into the deck (a mulligan, a trade); then they draw
    /// <paramref name="drawn"/> and play it. Whether the drawn card is the one sent back or another must not show.
    /// </summary>
    private static IReadOnlyList<string> SendBackThenDraw(int drawn) => Record(
    [
        .. Card(63, "", "HAND", 2, ""),
        .. Card(64, "", "DECK", 2, ""),
        Zone(63, "DECK"),
        Zone(drawn, "HAND"),
        string.Format(PlayBlock, drawn),
        .. Show(drawn, "TIME_700", "tag=CARDTYPE value=MINION", "tag=ATK value=3", "tag=HEALTH value=3"),
        Zone(drawn, "PLAY", "TIME_700"),
        "DebugPrintPower() - BLOCK_END",
    ]);

    private const string PlayBlock =
        "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id={0} zone=HAND zonePos=1 cardId= player=2] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ";

    [Fact]
    public void A_card_sent_back_into_their_deck_cannot_be_told_from_the_others_when_drawn()
    {
        // Review blocker: deck cards had ids, so "X <its deck id>" said the redrawn card was the one sent back.
        var redrawn = SendBackThenDraw(63);
        var other = SendBackThenDraw(64);

        ReplayChecks.AssertNothingHidden(redrawn);
        ReplayChecks.AssertNothingHidden(other);
        Assert.Equal(other, redrawn);
        Assert.DoesNotContain(redrawn, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" DECK "));
        Assert.Contains("D O 3", redrawn);
    }

    [Fact]
    public void A_card_shuffled_into_their_deck_cannot_be_told_from_the_others_when_drawn()
    {
        // Our known minion is shuffled into their deck; they draw it, or another card. The files must be the same.
        IReadOnlyList<string> Draw(int drawn) => Record(
        [
            .. Card(63, "BE_012", "PLAY", 1, "MINION", "tag=ATK value=3"),
            .. Card(64, "", "DECK", 2, ""),
            "DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=63 zone=PLAY zonePos=1 cardId=BE_012 player=1] tag=CONTROLLER value=2 ",
            Zone(63, "DECK", "BE_012"),
            Zone(drawn, "HAND"),
        ]);

        var shuffled = Draw(63);
        ReplayChecks.AssertNothingHidden(shuffled);
        Assert.Equal(Draw(64), shuffled);
        Assert.All(shuffled.Where(e => e.Contains("BE_012", StringComparison.Ordinal)), e => Assert.Contains(" PLAY ", e));
        Assert.Contains(shuffled, e => e.Contains(" O HAND ") && e.EndsWith("ORIGIN=drawn"));
    }

    [Fact]
    public void The_opponents_choices_are_not_recorded()
    {
        var events = Record(
            [
                .. Card(70, "TLC_515", "SETASIDE", 2, "SPELL"),
                .. Card(71, "CS2_029", "SETASIDE", 2, "SPELL"),
                "DebugPrintEntityChoices() - id=4 Player=UNKNOWN HUMAN PLAYER TaskList=12 ChoiceType=GENERAL CountMin=1 CountMax=1",
                "DebugPrintEntityChoices() -   Entities[0]=[entityName=A id=70 zone=SETASIDE zonePos=0 cardId=TLC_515 player=2]",
                "DebugPrintEntityChoices() -   Entities[1]=[entityName=B id=71 zone=SETASIDE zonePos=0 cardId=CS2_029 player=2]",
                "DebugPrintEntitiesChosen() - id=4 Player=UNKNOWN HUMAN PLAYER EntitiesCount=1",
                "DebugPrintEntitiesChosen() -   Entities[0]=[entityName=B id=71 zone=SETASIDE zonePos=0 cardId=CS2_029 player=2]",
                Zone(71, "HAND", "CS2_029"),
            ]);

        ReplayChecks.AssertNothingHidden(events);
        Assert.DoesNotContain(events, e => e.StartsWith("C ", StringComparison.Ordinal) || e.StartsWith("c ", StringComparison.Ordinal));
        Assert.DoesNotContain(events, e => e.Contains("TLC_515", StringComparison.Ordinal) || e.Contains("CS2_029", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Contains(" O HAND ") && e.EndsWith("ORIGIN=created"));
    }

    [Fact]
    public void A_card_changed_in_their_hand_stays_a_card_back()
    {
        var events = Record(
            [
                .. Card(65, "", "DECK", 2, ""),
                Zone(65, "HAND"),
                "DebugPrintPower() - CHANGE_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=65 zone=HAND zonePos=1 cardId= player=2] CardID=CS2_032",
                "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
                "DebugPrintPower() -     tag=COST value=7",
            ]);

        ReplayChecks.AssertNothingHidden(events);
        Assert.DoesNotContain(events, e => e.Contains("CS2_032", StringComparison.Ordinal));
    }

    [Fact]
    public void Their_minion_returned_to_hand_and_hidden_is_a_card_back()
    {
        var events = Record(
            [
                .. Card(66, "CS2_120", "PLAY", 2, "MINION", "tag=ATK value=2", "tag=HEALTH value=3"),
                "DebugPrintPower() - HIDE_ENTITY - Entity=[entityName=X id=66 zone=PLAY zonePos=1 cardId=CS2_120 player=2] tag=ZONE value=HAND",
            ]);

        ReplayChecks.AssertNothingHidden(events);
        var back = Assert.Single(events, e => e.Contains(" O HAND ", StringComparison.Ordinal));
        Assert.EndsWith(" - - ORIGIN=returned", back);
    }

    [Fact]
    public void Their_secret_typed_before_its_secret_tag_is_never_named()
    {
        // ZONE=SECRET can come before SECRET=1 while the card type is already known.
        var events = Record(
            [
                .. Card(67, "", "DECK", 2, ""),
                Zone(67, "HAND"),
                .. Show(67, "EX1_130", "tag=CARDTYPE value=SPELL"),
                Zone(67, "SECRET", "EX1_130"),
                Tag(67, "SECRET", "1"),
            ]);

        Assert.DoesNotContain(events, e => e.Contains("EX1_130", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_but_the_result_is_recorded_after_the_game_is_over()
    {
        var events = Record(
            [
                .. Card(68, "", "DECK", 2, ""),
                "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=FINAL_GAMEOVER ",
                .. Card(69, "CS2_182", "PLAY", 2, "MINION"),
                Zone(68, "HAND"),
                "DebugPrintPower() - TAG_CHANGE Entity=velox#21276 tag=PLAYSTATE value=WON ",
                "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE ",
            ]);

        var after = events.SkipWhile(e => e != "S FINAL_GAMEOVER").Skip(1).ToList();
        Assert.Equal(["R WON"], after);
    }

    [Fact]
    public void A_game_whose_local_player_looks_wrong_is_not_recorded()
    {
        // The local player is taken from the first choice header's name, but the mulligan it offers is the other
        // player's hand: the filter would be the wrong way round, so nothing is written.
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var recorder = new ReplayRecorder();
        parser.AddListener(recorder);
        string[] lines =
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=1 lo=11]",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=1 lo=22]",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            .. Card(30, "", "HAND", 2, ""),
            .. Card(40, "", "HAND", 1, ""),
            "DebugPrintGame() - GameType=GT_RANKED",
            "DebugPrintGame() - PlayerID=1, PlayerName=velox#21276",
            "DebugPrintGame() - PlayerID=2, PlayerName=Other#1",
            "DebugPrintEntityChoices() - id=1 Player=velox#21276 TaskList=1 ChoiceType=MULLIGAN CountMin=0 CountMax=3",
            "DebugPrintEntityChoices() -   Entities[0]=[entityName=A id=30 zone=HAND zonePos=1 cardId=CS2_029 player=2]",
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=1 ",
        ];
        foreach (var body in lines)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
        Assert.Equal(1, parser.Game!.LocalPlayerId);
        Assert.False(recorder.HasGame);
        Assert.Empty(recorder.Events);
    }

    [Fact]
    public void Their_secret_is_a_card_back_until_it_is_revealed()
    {
        var events = Record(
            [
                .. Card(64, "", "DECK", 2, ""),
                Zone(64, "HAND"),
                .. Show(64, "EX1_130", "tag=CARDTYPE value=SPELL", "tag=SECRET value=1"),
                Zone(64, "SECRET", "EX1_130"),
            ]);

        ReplayChecks.AssertNothingHidden(events);
        Assert.DoesNotContain(events, e => e.Contains("EX1_130", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" O SECRET 0 - -"));

        var revealed = Record(
            [
                .. Card(64, "", "DECK", 2, ""),
                Zone(64, "HAND"),
                .. Show(64, "EX1_130", "tag=CARDTYPE value=SPELL", "tag=SECRET value=1"),
                Zone(64, "SECRET", "EX1_130"),
                Zone(64, "GRAVEYARD", "EX1_130"),
            ]);
        Assert.Contains(revealed, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" O GRAVEYARD ") && e.Contains("EX1_130"));
    }

    [Fact]
    public void Our_draws_are_face_up_and_our_deck_is_not()
    {
        var events = Record(
            [
                .. Card(65, "", "DECK", 1, ""),
                "DebugPrintPower() - SHOW_ENTITY - Updating Entity=[entityName=UNKNOWN ENTITY [cardType=INVALID] id=65 zone=DECK zonePos=0 cardId= player=1] CardID=CORE_CS2_004",
                "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
                "DebugPrintPower() -     tag=COST value=1",
                Zone(65, "HAND", "CORE_CS2_004", player: 1),
            ]);

        ReplayChecks.AssertNothingHidden(events);
        Assert.Contains(events, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" L HAND ") && e.Contains("CORE_CS2_004 SPELL COST=1"));
        Assert.All(events.Where(e => e.Contains(" L DECK ")), e => Assert.Contains(" - -", e));
    }

    [Fact]
    public void Attacks_plays_and_choices_are_recorded()
    {
        var events = Record(
            [
                .. Card(66, "BE_012", "PLAY", 1, "MINION", "tag=ATK value=3", "tag=HEALTH value=2"),
                "DebugPrintPower() - BLOCK_START BlockType=ATTACK Entity=[entityName=X id=66 zone=PLAY zonePos=1 cardId=BE_012 player=1] EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=[entityName=Y id=21 zone=PLAY zonePos=0 cardId=HERO_08 player=2] SubOption=-1 ",
                "DebugPrintPower() -     TAG_CHANGE Entity=GameEntity tag=PROPOSED_DEFENDER value=21 ",
                "DebugPrintPower() -     TAG_CHANGE Entity=[entityName=Y id=21 zone=PLAY zonePos=0 cardId=HERO_08 player=2] tag=DAMAGE value=3 ",
                "DebugPrintPower() - BLOCK_END",
                .. Card(70, "TLC_515", "SETASIDE", 1, "SPELL"),
                .. Card(71, "CS2_029", "SETASIDE", 1, "SPELL"),
                "DebugPrintEntityChoices() - id=3 Player=velox#21276 TaskList=12 ChoiceType=GENERAL CountMin=1 CountMax=1",
                "DebugPrintEntityChoices() -   Source=[entityName=X id=66 zone=PLAY zonePos=1 cardId=BE_012 player=1]",
                "DebugPrintEntityChoices() -   Entities[0]=[entityName=A id=70 zone=SETASIDE zonePos=0 cardId=TLC_515 player=1]",
                "DebugPrintEntityChoices() -   Entities[1]=[entityName=B id=71 zone=SETASIDE zonePos=0 cardId=CS2_029 player=1]",
                "DebugPrintEntitiesChosen() - id=3 Player=velox#21276 EntitiesCount=1",
                "DebugPrintEntitiesChosen() -   Entities[0]=[entityName=B id=71 zone=SETASIDE zonePos=0 cardId=CS2_029 player=1]",
            ]);

        ReplayChecks.AssertNothingHidden(events);
        var attacker = IdOf(events.Single(e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains("BE_012")));
        var hero = IdOf(events.Single(e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains("HERO_08")));
        Assert.Contains($"A {attacker} {hero}", events);
        Assert.Contains($"V {hero} DAMAGE 3", events);
        Assert.Contains("C GENERAL BE_012 TLC_515,CS2_029", events);
        Assert.Contains("c CS2_029", events);
        // Set-aside choice cards are never on the table.
        Assert.DoesNotContain(events, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains("SETASIDE"));
    }

    [Fact]
    public void Battlegrounds_games_are_not_recorded_as_events()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var recorder = new ReplayRecorder();
        parser.AddListener(recorder);
        foreach (var body in new[]
                 {
                     "DebugPrintPower() - CREATE_GAME",
                     "DebugPrintPower() -     GameEntity EntityID=1",
                     "DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=1 lo=11]",
                     "DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=0 lo=0]",
                     "DebugPrintGame() - GameType=GT_BATTLEGROUNDS",
                     "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=1 ",
                 })
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
        Assert.False(recorder.HasGame);
        Assert.Empty(recorder.Events);
    }
}

/// <summary>Checks that a recorded event stream holds nothing the local player could not see.</summary>
internal static class ReplayChecks
{
    public static void AssertNothingHidden(IEnumerable<string> events)
    {
        // Replay id -> (zone, side, hidden); ids are never reused once removed.
        var live = new Dictionary<string, (string Zone, string Side, bool Hidden)>(StringComparer.Ordinal);
        var dead = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in events)
        {
            var t = line.Split(' ');
            Assert.DoesNotContain("CREATOR", line, StringComparison.Ordinal);
            switch (t[0])
            {
                case "E":
                    Assert.True(t.Length >= 7, line);
                    var (id, side, zone, card, type) = (t[1], t[2], t[3], t[5], t[6]);
                    Assert.DoesNotContain(id, dead);
                    // A deck is only a count (D lines): an id in it would link what went in with what is drawn later.
                    Assert.True(zone != "DECK", $"a card in a deck has an id: {line}");
                    var hidden = zone == "DECK" || (side == "O" && zone == "HAND") || (side == "O" && zone == "SECRET" && card == "-");
                    if (zone == "DECK" || (side == "O" && zone == "HAND"))
                    {
                        Assert.True(card == "-" && type == "-", $"hidden card named: {line}");
                        Assert.True(t.Skip(7).All(x => x.StartsWith("ORIGIN=", StringComparison.Ordinal)), $"hidden card has tags: {line}");
                    }

                    if (live.TryGetValue(id, out var before))
                    {
                        // The same id only for a face-up card staying face up.
                        Assert.False(before.Hidden || hidden, $"id {id} crossed a hidden zone: {line}");
                    }

                    live[id] = (zone, side, hidden);
                    break;
                case "X":
                    Assert.True(live.Remove(t[1]), line);
                    dead.Add(t[1]);
                    break;
                case "D":
                    Assert.True(t.Length == 3 && t[1] is "L" or "O" && int.TryParse(t[2], out _), line);
                    break;
                case "V" when t[1] is not ("L" or "O"):
                    Assert.True(live.TryGetValue(t[1], out var target) && !target.Hidden, $"tag of a hidden card: {line}");
                    break;
                case "Z":
                    Assert.True(live.ContainsKey(t[1]), line);
                    break;
                case "P" or "U" or "A":
                    Assert.True(t[2] == "-" || (live.TryGetValue(t[2], out var aimed) && !aimed.Hidden), $"hidden target: {line}");
                    break;
            }
        }
    }
}
