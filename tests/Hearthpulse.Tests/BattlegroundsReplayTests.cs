using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// The Battlegrounds step-by-step recording (format 4): one step per tavern action and per combat block, only what the
/// player saw, the place at the end. Synthetic lines follow the shapes of the 28.10 log; the fixture and the kept log
/// check the real order.
/// </summary>
public sealed class BattlegroundsReplayTests(ITestOutputHelper output)
{
    internal const int Me = 1;
    internal const int Bob = 9;
    internal const string RealLog = @"D:\Hearthstone\Logs\Hearthstone_2026_10_28_00_19_35\Power.log";

    /// <summary>A Battlegrounds game on turn 1: our hero and hero power, a lobby hero, Bob's buttons and offer, 3 gold.</summary>
    internal static readonly string[] Setup =
    [
        "DebugPrintPower() - CREATE_GAME",
        "DebugPrintPower() -     GameEntity EntityID=1",
        "DebugPrintPower() -         tag=CARDTYPE value=GAME",
        $"DebugPrintPower() -     Player EntityID=2 PlayerID={Me} GameAccountId=[hi=144115198130930503 lo=11]",
        $"DebugPrintPower() -         tag=CONTROLLER value={Me}",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "DebugPrintPower() -         tag=HERO_ENTITY value=10",
        $"DebugPrintPower() -     Player EntityID=3 PlayerID={Bob} GameAccountId=[hi=0 lo=0]",
        $"DebugPrintPower() -         tag=CONTROLLER value={Bob}",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "DebugPrintPower() -         tag=BACON_DUMMY_PLAYER value=1",
        "DebugPrintGame() - GameType=GT_BATTLEGROUNDS",
        $"DebugPrintGame() - PlayerID={Me}, PlayerName=Me#1234",
        $"DebugPrintGame() - PlayerID={Bob}, PlayerName=Rival#4321",
        .. Entity(10, "TB_BaconShop_HERO_50", Me, "PLAY", "HERO", "PLAYER_ID=1", "HEALTH=30", "PLAYER_LEADERBOARD_PLACE=1"),
        .. Entity(11, "TB_BaconShop_HP_050", Me, "PLAY", "HERO_POWER"),
        .. Entity(20, "BG20_HERO_102", Bob, "SETASIDE", "HERO", "PLAYER_ID=4", "HEALTH=30", "PLAYER_LEADERBOARD_PLACE=2", "PLAYER_TECH_LEVEL=2"),
        .. Entity(30, "TB_BaconShopBob", Bob, "PLAY", "HERO"),
        .. Entity(40, "TB_BaconShop_DragBuy", Me, "PLAY", "MOVE_MINION_HOVER_TARGET"),
        .. Entity(41, "TB_BaconShop_DragSell", Me, "PLAY", "MOVE_MINION_HOVER_TARGET"),
        .. Entity(42, "TB_BaconShop_8p_Reroll_Button", Me, "PLAY", "GAME_MODE_BUTTON"),
        .. Entity(43, "TB_BaconShopLockAll_Button", Me, "PLAY", "GAME_MODE_BUTTON"),
        .. Entity(44, "TB_BaconShopTechUp02_Button", Me, "PLAY", "GAME_MODE_BUTTON"),
        .. Entity(46, "TB_BaconShop_8P_PlayerE", Me, "PLAY", "ENCHANTMENT"),
        Game("TURN", 1),
        Game("STEP", "MAIN_READY"),
        .. Entity(50, "BGS_004", Bob, "PLAY", "MINION", "ATK=1", "HEALTH=1", "ZONE_POSITION=1", "TECH_LEVEL=1"),
        .. Entity(51, "BGS_039", Bob, "PLAY", "MINION", "ATK=2", "HEALTH=1", "ZONE_POSITION=2", "TECH_LEVEL=1"),
        .. Entity(52, "BG28_810", Bob, "PLAY", "BATTLEGROUND_SPELL", "ZONE_POSITION=3", "TECH_LEVEL=1"),
        Player("RESOURCES", 3),
        Player("PLAYER_TECH_LEVEL", 1),
        Game("STEP", "MAIN_ACTION"),
    ];

    [Fact]
    public void Buying_moves_the_offer_into_our_hand_in_one_step()
    {
        var events = Events(Buy(51));
        var step = Assert.Single(events, e => e.StartsWith("M ", StringComparison.Ordinal));
        var id = IdOf(events, "BGS_039");
        Assert.Equal($"M BUY {id}", step);
        // One card on screen: the offer becomes ours under the same id, written once with where the block left it.
        Assert.Equal([$"E {id} L HAND 0 BGS_039 MINION ATK=2 HEALTH=1 TECH_LEVEL=1", $"Z {id} 1"],
            After(events, step).Where(l => l.StartsWith($"E {id} ", StringComparison.Ordinal) || l.StartsWith($"Z {id} ", StringComparison.Ordinal)));
        Assert.Contains("V L RESOURCES_USED 3", After(events, step));

        var frame = Frames(events).Single(f => f.Kind == MatchStepKind.Buy);
        Assert.Equal("BGS_039", frame.Actor?.CardId);
        Assert.Contains(frame.Cards, c => c is { Side: 'L', Zone: "HAND", CardId: "BGS_039" });
        Assert.DoesNotContain(frame.Cards, c => c is { Side: 'B', CardId: "BGS_039" });
        Assert.Equal((0, 3), (frame.Gold, frame.MaxGold));
    }

    [Fact]
    public void Playing_a_card_from_hand_is_a_play_step()
    {
        var events = Events([.. Buy(51), Start("PLAY", 51), Tag(51, "ZONE", "PLAY"), Tag(51, "ZONE_POSITION", 1), End]);
        Assert.Contains($"M PLAY {IdOf(events, "BGS_039")} -", events);
        var frame = Frames(events).Single(f => f.Kind == MatchStepKind.Play);
        Assert.Contains(frame.Cards, c => c is { Side: 'L', Zone: "PLAY", Position: 1, CardId: "BGS_039" });
    }

