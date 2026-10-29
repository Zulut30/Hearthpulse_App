using System.Reflection;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using static Hearthpulse.Tests.BattlegroundsReplayTests;

namespace Hearthpulse.Tests;

/// <summary>
/// The match page of a Battlegrounds game recorded step by step (format 4), as the player bar, the timeline and the BG
/// table read it: <see cref="MatchPage.BattlegroundsStep"/>, the marks and spans P2's timeline calls, the frame count.
/// </summary>
public sealed class MatchPageBattlegroundsTests : IDisposable
{
    private readonly ReplayStore _store = new(Path.Combine(Path.GetTempPath(), $"hp-match-bg-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(_store.Directory))
        {
            Directory.Delete(_store.Directory, recursive: true);
        }
    }

    private static readonly GameRecord Record = new()
    {
        Key = "20261023123000|me#1234|GT_BATTLEGROUNDS",
        StartedAt = new DateTime(2026, 10, 23, 12, 30, 0),
        EndedAt = new DateTime(2026, 10, 23, 12, 45, 0),
        Mode = "Battlegrounds",
        GameType = "GT_BATTLEGROUNDS",
        PlayerName = "Me#1234",
        HeroCardId = "TB_BaconShop_HERO_50",
        PlayerClass = "NEUTRAL",
        Result = "Loss",
        Placement = 5,
        Turns = 2,
    };

    private MatchPage Page()
    {
        var (game, recorder, tracker) = Play(Game5th());
        var saved = ReplayCapture.Attach(Record, game, recorder, tracker, _store);
        return MatchPage.Load(saved, _store, CardDb.Empty, null);
    }

    [Fact]
    public void Every_step_of_the_game_is_a_frame_in_order()
    {
        var page = Page();

        Assert.True(page.HasBattlegroundsSteps);
        Assert.NotEmpty(page.Rounds);
        Assert.Equal(19, page.FrameCount);
        var steps = Enumerable.Range(0, page.FrameCount).Select(i => page.BattlegroundsStep(i)!).ToList();
        Assert.Equal(
        [
            MatchStepKind.Start, MatchStepKind.TurnStart, MatchStepKind.Buy,
            MatchStepKind.CombatStart, MatchStepKind.Attack, MatchStepKind.Deaths, MatchStepKind.Attack, MatchStepKind.Trigger,
            MatchStepKind.Attack, MatchStepKind.Deaths, MatchStepKind.CombatEnd,
            MatchStepKind.TurnStart, MatchStepKind.Upgrade,
            MatchStepKind.CombatStart, MatchStepKind.Attack, MatchStepKind.Deaths, MatchStepKind.Attack, MatchStepKind.CombatEnd,
            MatchStepKind.End,
        ], steps.Select(s => s.Kind));
        Assert.All(steps, s => Assert.Equal(19, s.Count));
        Assert.Equal(steps.Select((_, i) => i), steps.Select(s => s.Index));
        // Out of range clamps, as the Constructed steps do.
        Assert.Equal(MatchStepKind.End, page.BattlegroundsStep(99)!.Kind);
    }

    [Fact]
    public void A_tavern_step_shows_bob_s_offer_our_side_gold_and_the_leaderboard()
    {
        var page = Page();
        var tavern = page.BattlegroundsStep(1)!;

        Assert.Equal(("Turn 1 · tavern", "The tavern opens", MatchPhase.Shop), (tavern.Title, tavern.Action, tavern.Phase));
        Assert.False(tavern.InCombat);
        Assert.Equal("3 / 3", tavern.Gold);
        Assert.Equal(["BGS_004", "BGS_039", "BG28_810"], tavern.Shop.Select(c => c.CardId));
        Assert.Equal(1, tavern.Shop[0].Tier);
        Assert.True(tavern.Shop[0].ShowStats);
        Assert.False(tavern.Shop[2].ShowStats);
        Assert.Equal("BGS_004 · 1/1 · tier 1", tavern.Shop[0].Tooltip);
        Assert.Equal("TB_BaconShop_HERO_50", tavern.Hero!.HeroCardId);
        Assert.Equal((30, 1), (tavern.Hero.Health, tavern.Hero.Tier));
        Assert.Equal(["TB_BaconShop_HP_050"], tavern.Hero.HeroPowers.Select(p => p.CardId));
        Assert.Null(tavern.Opponent);
        Assert.Empty(tavern.OpponentBoard);
        Assert.Null(tavern.ResultKey);
        // Heroes by card, never players; ours marked.
        Assert.Equal([("TB_BaconShop_HERO_50", 1, true), ("BG20_HERO_102", 2, false)], tavern.Leaderboard.Select(l => (l.HeroCardId, l.Place, l.Local)));
        Assert.Equal(2, tavern.Leaderboard[1].Tier);
        Assert.DoesNotContain(tavern.Leaderboard, l => l.Fighting);

        var buy = page.BattlegroundsStep(2)!;
        Assert.Equal("You bought BGS_039", buy.Action);
        Assert.True(Assert.Single(buy.Hand).Acting);
        Assert.Equal("0 / 3", buy.Gold);
        Assert.Equal(["BGS_004", "BG28_810"], buy.Shop.Select(c => c.CardId));
    }

    [Fact]
    public void A_combat_step_shows_the_opponent_and_marks_attacker_defender_and_the_dead()
    {
        var page = Page();
        var start = page.BattlegroundsStep(3)!;
        Assert.Equal(("Turn 1 · combat", "Combat against BG20_HERO_102"), (start.Title, start.Action));
        Assert.True(start.InCombat);
        Assert.Equal(string.Empty, start.Gold);
        Assert.Empty(start.Shop);
        Assert.Equal("BG20_HERO_102", start.Opponent!.HeroCardId);
        Assert.Equal(2, start.Opponent.Tier);
        Assert.Equal(["BGS_119", "BGS_061"], start.OpponentBoard.Select(c => c.CardId));
        Assert.Single(start.Leaderboard, l => l.Fighting && l.HeroCardId == "BG20_HERO_102");

        var attack = page.BattlegroundsStep(4)!;
        Assert.Equal("BG26_801 attacked BGS_119", attack.Action);
        Assert.True(Assert.Single(attack.Board).Attacking);
        Assert.True(attack.OpponentBoard[0].Defending);
        Assert.Contains("defending", attack.OpponentBoard[0].Tooltip);
        Assert.True(attack.OpponentBoard[0].Damaged);

        var deaths = page.BattlegroundsStep(5)!;
        Assert.Equal("Died: BGS_119", deaths.Action);
        Assert.True(Assert.Single(deaths.OpponentBoard, c => c.CardId == "BGS_119").Dying);
        Assert.DoesNotContain(page.BattlegroundsStep(6)!.OpponentBoard, c => c.CardId == "BGS_119");

        Assert.Equal("BG26_801 triggered", page.BattlegroundsStep(7)!.Action);
        Assert.Equal(9, page.BattlegroundsStep(7)!.Board[0].Attack);
    }

    [Fact]
    public void The_last_step_of_each_combat_has_its_result_and_the_game_ends_with_the_place()
    {
        var page = Page();
        var won = page.BattlegroundsStep(10)!;
        Assert.Equal(("Win", "You won", "5 damage"), (won.ResultKey, won.Action, won.Detail));
        // The boards as the fight left them: the cleared board shows with the next tavern.
        Assert.NotNull(won.Opponent);
        Assert.Equal("You upgraded the tavern to tier 2", page.BattlegroundsStep(12)!.Action);
        var lost = page.BattlegroundsStep(17)!;
        Assert.Equal(("Loss", "30 damage to you"), (lost.ResultKey, lost.Detail));
        Assert.Equal("BG20_HERO_102 attacked TB_BaconShop_HERO_50", page.BattlegroundsStep(16)!.Action);

        var end = page.BattlegroundsStep(18)!;
        Assert.Equal(("Game over", "Game over: 5th", MatchPhase.End), (end.Title, end.Action, end.Phase));
    }

    [Fact]
    public void Marks_stop_left_and_right_at_events_and_carry_the_step_s_line()
    {
        var page = Page();
        var marks = Private<IReadOnlyList<MatchStepMark>>(page, "BattlegroundsMarks");

        Assert.Equal(page.FrameCount, marks.Count);
        Assert.All(marks, m => Assert.Equal(page.BattlegroundsStep(m.Frame)!.Action, m.Caption));
        Assert.Equal(
            [MatchStepKind.Buy, MatchStepKind.CombatStart, MatchStepKind.Attack, MatchStepKind.Attack, MatchStepKind.Attack, MatchStepKind.CombatEnd,
                MatchStepKind.Upgrade, MatchStepKind.CombatStart, MatchStepKind.Attack, MatchStepKind.Attack, MatchStepKind.CombatEnd, MatchStepKind.End],
            marks.Where(m => m.IsEvent).Select(m => m.Kind));
        Assert.Equal([1, 1, 2, 2, 0], new[] { 2, 4, 12, 14, 0 }.Select(i => marks[i].Turn));
        // Their minion's attack and their hero's are not ours.
        Assert.Equal([false, false], new[] { 14, 16 }.Select(i => marks[i].Local));
        Assert.True(marks[4].Local);
    }

    [Fact]
    public void The_timeline_has_the_start_each_tavern_and_combat_and_the_end()
    {
        var page = Page();
        var spans = Private<IReadOnlyList<MatchTimelineSpan>>(page, "BattlegroundsSpans");

        Assert.Equal(
        [
            (0, 0, MatchPhase.Start, "", "Hero choice"),
            (1, 2, MatchPhase.Shop, "1", "Turn 1 · tavern, tier 1"),
            (3, 10, MatchPhase.Combat, "", "Turn 1 · combat against BG20_HERO_102 · won, 5 damage"),
            (11, 12, MatchPhase.Shop, "2", "Turn 2 · tavern, tier 2"),
            (13, 17, MatchPhase.Combat, "", "Turn 2 · combat against BG20_HERO_102 · lost, 30 damage to you"),
            (18, 18, MatchPhase.End, "", "Game over: 5th"),
        ], spans.Select(s => (s.FirstFrame, s.LastFrame, s.Phase, s.Label, s.Tooltip)));
        // The timeline colours a combat by the result on its last step.
        Assert.All(spans.Where(s => s.Phase == MatchPhase.Combat), s => Assert.NotNull(page.BattlegroundsStep(s.LastFrame)!.ResultKey));
    }

    [Fact]
    public void A_game_without_steps_has_no_battlegrounds_step_marks_or_spans()
    {
        var game = Record with { Replay = _store.Save(Record.Key, ReplayFile.WriteBattlegrounds(new ReplayHeader(), new BattlegroundsReplayData())) };
        var page = MatchPage.Load(game, _store, CardDb.Empty, null);

        Assert.False(page.HasBattlegroundsSteps);
        Assert.Equal(0, page.FrameCount);
        Assert.Null(page.BattlegroundsStep(0));
        Assert.Empty(Private<IReadOnlyList<MatchStepMark>>(page, "BattlegroundsMarks"));
        Assert.Empty(Private<IReadOnlyList<MatchTimelineSpan>>(page, "BattlegroundsSpans"));
    }

    /// <summary>The private members the timeline partial (P2) calls; their names are part of the P1/P2 contract.</summary>
    private static T Private<T>(MatchPage page, string name) =>
        (T)typeof(MatchPage).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, null)!;
}
