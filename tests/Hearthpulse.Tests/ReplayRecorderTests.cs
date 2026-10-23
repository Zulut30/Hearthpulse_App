using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Xunit;

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
        foreach (var body in setup.Concat(bodies))
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
        return recorder.Events;
    }

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

    [Fact]
    public void A_card_shuffled_into_a_deck_loses_its_identity()
    {
        // Our minion is shuffled into their deck, then drawn: nothing ties the card back to it.
        var events = Record(
            [
                .. Card(63, "BE_012", "PLAY", 1, "MINION", "tag=ATK value=3"),
                "DebugPrintPower() - TAG_CHANGE Entity=[entityName=X id=63 zone=PLAY zonePos=1 cardId=BE_012 player=1] tag=CONTROLLER value=2 ",
                Zone(63, "DECK", "BE_012"),
                Zone(63, "HAND", "BE_012"),
            ]);

        ReplayChecks.AssertNothingHidden(events);
        var onTable = IdOf(events.Last(e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains("BE_012")));
        var hidden = events.Where(e => e.StartsWith("E ", StringComparison.Ordinal) && (e.Contains(" O DECK ") || e.Contains(" O HAND ")))
            .Select(IdOf).ToList();
        Assert.DoesNotContain(onTable, hidden);
        Assert.Equal(hidden.Count, hidden.Distinct().Count());
        Assert.All(events.Where(e => e.Contains("BE_012", StringComparison.Ordinal)), e => Assert.Contains(" PLAY ", e));
        Assert.Contains(events, e => e.Contains(" O HAND ") && e.EndsWith("ORIGIN=drawn"));
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