    [Fact]
    public void Selling_names_the_minion_sold()
    {
        var events = Events(
        [
            .. Buy(51), Start("PLAY", 51), Tag(51, "ZONE", "PLAY"), Tag(51, "ZONE_POSITION", 1), End,
            Start("PLAY", 41), Tag(41, "CARD_TARGET", 51), Tag(51, "ZONE", "SETASIDE"), End,
        ]);
        var id = IdOf(events, "BGS_039");
        Assert.Contains($"M SELL {id}", events);
        Assert.Contains($"X {id}", After(events, $"M SELL {id}"));
        var frame = Frames(events).Single(f => f.Kind == MatchStepKind.Sell);
        Assert.Equal("BGS_039", frame.Actor?.CardId);
        Assert.DoesNotContain(frame.Cards, c => c.CardId == "BGS_039");
    }

    [Fact]
    public void Refreshing_replaces_the_offer_in_one_step()
    {
        var events = Events(
        [
            Start("PLAY", 42),
            Tag(50, "ZONE", "REMOVEDFROMGAME"), Tag(51, "ZONE", "REMOVEDFROMGAME"),
            .. Entity(53, "BG21_001", Bob, "PLAY", "MINION", "ATK=3", "HEALTH=3", "ZONE_POSITION=1"),
            End,
        ]);
        Assert.Contains("M REFRESH -", events);
        var shop = Frames(events).Single(f => f.Kind == MatchStepKind.Refresh).Cards.Where(c => c.Side == 'B').Select(c => c.CardId);
        Assert.Equal(["BG21_001", "BG28_810"], shop.Order());
    }

    [Fact]
    public void Upgrading_the_tavern_shows_the_new_tier()
    {
        var events = Events(Start("PLAY", 44), Player("RESOURCES_USED", 3), Player("PLAYER_TECH_LEVEL", 2), End);
        Assert.Contains("M UPGRADE -", events);
        Assert.Equal(2, Frames(events).Single(f => f.Kind == MatchStepKind.Upgrade).Tier);
    }

    [Fact]
    public void Freezing_by_the_button_or_its_trigger_is_a_freeze_step()
    {
        var events = Events(
        [
            Start("PLAY", 43), Tag(50, "FROZEN", 1), Tag(51, "FROZEN", 1), End,
            // The 28.10 log also shows the button's own trigger; one that changes nothing is no step.
            Start("TRIGGER", 43), End,
            Start("TRIGGER", 43), Tag(50, "FROZEN", 0), Tag(51, "FROZEN", 0), End,
        ]);
        Assert.Equal(2, events.Count(e => e == "M FREEZE -"));
        var frames = Frames(events).Where(f => f.Kind == MatchStepKind.Freeze).ToList();
        Assert.True(frames[0].Cards.Where(c => c.Side == 'B' && c.Type == "MINION").All(c => c.Frozen));
        Assert.True(frames[1].Cards.Where(c => c.Side == 'B').All(c => !c.Frozen));
    }

    [Fact]
    public void The_hero_power_is_its_own_step()
    {
        var events = Events(Start("PLAY", 11), Player("RESOURCES_USED", 1), End);
        Assert.Contains($"M POWER {IdOf(events, "TB_BaconShop_HP_050")} -", events);
        Assert.Equal(MatchStepKind.HeroPower, Frames(events)[^1].Kind);
    }

    [Fact]
    public void Using_a_minion_on_the_table_is_its_own_step_with_what_it_did()
    {
        var events = Events(
        [
            .. Entity(60, "BG36_311", Me, "PLAY", "MINION", "ATK=2", "HEALTH=2", "ZONE_POSITION=1"),
            .. Entity(61, "BG34_604", Me, "HAND", "MINION", "ATK=3", "HEALTH=3", "ZONE_POSITION=1"),
            Game("STEP", "MAIN_END"),
            // BG36_311 eats a card in hand (28.10: 17 such plays of BG36_311 and BG36_099, none closed by a BLOCK_END).
            Start("PLAY", 60), Tag(60, "CARD_TARGET", 61),
            Start("POWER", 60), Tag(61, "ZONE", "REMOVEDFROMGAME"), Tag(60, "ATK", 5), Tag(60, "HEALTH", 5), End,
            Start("PLAY", 40), Tag(40, "CARD_TARGET", 51), Tag(51, "CONTROLLER", Me), Tag(51, "ZONE", "HAND"), End,
        ]);
        var used = IdOf(events, "BG36_311");
        Assert.Contains($"M USE {used} {IdOf(events, "BG34_604")}", events);
        var frames = Frames(events);
        var use = frames.Single(f => f.Kind == MatchStepKind.Use);
        Assert.Equal(("BG36_311", "BG34_604"), (use.Actor?.CardId, use.Target?.CardId));
        Assert.Equal((5, 5), (use.Actor!.Attack, use.Actor.Health));
        Assert.DoesNotContain(use.Cards, c => c.CardId == "BG34_604");
        // The card in hand is still there before the use: its change is not the step before.
        Assert.Contains(frames[use.Index - 1].Cards, c => c.CardId == "BG34_604");
        Assert.Equal(MatchStepKind.Buy, frames[use.Index + 1].Kind);
    }

    [Fact]
    public void Moving_a_minion_is_a_move_step_in_its_block_or_without_one()
    {
        var events = Events(
        [
            .. Entity(60, "BGS_061", Me, "PLAY", "MINION", "ATK=2", "HEALTH=3", "ZONE_POSITION=1"),
            .. Entity(61, "BGS_119", Me, "PLAY", "MINION", "ATK=1", "HEALTH=2", "ZONE_POSITION=2"),
            Start("MOVE_MINION", 60), Tag(61, "ZONE_POSITION", 1), Tag(60, "ZONE_POSITION", 2), End,
            // Outside a block (not in the 28.10 log): one step for the run of slot changes.
            Tag(60, "ZONE_POSITION", 1), Tag(61, "ZONE_POSITION", 2),
            Game("STEP", "MAIN_END"),
        ]);
        var first = IdOf(events, "BGS_061");
        Assert.Equal(2, events.Count(e => e == $"M MOVE {first}"));
        var moves = Frames(events).Where(f => f.Kind == MatchStepKind.Move).ToList();
        Assert.Equal((1, 2), (moves[0].MovedFrom, moves[0].Actor?.Position));
        Assert.Equal((2, 1), (moves[1].MovedFrom, moves[1].Actor?.Position));
    }

    [Fact]
    public void Our_choices_are_steps_with_their_options_and_pick()
    {
        var events = Events(
        [
            .. Entity(70, "BG26_HERO_104", Me, "HAND", "HERO"),
            .. Entity(71, "TB_BaconShop_HERO_94", Me, "HAND", "HERO"),
            .. Entity(80, "TB_BaconShop_Triples_01", Me, "HAND", "SPELL"),
            .. Entity(81, "BG30_121", Me, "SETASIDE", "MINION"),
            .. Entity(82, "BG32_340", Me, "SETASIDE", "MINION"),
            Start("PLAY", 80),
            Tag(80, "ZONE", "PLAY"),
            .. Offer("GENERAL", 80, 81, 82),
            .. Chosen(82),
            Tag(82, "ZONE", "HAND"),
            Tag(80, "ZONE", "GRAVEYARD"),
            // The 28.10 log never closes a play with a choice: the next player action starts at the top level anyway.
            Start("PLAY", 82), Tag(82, "ZONE", "PLAY"), Tag(82, "ZONE_POSITION", 1), End,
        ]);
        Assert.Contains("C DISCOVER TB_BaconShop_Triples_01 BG30_121,BG32_340", events);
        Assert.Contains("c BG32_340", events);
        // Hero options in hand and set-aside options are never cards on screen.
        Assert.DoesNotContain(events, e => e.StartsWith("E ", StringComparison.Ordinal) && (e.Contains("HERO ", StringComparison.Ordinal) && e.Contains(" HAND ", StringComparison.Ordinal) || e.Contains("SETASIDE", StringComparison.Ordinal)));

        // The table before the first tavern (no hero choice in this game) is the start step.
        var frames = Frames(events).Where(f => f.Kind != MatchStepKind.Start).ToList();
        Assert.Equal([MatchStepKind.TurnStart, MatchStepKind.Play, MatchStepKind.Choice, MatchStepKind.Play], frames.Select(f => f.Kind));
        var choice = frames[2].Choice!;
        Assert.Equal(("DISCOVER", "TB_BaconShop_Triples_01"), (choice.Kind, choice.SourceCardId));
        Assert.Equal(["BG32_340"], choice.Picked);
        Assert.Equal("BG32_340", frames[3].Actor?.CardId);
    }

    [Fact]
    public void The_hero_choice_opens_the_replay()
    {
        var recorder = Run(
        [
            .. Setup.Take(14),
            .. Entity(70, "BG26_HERO_104", Me, "HAND", "HERO"),
            .. Entity(71, "TB_BaconShop_HERO_94", Me, "HAND", "HERO"),
            .. Offer("MULLIGAN", null, 70, 71),
            .. Chosen(71),
            Tag(71, "ZONE", "PLAY"),
            Game("TURN", 1),
        ]);
        var events = recorder.Events;
        Assert.Equal("C HERO - BG26_HERO_104,TB_BaconShop_HERO_94", events[0]);
        var frames = Frames(events);
        Assert.Equal(MatchStepKind.Choice, frames[0].Kind);
        Assert.Equal(MatchPhase.Start, frames[0].Phase);
        Assert.Equal(["TB_BaconShop_HERO_94"], frames[0].Choice!.Picked);
        Assert.Contains(frames[0].Cards, c => c is { Side: 'L', Type: "HERO", CardId: "TB_BaconShop_HERO_94" });
    }

    [Fact]
    public void A_rerolled_hero_option_is_offered_as_the_hero_it_became()
    {
        var recorder = Run(
        [
            .. Setup.Take(14),
            .. Entity(70, "BG26_HERO_104", Me, "HAND", "HERO"),
            .. Entity(71, "TB_BaconShop_HERO_94", Me, "HAND", "HERO"),
            .. Offer("MULLIGAN", null, 70, 71),
            // The reroll (BACON_MULLIGAN_HERO_REROLL_ACTIVE in the 28.10 log) turns the option into another hero.
            "DebugPrintPower() - CHANGE_ENTITY - Updating Entity=71 CardID=BG36_HERO_000",
            "DebugPrintPower() -     tag=CARDTYPE value=HERO",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            .. Chosen(71),
            Tag(71, "ZONE", "PLAY"),
            Game("TURN", 1),
        ]);
        var events = recorder.Events;
        Assert.Equal(["C HERO - BG26_HERO_104,BG36_HERO_000", "c BG36_HERO_000"], events.Take(2));
        AssertPicksOffered(events);
        var choice = Frames(events)[0].Choice!;
        Assert.Equal(["BG36_HERO_000"], choice.Picked);
        Assert.Contains("BG36_HERO_000", choice.Options);
        Assert.DoesNotContain("TB_BaconShop_HERO_94", events.Single(e => e.StartsWith("C ", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_frozen_offer_leaves_view_for_the_combat_and_is_back_in_the_next_tavern()
    {
        var events = Events(
        [
            Start("PLAY", 43), Tag(50, "FROZEN", 1), End,
            Game("TURN", 2),
            Game("BOARD_VISUAL_STATE", 2),
            // The rest of the offer goes; the frozen card stays in Bob's play through the fight (28.10: BG20_104).
            Tag(51, "ZONE", "REMOVEDFROMGAME"), Tag(52, "ZONE", "REMOVEDFROMGAME"),
            Game("BACON_IN_COMBAT_PHASE", 1),
            .. Entity(110, "BG20_HERO_102", Bob, "PLAY", "HERO", "HEALTH=30", "PLAYER_ID=4"),
            .. Entity(111, "BGS_119", Bob, "PLAY", "MINION", "ATK=1", "HEALTH=2", "ZONE_POSITION=1"),
            Tag(50, "ZONE_POSITION", 2),
            Game("BOARD_VISUAL_STATE", 1),
            Game("TURN", 3),
        ]);
        AssertSeenOnly(events);
        var frames = Frames(events);
        Assert.DoesNotContain(frames.Single(f => f.Kind == MatchStepKind.CombatStart).Cards, c => c.CardId == "BGS_004");
        var tavern = frames.Last(f => f.Kind == MatchStepKind.TurnStart);
        Assert.Equal(2, tavern.Turn);
        Assert.Contains(tavern.Cards, c => c is { Side: 'B', CardId: "BGS_004", Frozen: true });
    }

    [Fact]
    public void A_recording_that_starts_in_a_combat_keeps_bob_s_frozen_offer_off_the_opponent_s_board()
    {
        var recorder = Run(
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=1",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=2",
            "DebugPrintPower() -         tag=BOARD_VISUAL_STATE value=2",
            "DebugPrintPower() -         tag=BACON_IN_COMBAT_PHASE value=1",
            .. Setup.Skip(3).Take(11),
            // A reconnect: the whole table at once, Bob's frozen offer in his play next to the opponent's board.
            .. Entity(10, "TB_BaconShop_HERO_50", Me, "PLAY", "HERO", "PLAYER_ID=1", "HEALTH=30", "PLAYER_LEADERBOARD_PLACE=1"),
            .. Entity(50, "BGS_004", Bob, "PLAY", "MINION", "ATK=1", "HEALTH=1", "ZONE_POSITION=1", "FROZEN=1"),
            .. Entity(101, "BG26_801", Me, "PLAY", "MINION", "ATK=4", "HEALTH=6", "ZONE_POSITION=1"),
            .. Entity(110, "BG20_HERO_102", Bob, "PLAY", "HERO", "HEALTH=30", "PLAYER_ID=4"),
            .. Entity(111, "BGS_119", Bob, "PLAY", "MINION", "ATK=1", "HEALTH=2", "ZONE_POSITION=1"),
            Game("STEP", "MAIN_START"),
            Start("ATTACK", 101), Game("PROPOSED_DEFENDER", 111), Tag(111, "DAMAGE", 4), End,
            Start("DEATHS", 1), Tag(111, "ZONE", "GRAVEYARD"), End,
            Game("BOARD_VISUAL_STATE", 1),
            Game("TURN", 3),
        ]);
        var events = recorder.Events;
        Assert.StartsWith("F 1 ", events[0], StringComparison.Ordinal);
        AssertSeenOnly(events);
        Assert.DoesNotContain(events, e => e.StartsWith("E ", StringComparison.Ordinal) && e.Contains(" O PLAY ", StringComparison.Ordinal) && e.Contains("FROZEN", StringComparison.Ordinal));
        var frames = Frames(events);
        Assert.Equal(["BGS_119"], frames.First(f => f.Kind == MatchStepKind.Attack).Cards.Where(c => c.Side == 'O' && c.Type == "MINION").Select(c => c.CardId));
        Assert.Contains(frames.Last(f => f.Kind == MatchStepKind.TurnStart).Cards, c => c is { Side: 'B', CardId: "BGS_004" });
    }

    [Fact]
    public void Every_line_written_follows_the_format_4_grammar_and_foreign_lines_do_not()
    {
        var events = Run(Game5th()).Events;
        AssertSeenOnly(events);
        Assert.Contains(events, e => e.StartsWith("L ", StringComparison.Ordinal));

        string[] valid =
        [
            "T 1 47", "F 12 1296", "f LOSS 19 0", "M BUY -", "M PLAY 66 -", "M USE 66 64", "M REFRESH -", "C HERO - BG36_HERO_000,BG30_HERO_304",
            "C DISCOVER TB_BaconShop_Triples_01 BG30_121,BG32_340,BG34_604", "c BG34_604", "R 4", "E 1 L PLAY 0 BG36_HERO_000 HERO ARMOR=12 HEALTH=30",
            "E 4 L HAND 0 BG36_110 MINION ATK=2 HEALTH=3 TECH_LEVEL=1", "E 6 B PLAY 4 BG28_810 BATTLEGROUND_SPELL TECH_LEVEL=1",
            "E 9 O PLAY 0 BG26_HERO_104 HERO ARMOR=15 HEALTH=30", "E 12 L SECRET 0 BG24_Quest_112 SPELL", "X 3", "Y 11", "Z 4 7",
            "V 11 DAMAGE 2", "V L RESOURCES_USED 3", "L 1 L BG36_HERO_000 4 0 0 5",
        ];
        Assert.All(valid, line => Assert.True(BattlegroundsReplayGrammar.IsLine(line), line));

        // What a foreign file could carry that the recorder never writes.
        string[] invalid =
        [
            "E 5 O HAND 1 BG26_800 MINION", "E 5 B PLAY 1 TB_BaconShopBob HERO", "E 5 L PLAY 1 TB_BaconShop_8P_PlayerE ENCHANTMENT",
            "E 5 L SECRET 0 BG_OldGod MINION", "E 0 L PLAY 1 BGS_004 MINION", "E 5 L PLAY -1 BGS_004 MINION", "E 5 L PLAY 1 BGS_004 MINION ATK=-3",
            "E 5 L PLAY 1 BGS_004 MINION CREATOR_DBID=1234", "L 1 - BG36_HERO_000 4 -4 0 5", "M BUY 5 6", "M REFRESH 4", "M DANCE -",
            "C DISCOVER - https://example.test/x", "c ../../x", "V O PLAYER_TECH_LEVEL 3", "V 4 PLAYER_ID 2", "R -1", "T 0 5",
            "E 5 L PLAY 1 Me#1234 MINION", "N 2 64", "",
        ];
        Assert.All(invalid, line => Assert.False(BattlegroundsReplayGrammar.IsLine(line), line));
    }

    /// <summary>Every pick is one of the options of the choice before it.</summary>
    internal static void AssertPicksOffered(IEnumerable<string> events)
    {
        string[] options = [];
        foreach (var line in events)
        {
            if (line.StartsWith("C ", StringComparison.Ordinal))
            {
                options = line.Split(' ')[3].Split(',');
            }
            else if (line.StartsWith("c ", StringComparison.Ordinal))
            {
                Assert.Contains(line[2..], options);
            }
        }
    }

    [Fact]
    public void A_trigger_of_our_card_in_the_tavern_is_a_step_and_shows_its_buff()
    {
        var events = Events(
        [
            .. Entity(60, "BGS_061", Me, "PLAY", "MINION", "ATK=2", "HEALTH=3", "ZONE_POSITION=1"),
            Game("STEP", "MAIN_END"),
            // Enchantments and the game's own blocks are no steps: their changes belong to the step before.
            Start("TRIGGER", 46), Tag(60, "ATK", 3), End,
            Start("TRIGGER", 1), Tag(60, "HEALTH", 4), End,
            Start("TRIGGER", 60), Tag(60, "ATK", 4), Tag(60, "HEALTH", 5), End,
        ]);
        Assert.Single(events, e => e.StartsWith("M TRIGGER", StringComparison.Ordinal));
        var frames = Frames(events);
        var trigger = frames.Single(f => f.Kind == MatchStepKind.Trigger);
        Assert.Equal((4, 5), (trigger.Actor!.Attack, trigger.Actor.Health));
        Assert.Contains(frames[trigger.Index - 1].Cards, c => c is { CardId: "BGS_061", Attack: 3, Health: 4 });
    }

    [Fact]
    public void A_combat_is_one_step_per_visible_block_with_the_block_s_final_values()
    {
        var events = Events(Combat());
        var steps = Between(events, "F ", "f ").Where(e => e.StartsWith("M ", StringComparison.Ordinal)).ToList();
        output.WriteLine(string.Join(Environment.NewLine, events));
        Assert.Equal(["ATTACK", "DEATHS", "ATTACK", "TRIGGER", "ATTACK", "DEATHS"], steps.Select(s => s.Split(' ')[1]));

        // Five ATK changes of one minion inside its block are one line with the last value.
        var buffed = IdOf(events, "BG26_801");
        Assert.Single(events, e => e.StartsWith($"V {buffed} ATK ", StringComparison.Ordinal));
        Assert.Contains($"V {buffed} ATK 9", events);

        var frames = Frames(events);
        var fight = frames.SkipWhile(f => f.Kind != MatchStepKind.CombatStart).TakeWhile(f => f.Kind != MatchStepKind.CombatEnd).Skip(1).ToList();
        Assert.Equal(6, fight.Count);
        var attack = fight[0];
        Assert.Equal(("BG26_801", "BGS_119"), (attack.Actor?.CardId, attack.Target?.CardId));
        Assert.Equal(["BGS_119"], fight[1].Cards.Where(c => c.Dying).Select(c => c.CardId));
        Assert.DoesNotContain(fight[2].Cards, c => c.CardId == "BGS_119");

        var start = frames.Single(f => f.Kind == MatchStepKind.CombatStart);
        Assert.Contains(start.Cards, c => c is { Side: 'O', Type: "HERO", CardId: "BG20_HERO_102" });
        Assert.DoesNotContain(start.Cards, c => c.Side == 'B');
        var end = frames.Single(f => f.Kind == MatchStepKind.CombatEnd);
        Assert.Equal(("WIN", 0, 5), (end.Result, end.Taken, end.Dealt));
        // The end shows the boards as the fight left them; the clean-up shows with the next tavern.
        Assert.Contains(end.Cards, c => c is { Side: 'L', CardId: "BG26_801" });
        Assert.Contains(end.Cards, c => c.Side == 'O');
        var tavern = frames.Last(f => f.Kind == MatchStepKind.TurnStart);
        Assert.Equal(2, tavern.Turn);
        Assert.DoesNotContain(tavern.Cards, c => c.Side == 'O');
    }

    [Fact]
    public void Battlegrounds_replay_records_only_what_the_player_saw()
    {
        var recorder = Run([.. Setup,
            // A card in the opponent's hand, a minion of another player (a Duos partner), an enchantment.
            .. Entity(90, "BG26_800", Bob, "HAND", "MINION", "ATK=5", "HEALTH=5"),
            .. Entity(91, "BG25_011", 5, "PLAY", "MINION", "ATK=4", "HEALTH=4"),
            .. Entity(92, "BG_ShopBuff_Elemental", Me, "PLAY", "ENCHANTMENT"),
            // Bob's offer left in play when the combat starts never joins the opponent's board.
            .. Entity(93, "BG21_001", Bob, "PLAY", "MINION", "ATK=3", "HEALTH=3", "ZONE_POSITION=4"),
            .. CombatSetup(),
            Tag(93, "ZONE_POSITION", 3), Tag(93, "ATK", 9),
            .. Fight(win: true),
            ]);
        var events = recorder.Events;
        Assert.DoesNotContain(events, e => e.Contains("BG26_800", StringComparison.Ordinal) || e.Contains("BG25_011", StringComparison.Ordinal)
                                           || e.Contains("BG_ShopBuff_Elemental", StringComparison.Ordinal));
        var offer = IdOf(events, "BG21_001");
        Assert.DoesNotContain(Between(events, "F ", "f "), e => e.StartsWith($"V {offer} ", StringComparison.Ordinal) || e.Contains(" BG21_001 ", StringComparison.Ordinal));
        AssertSeenOnly(events);

        var text = recorder.Battlegrounds.Text(new ReplayHeader(), new BattlegroundsReplayData());
        Assert.DoesNotContain("Me#1234", text);
        Assert.DoesNotContain("Rival", text);
        Assert.DoesNotContain("#", text);
        Assert.DoesNotContain("hi=", text);
    }

    [Fact]
    public void The_fixture_records_the_hero_choice_the_tavern_and_the_first_combat()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var tracker = new BattlegroundsTracker();
        var recorder = new ReplayRecorder();
        parser.AddListener(tracker);
        parser.AddListener(recorder);
        foreach (var line in BattlegroundsFixtureTests.Lines())
        {
            parser.Feed(line);
        }

        parser.Flush();
        var events = recorder.Events;
        output.WriteLine(string.Join(Environment.NewLine, events.Take(80)));
        AssertSeenOnly(events);
        Assert.StartsWith("C HERO - ", events.First(e => e.StartsWith("C ", StringComparison.Ordinal)), StringComparison.Ordinal);
        // BG turn = (TURN + 1) / 2: the tavern of turn 1 (TURN 1), its combat (TURN 2), the tavern of turn 2 (TURN 3).
        Assert.Equal(["T 1", "F 1", "T 2"], events.Where(e => e[0] is 'T' or 'F' && e[1] == ' ').Select(e => string.Join(' ', e.Split(' ').Take(2))));
        Assert.Contains(events, e => e.StartsWith("M BUY ", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("M PLAY ", StringComparison.Ordinal));
        Assert.Contains(events, e => e.StartsWith("M ATTACK ", StringComparison.Ordinal));

        var text = recorder.Battlegrounds.Text(new ReplayHeader(), tracker.ReplayData(parser.Game!));
        foreach (var name in new[] { "Player#1000", "Opponent", "Innkeeper", "GameAccountId" })
        {
            Assert.DoesNotContain(name, text);
        }

        var frames = Frames(events);
        var buy = frames.First(f => f.Kind == MatchStepKind.Buy);
        Assert.Single(buy.Cards, c => c.Side == 'L' && c.Zone == "HAND");
        Assert.Equal(1, buy.Turn);
        Assert.Contains(frames, f => f.Phase == MatchPhase.Combat && f.Cards.Any(c => c.Side == 'O' && c.Type == "HERO"));
    }

    [Fact]
    public void The_place_is_our_hero_s_when_the_game_is_over_for_us()
    {
        var recorder = Run([.. Setup, .. Combat(win: false),
            Player("PLAYSTATE", "LOST"),
            // The 28.10 log sets the final place after PLAYSTATE.
            Tag(10, "PLAYER_LEADERBOARD_PLACE", 5),
            Game("STATE", "COMPLETE"),
        ]);
        var events = recorder.Events;
        Assert.Equal("R 5", events[^1]);
        Assert.Contains("f LOSS 5 0", events);
        var end = Frames(events)[^1];
        Assert.Equal((MatchStepKind.End, MatchPhase.End, 5), (end.Kind, end.Phase, end.Place));
    }

    [Fact]
    public void An_endless_combat_stops_adding_steps_at_the_cap()
    {
        string[] attack = [Start("ATTACK", 101), Game("PROPOSED_DEFENDER", 111), End];
        var recorder = Run([.. Setup, .. CombatSetup(),
            .. Enumerable.Range(0, BattlegroundsReplayRecorder.MaxCombatSteps + 100).SelectMany(_ => attack),
            Game("BOARD_VISUAL_STATE", 1)]);
        var events = recorder.Events;
        Assert.True(recorder.Truncated);
        Assert.Equal(BattlegroundsReplayRecorder.MaxCombatSteps, events.Count(e => e.StartsWith("M ATTACK", StringComparison.Ordinal)));
        Assert.Contains(events, e => e.StartsWith("f ", StringComparison.Ordinal));
        Assert.Contains("\"truncated\":true", recorder.Battlegrounds.Text(new ReplayHeader(), new BattlegroundsReplayData()));
    }

    [RealLogsFact]
    public void The_28_10_game_is_recorded_whole_under_a_megabyte_and_without_slowing_the_parser()
    {
        if (!File.Exists(RealLog))
        {
            output.WriteLine($"skipped: {RealLog} is not here");
            return;
        }

        // The Battlegrounds game only: from its CREATE_GAME on (the log holds two Constructed games before it).
        var all = File.ReadAllLines(RealLog);
        var type = Array.FindIndex(all, l => l.Contains("GameType=GT_BATTLEGROUNDS", StringComparison.Ordinal));
        var lines = all[Array.FindLastIndex(all, type, l => l.Contains("GameState.DebugPrintPower() - CREATE_GAME", StringComparison.Ordinal))..];
        // Alternated runs, the fastest of each: the tracker's background simulations and the GC make single runs noisy.
        var (without, with) = (TimeSpan.MaxValue, TimeSpan.MaxValue);
        (ReplayRecorder Recorder, BattlegroundsTracker Tracker, GameState? Game) end = default;
        for (var run = 0; run < 5; run++)
        {
            var plain = Time(lines, record: false, out _);
            var recorded = Time(lines, record: true, out end);
            without = plain < without ? plain : without;
            with = recorded < with ? recorded : with;
        }

        var text = end.Recorder!.Battlegrounds.Text(new ReplayHeader(), end.Tracker!.ReplayData(end.Game!));
        var frames = BattlegroundsMatchReplay.Parse(text)!.Frames.Count;
        var (header, body) = ReplayFile.Read(text)!.Value;
        var gzip = Gzip(text);
        output.WriteLine($"events {header.Events}, lines {body.Count}, frames {frames}, text {Encoding.UTF8.GetByteCount(text)} B, gzip {gzip} B, truncated {header.Truncated}");
        output.WriteLine($"parse {without.TotalMilliseconds:F0} ms, with the recorder {with.TotalMilliseconds:F0} ms, x{with / without:F2}");

        Assert.False(header.Truncated);
        Assert.InRange(header.Events, 1, BattlegroundsReplayRecorder.MaxEvents - 1);
        Assert.True(gzip <= 1024 * 1024, $"{gzip} B");
        Assert.Equal(12, body.Count(l => l.StartsWith("F ", StringComparison.Ordinal)));
        Assert.Equal(12, body.Count(l => l.StartsWith("f ", StringComparison.Ordinal)));
        Assert.Equal("R 4", body[header.Events - 1]);
        // 47 plays from hand and 17 activated powers of BG36_311 / BG36_099(_G) on the table.
        Assert.Equal((47, 17), (body.Count(l => l.StartsWith("M PLAY ", StringComparison.Ordinal)), body.Count(l => l.StartsWith("M USE ", StringComparison.Ordinal))));
        AssertSeenOnly(body.Take(header.Events).ToList());
        // The hero was rerolled before the pick (BG28_HERO_400_SKIN_C -> BG36_HERO_000): the first step offers it.
        AssertPicksOffered(body);
        var steps = BattlegroundsMatchReplay.Parse(text)!.Frames;
        // The last step's leaderboard has our final place, set by the game after PLAYSTATE.
        Assert.Equal(4, steps[^1].Leaderboard.Single(l => l.Local).Place);
        Assert.Equal(["BG36_HERO_000"], steps[0].Choice!.Picked);
        Assert.Contains("BG36_HERO_000", steps[0].Choice!.Options);
        Assert.True(with / without <= 1.25, $"x{with / without:F2}");
    }

    /// <summary>One parse of the lines with the trackers of a log import, with or without the recorder.</summary>
    private static TimeSpan Time(string[] lines, bool record, out (ReplayRecorder Recorder, BattlegroundsTracker Tracker, GameState? Game) end)
    {
        var parser = new PowerParser(new DateTime(2026, 10, 28, 0, 19, 35));
        var tracker = new BattlegroundsTracker { Cards = CardDb.Empty };
        var recorder = new ReplayRecorder();
        parser.AddListener(new ConstructedTracker());
        parser.AddListener(tracker);
        parser.AddListener(new GameFactsTracker());
        if (record)
        {
            parser.AddListener(recorder);
        }

        var watch = Stopwatch.StartNew();
        foreach (var line in lines)
        {
            parser.Feed(line);
        }

        parser.AbandonCurrentGame();
        var elapsed = watch.Elapsed;
        end = (recorder, tracker, parser.Game);
        return elapsed;
    }

    private static long Gzip(string text)
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return memory.Length;
    }

    /// <summary>
    /// Every card line follows the rule: Bob's offer only outside combat, the combat opponent's cards only in combat,
    /// nothing set aside, no hand but ours, no enchantment; tag lines only for cards shown.
    /// </summary>
    internal static void AssertSeenOnly(IReadOnlyList<string> events)
    {
        var combat = false;
        var shown = new Dictionary<string, char>(StringComparer.Ordinal);
        foreach (var line in events)
        {
            Assert.True(BattlegroundsReplayGrammar.IsLine(line), $"not in the format 4 grammar: {line}");
            var t = line.Split(' ');
            switch (t[0])
            {
                case "F":
                    combat = true;
                    break;
                case "f":
                    combat = false;
                    break;
                case "T":
                    Assert.DoesNotContain(shown.Values, side => side == 'O');
                    break;
                case "E":
                    var (side, zone, type) = (t[2][0], t[3], t[6]);
                    Assert.True(side is 'L' or 'B' or 'O', line);
                    Assert.True(zone is "PLAY" or "HAND" or "SECRET", line);
                    Assert.True(side == 'L' || zone == "PLAY", $"another player's hand or secret: {line}");
                    Assert.NotEqual("ENCHANTMENT", type);
                    Assert.False(side == 'B' && combat, $"Bob's offer in combat: {line}");
                    Assert.False(side == 'O' && !combat, $"the opponent's board outside combat: {line}");
                    shown[t[1]] = side;
                    break;
                case "X" or "Y":
                    Assert.True(shown.Remove(t[1]), line);
                    break;
                case "V" when t[1] != "L":
                case "Z":
                    Assert.True(shown.ContainsKey(t[1]), $"a tag of a card not shown: {line}");
                    break;
            }

            Assert.DoesNotContain("CREATOR", line);
        }
    }

    /// <summary>
    /// A combat of turn 1 against BG20_HERO_102: three attacks, two deaths blocks, a trigger of our minion, a block of
    /// the game and one of an enchantment; our hero then hits theirs for 5 (or theirs ours).
    /// </summary>
    private static string[] Combat(bool win = true) => [.. CombatSetup(), .. Fight(win)];

    private static string[] Fight(bool win) =>
    [
        Start("ATTACK", 101), Game("PROPOSED_DEFENDER", 111), Tag(111, "DEFENDING", 1),
        Tag(111, "DAMAGE", 2), Tag(101, "DAMAGE", 1), Tag(111, "DEFENDING", 0), End,
        Start("DEATHS", 1), Tag(111, "ZONE", "GRAVEYARD"), End,
        Start("ATTACK", 112), Game("PROPOSED_DEFENDER", 101), Tag(101, "DAMAGE", 2), End,
        Start("TRIGGER", 101),
        Tag(101, "ATK", 5), Tag(101, "ATK", 6), Tag(101, "ATK", 7), Tag(101, "ATK", 8), Tag(101, "ATK", 9),
        End,
        Start("TRIGGER", 1), Tag(101, "HEALTH", 6), End,
        Start("TRIGGER", 46), Tag(101, "HEALTH", 7), End,
        Start("ATTACK", 101), Game("PROPOSED_DEFENDER", 112), Tag(112, "DAMAGE", 9), Tag(101, "DAMAGE", 3), End,
        Start("DEATHS", 1), Tag(112, "ZONE", "GRAVEYARD"), End,
        // The hero attack after the fight sits in the game's own block in real logs.
        Start("TRIGGER", 46),
        Start("ATTACK", win ? 10 : 110), Tag(win ? 110 : 10, "DAMAGE", 5), End,
        End,
        // The game clears the boards before it says the combat is over.
        Tag(111, "ZONE", "REMOVEDFROMGAME"), Tag(112, "ZONE", "REMOVEDFROMGAME"), Tag(110, "ZONE", "REMOVEDFROMGAME"),
        Game("BOARD_VISUAL_STATE", 1),
        Tag(30, "ZONE", "PLAY"),
        Game("TURN", 3),
        Game("STEP", "MAIN_READY"),
        Game("BACON_IN_COMBAT_PHASE", 0),
    ];

    /// <summary>Turn 1 ends, the combat turn starts: Bob's offer goes, the opponent's hero and board are set up.</summary>
    private static string[] CombatSetup() =>
    [
        .. Entity(101, "BG26_801", Me, "PLAY", "MINION", "ATK=4", "HEALTH=6", "ZONE_POSITION=1"),
        Game("TURN", 2),
        Game("BOARD_VISUAL_STATE", 2),
        // Bob's offer goes (51 may be in our hand by now; left in Bob's play it must not join the opponent's board).
        Tag(50, "ZONE", "REMOVEDFROMGAME"), Tag(52, "ZONE", "REMOVEDFROMGAME"),
        Game("BACON_IN_COMBAT_PHASE", 1),
        Game("STEP", "MAIN_READY"),
        Start("TRIGGER", 46),
        Tag(30, "ZONE", "SETASIDE"),
        .. Entity(110, "BG20_HERO_102", Bob, "PLAY", "HERO", "HEALTH=30"),
        Tag(110, "PLAYER_ID", 4),
        .. Entity(111, "BGS_119", Bob, "PLAY", "MINION", "ATK=1", "HEALTH=2", "ZONE_POSITION=1"),
        .. Entity(112, "BGS_061", Bob, "PLAY", "MINION", "ATK=2", "HEALTH=3", "ZONE_POSITION=2"),
        End,
        Game("STEP", "MAIN_START"),
    ];

    private static string[] Buy(int offer) =>
    [
        Start("PLAY", 40), Player("RESOURCES_USED", 3), Tag(40, "CARD_TARGET", offer),
        Tag(offer, "CONTROLLER", Me), Tag(offer, "ZONE_POSITION", 0), Tag(offer, "ZONE", "HAND"), Tag(offer, "ZONE_POSITION", 1),
        End,
    ];

    private static List<string> Events(params string[] bodies) => Run([.. Setup, .. bodies]).Events.ToList();

    internal static ReplayRecorder Run(params string[] bodies) => Play(bodies).Recorder;

    /// <summary>Feeds the lines to a parser with the recorder and the Battlegrounds tracker (rounds and lobby).</summary>
    internal static (GameState Game, ReplayRecorder Recorder, BattlegroundsTracker Tracker) Play(params string[] bodies)
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var tracker = new BattlegroundsTracker();
        var recorder = new ReplayRecorder();
        parser.AddListener(tracker);
        parser.AddListener(recorder);
        foreach (var body in bodies)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
        return (parser.Game!, recorder, tracker);
    }

    /// <summary>A whole short game: turn 1, a won combat, a lost one on turn 2, out in 5th place.</summary>
    internal static string[] Game5th() =>
    [
        .. Setup,
        .. Buy(51),
        .. Combat(),
        Player("RESOURCES", 4), Player("RESOURCES_USED", 0),
        .. Entity(54, "BG21_001", Bob, "PLAY", "MINION", "ATK=3", "HEALTH=3", "ZONE_POSITION=1", "TECH_LEVEL=2"),
        Start("PLAY", 44), Player("RESOURCES_USED", 4), Player("PLAYER_TECH_LEVEL", 2), End,
        Game("TURN", 4),
        Game("BOARD_VISUAL_STATE", 2),
        Tag(54, "ZONE", "REMOVEDFROMGAME"),
        Game("STEP", "MAIN_READY"),
        .. Entity(120, "BG20_HERO_102", Bob, "PLAY", "HERO", "HEALTH=30", "DAMAGE=5", "PLAYER_ID=4"),
        .. Entity(121, "BGS_119", Bob, "PLAY", "MINION", "ATK=6", "HEALTH=6", "ZONE_POSITION=1"),
        Game("STEP", "MAIN_START"),
        Start("ATTACK", 121), Game("PROPOSED_DEFENDER", 101), Tag(101, "DAMAGE", 9), End,
        Start("DEATHS", 1), Tag(101, "ZONE", "GRAVEYARD"), End,
        Start("ATTACK", 120), Game("PROPOSED_DEFENDER", 10), Tag(10, "DAMAGE", 30), End,
        Player("PLAYSTATE", "LOST"),
        Tag(10, "PLAYER_LEADERBOARD_PLACE", 5),
        Game("STATE", "COMPLETE"),
    ];

    internal static IReadOnlyList<BgReplayFrame> Frames(IReadOnlyList<string> events) =>
        BattlegroundsMatchReplay.Parse(ReplayFile.WriteBattlegrounds(new ReplayHeader(), new BattlegroundsReplayData(), events))!.Frames;

    private static string IdOf(IReadOnlyList<string> events, string cardId) =>
        events.First(e => e.StartsWith("E ", StringComparison.Ordinal) && e.Split(' ')[5] == cardId).Split(' ')[1];

    private static List<string> After(IReadOnlyList<string> events, string line) => events.SkipWhile(e => e != line).Skip(1).ToList();

    private static IEnumerable<string> Between(IReadOnlyList<string> events, string from, string to) =>
        events.SkipWhile(e => !e.StartsWith(from, StringComparison.Ordinal)).Skip(1).TakeWhile(e => !e.StartsWith(to, StringComparison.Ordinal));

    internal static string[] Entity(int id, string cardId, int controller, string zone, string type, params string[] tags) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        $"DebugPrintPower() -     tag=CARDTYPE value={type}",
        .. tags.Select(t => $"DebugPrintPower() -     tag={t.Replace("=", " value=", StringComparison.Ordinal)}"),
        $"DebugPrintPower() -     tag=ZONE value={zone}",
    ];

    internal static string Tag(int id, string tag, object value) => $"DebugPrintPower() - TAG_CHANGE Entity={id} tag={tag} value={value} ";

    internal static string Game(string tag, object value) => $"DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag={tag} value={value} ";

    internal static string Player(string tag, object value) => $"DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag={tag} value={value} ";

    internal static string Start(string type, int source) =>
        $"DebugPrintPower() - BLOCK_START BlockType={type} Entity={(source == 1 ? "GameEntity" : source.ToString(System.Globalization.CultureInfo.InvariantCulture))} EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ";

    internal const string End = "DebugPrintPower() - BLOCK_END";

    internal static string[] Offer(string type, int? source, params int[] options) =>
    [
        $"DebugPrintEntityChoices() - id=1 Player=Me#1234 TaskList=1 ChoiceType={type} CountMin=1 CountMax=1",
        .. source is { } s ? [$"DebugPrintEntityChoices() -   Source={s}"] : Array.Empty<string>(),
        .. options.Select((o, i) => $"DebugPrintEntityChoices() -   Entities[{i}]={o}"),
    ];

    internal static string[] Chosen(params int[] picked) =>
    [
        $"DebugPrintEntitiesChosen() - id=1 Player=Me#1234 EntitiesCount={picked.Length}",
        .. picked.Select((p, i) => $"DebugPrintEntitiesChosen() -   Entities[{i}]={p}"),
    ];
}
