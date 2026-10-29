using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// The match page's player bar (shared view model, Avalonia build): steps, events and turns, playback on a test clock,
/// the timeline, and the Battlegrounds rows built from contract records.
/// </summary>
public sealed class MatchPlayerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"hp-match-player-{Guid.NewGuid():N}");
    private readonly ReplayStore _store;

    public MatchPlayerTests() => _store = new ReplayStore(Path.Combine(_folder, "replays"));

    public void Dispose()
    {
        // The history store's connection pool may still hold its file for a moment.
        for (var attempt = 0; attempt < 20 && Directory.Exists(_folder); attempt++)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>The playback's waits: each one completes (a played step) or is cancelled (a pause) when the test says.</summary>
    private sealed class Clock
    {
        public List<(TimeSpan Wait, CancellationToken Token, TaskCompletionSource Done)> Waits { get; } = [];

        public Task Delay(TimeSpan wait, CancellationToken token)
        {
            var done = new TaskCompletionSource();
            token.Register(() => done.TrySetCanceled(token));
            Waits.Add((wait, token, done));
            return done.Task;
        }

        /// <summary>The second passes: the playback (waiting on the latest wait) goes on, here and now.</summary>
        public void Tick() => Waits[^1].Done.SetResult();
    }

    private MatchViewModel Model(out Clock clock)
    {
        Directory.CreateDirectory(_folder);
        clock = new Clock();
        return new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(_folder, $"{Guid.NewGuid():N}.db")), new AppSettings()), clock.Delay);
    }

    /// <summary>The Constructed game of <see cref="MatchTimelineTests.Events"/>: ten steps over the mulligan and three turns.</summary>
    private MatchPage Page()
    {
        var name = _store.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), MatchTimelineTests.Events));
        return MatchPage.Load(MatchTimelineTests.Game(name), _store, CardDb.Empty, null);
    }

    /// <summary>
    /// Without a synchronization context the playback's awaits resume inside <see cref="Clock.Tick"/>, so each tick is one
    /// step taken before it returns (the app resumes them on its UI thread).
    /// </summary>
    private static void WithoutContext(Action body)
    {
        var context = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }

    [Fact]
    public void Bar_goes_by_step_event_and_turn_and_says_where_it_is()
    {
        var model = Model(out _);
        model.Show(Page());

        Assert.True(model.ShowReplay && model.ShowConstructedTable && model.ShowSide && model.HasPlayer);
        Assert.False(model.ShowBattlegroundsTable || model.ShowBattlegroundsOldNote);
        Assert.Equal(9, model.FrameMax);
        Assert.Equal("Step 1 of 10", model.Position);

        model.NextEvent.Execute(null);
        Assert.Equal(1, model.Frame);
        model.NextEvent.Execute(null);
        Assert.Equal(3, model.Frame);
        Assert.Equal("Step 4 of 10 · turn 1", model.Position);
        model.Next.Execute(null);
        Assert.Equal(4, model.Frame);
        model.NextTurn.Execute(null);
        Assert.Equal(7, model.Frame);
        Assert.Equal("Step 8 of 10 · turn 2", model.Position);
        model.PreviousTurn.Execute(null);
        Assert.Equal(4, model.Frame);
        model.PreviousEvent.Execute(null);
        Assert.Equal(3, model.Frame);
        model.Previous.Execute(null);
        Assert.Equal(2, model.Frame);
        model.Last.Execute(null);
        Assert.Equal(9, model.Frame);
        model.First.Execute(null);
        Assert.Equal(0, model.Frame);
        model.GoFrame.Execute(model.FrameOf(ReplayActionKind.Attack));
        Assert.Equal(6, model.Frame);

        // The timeline: the mulligan, our turn, theirs, ours; the event ticks; the stretch and the line follow the step.
        Assert.Equal([MatchTimelineLook.Neutral, MatchTimelineLook.Mine, MatchTimelineLook.Theirs, MatchTimelineLook.Mine],
            model.TimelineSegments.Select(s => s.Look));
        Assert.Equal((0.0, 0.2), (model.TimelineSegments[0].Start, model.TimelineSegments[0].End));
        Assert.Equal(1.0, model.TimelineSegments[^1].End);
        Assert.Equal([1, 3, 5, 6, 8, 9], model.TimelineTicks.Select(t => t.Frame));
        Assert.Equal(2, model.TimelineCurrent);
        Assert.Equal(0.65, model.TimelinePosition, 6);

        // The "?" tooltip: the legend of the mode, then the keys.
        Assert.StartsWith(Loc.T("Match.Timeline.LegendConstructed"), model.HelpText);
        Assert.EndsWith(Loc.T("Match.Player.Help"), model.HelpText);
    }

    [Fact]
    public void Playback_steps_each_second_by_speed_and_pauses_on_the_last_step() => WithoutContext(() =>
    {
        var model = Model(out var clock);
        model.Show(Page());

        model.TogglePlay.Execute(null);
        Assert.True(model.Playing);
        Assert.Equal(Loc.T("Match.Player.Pause"), model.PlayTip);
        Assert.Equal("\U000F03E4", model.PlayGlyph);
        Assert.Equal(TimeSpan.FromSeconds(1), clock.Waits[^1].Wait);
        clock.Tick();
        Assert.Equal(1, model.Frame);

        // A faster speed counts from the next wait.
        model.CycleSpeed.Execute(null);
        clock.Tick();
        Assert.Equal(2, model.Frame);
        Assert.Equal(TimeSpan.FromSeconds(0.5), clock.Waits[^1].Wait);
        model.Faster.Execute(null);
        clock.Tick();
        Assert.Equal(TimeSpan.FromSeconds(0.25), clock.Waits[^1].Wait);

        // A step chosen by hand does not stop it; the last step does.
        model.GoFrame.Execute(7);
        Assert.True(model.Playing);
        clock.Tick();
        Assert.Equal(8, model.Frame);
        clock.Tick();
        Assert.Equal(9, model.Frame);
        Assert.False(model.Playing);
        Assert.Equal(Loc.T("Match.Player.Play"), model.PlayTip);
        Assert.Equal("\U000F040A", model.PlayGlyph);
        Assert.True(clock.Waits[^1].Done.Task.IsCompletedSuccessfully);

        // Play on the last step starts over from the first.
        model.TogglePlay.Execute(null);
        Assert.Equal(0, model.Frame);
        Assert.True(model.Playing);
        model.TogglePlay.Execute(null);
        Assert.False(model.Playing);
        Assert.True(clock.Waits[^1].Done.Task.IsCanceled);
        Assert.Equal(0, model.Frame);
    });

    [Fact]
    public void Playback_stops_on_another_tab_another_game_and_clear() => WithoutContext(() =>
    {
        var model = Model(out var clock);
        var page = Page();
        model.Show(page);

        model.TogglePlay.Execute(null);
        model.SetTab.Execute("Decks");
        Assert.False(model.Playing);
        Assert.True(clock.Waits[^1].Done.Task.IsCanceled);

        model.SetTab.Execute("Replay");
        model.TogglePlay.Execute(null);
        model.Show(page);
        Assert.False(model.Playing);
        Assert.True(clock.Waits[^1].Done.Task.IsCanceled);

        // The same game built again in another language keeps playing.
        model.TogglePlay.Execute(null);
        model.Show(page, keepPosition: true);
        Assert.True(model.Playing);

        // Closing the page (Back, another game loading) clears it: the wait is cancelled and nothing runs on.
        model.Clear();
        Assert.False(model.Playing);
        var (_, token, done) = clock.Waits[^1];
        Assert.True(token.IsCancellationRequested);
        Assert.True(done.Task.IsCanceled);
        var waits = clock.Waits.Count;
        Assert.Empty(model.TimelineSegments);
        model.TogglePlay.Execute(null);
        Assert.False(model.Playing);
        Assert.Equal(waits, clock.Waits.Count);
    });

    [Fact]
    public void Speed_keys_stop_at_the_ends_and_the_button_goes_round()
    {
        var model = Model(out _);
        Assert.Equal((1, "×1"), (model.Speed, model.SpeedText));
        model.Slower.Execute(null);
        Assert.Equal(1, model.Speed);
        model.Faster.Execute(null);
        model.Faster.Execute(null);
        model.Faster.Execute(null);
        Assert.Equal((4, "×4"), (model.Speed, model.SpeedText));
        model.Slower.Execute(null);
        Assert.Equal(2, model.Speed);
        model.CycleSpeed.Execute(null);
        Assert.Equal(4, model.Speed);
        model.CycleSpeed.Execute(null);
        Assert.Equal(1, model.Speed);
    }

    [Fact]
    public void Ticks_of_a_long_game_merge_into_at_most_300()
    {
        // 1 500 steps, every one an event: ours on even steps, theirs on odd; one step that is not an event.
        var marks = Enumerable.Range(0, 1500)
            .Select(i => new MatchStepMark(i, i / 10, MatchPhase.Turn, MatchStepKind.Play, i % 2 == 0, i != 7, $"Step {i}"))
            .ToList();

        var ticks = MatchViewModel.Ticks(marks, 1500);
        Assert.Equal(300, ticks.Count);
        Assert.All(ticks, t => Assert.Equal(MatchTimelineLook.Neutral, t.Look));
        Assert.Equal([0, 5, 10], ticks.Take(3).Select(t => t.Frame));
        Assert.Equal("Step 0\nStep 1\nStep 2\nand 2 more", ticks[0].Tooltip);
        Assert.Equal("Step 5\nStep 6\nStep 8\nand 1 more", ticks[1].Tooltip);
        Assert.Equal(0.5 / 1500, ticks[0].At, 9);
        // With at most ~100 stretches and the position line the bar stays within ~400 elements.
        Assert.True(ticks.Count + 100 + 1 <= 401);

        // All ours keeps our colour; a short game keeps a tick per event.
        Assert.All(MatchViewModel.Ticks(marks.Where(m => m.Local).ToList(), 1500), t => Assert.Equal(MatchTimelineLook.Mine, t.Look));
        Assert.All(MatchViewModel.Ticks(marks.Where(m => !m.Local).ToList(), 1500), t => Assert.Equal(MatchTimelineLook.Theirs, t.Look));
        var short_ = MatchViewModel.Ticks(marks.Take(40).ToList(), 40);
        Assert.Equal(39, short_.Count);
        Assert.Equal("Step 0", short_[0].Tooltip);
    }

    [Fact]
    public void Battlegrounds_combats_take_the_colour_of_their_result()
    {
        MatchTimelineSpan Span(int first, int last, MatchPhase phase, string label = "") => new(first, last, 1, phase, true, label, $"{phase} {first}");
        IReadOnlyList<MatchTimelineSpan> spans =
        [
            Span(0, 1, MatchPhase.Start), Span(2, 4, MatchPhase.Shop, "1"), Span(5, 7, MatchPhase.Combat), Span(8, 9, MatchPhase.Shop, "2"),
            Span(10, 12, MatchPhase.Combat), Span(13, 14, MatchPhase.Combat), Span(15, 15, MatchPhase.End),
        ];
        var results = new Dictionary<int, string> { [7] = "Win", [12] = "Loss", [14] = "Tie" };

        var segments = MatchViewModel.Segments(spans, 16, frame => results.GetValueOrDefault(frame));
        Assert.Equal(
            [MatchTimelineLook.Neutral, MatchTimelineLook.Shop, MatchTimelineLook.Won, MatchTimelineLook.Shop, MatchTimelineLook.Lost, MatchTimelineLook.Combat,
             MatchTimelineLook.Neutral],
            segments.Select(s => s.Look));
        Assert.Equal([false, false, true, false, true, true, false], segments.Select(s => s.IsCombat));
        Assert.Equal((2 / 16.0, 5 / 16.0), (segments[1].Start, segments[1].End));
        Assert.Equal(("1", "Shop 2", 2, 4), (segments[1].Label, segments[1].Tooltip, segments[1].FirstFrame, segments[1].LastFrame));
    }

    [Fact]
    public void Battlegrounds_leaderboard_and_the_events_of_a_combat_come_from_the_contract()
    {
        var fighting = MatchViewModel.Leader(new MatchBgLeaderRow("TB_BaconShop_HERO_08", "Hagatha", 3, 20, 5, 4, false, true, false));
        Assert.Equal(("3", "20 + 5", "4", false), (fighting.Place, fighting.Health, fighting.Tier, fighting.Local));
        Assert.Equal("Hagatha · Your opponent in this combat", fighting.Tooltip);
        var dead = MatchViewModel.Leader(new MatchBgLeaderRow("TB_BaconShop_HERO_01", "Bartendotron", 8, 0, 0, 3, false, false, true));
        Assert.Equal(("0", "Bartendotron · Out", true), (dead.Health, dead.Tooltip, dead.Dead));
        Assert.Equal("Rakanishu", MatchViewModel.Leader(new MatchBgLeaderRow("TB_BaconShop_HERO_75", "Rakanishu", 1, 30, 0, 5, true, false, false)).Tooltip);

        MatchStepMark Mark(int frame, MatchStepKind kind, bool local, bool isEvent) =>
            new(frame, 4, kind is MatchStepKind.Buy ? MatchPhase.Shop : MatchPhase.Combat, kind, local, isEvent, $"{kind} {frame}");
        IReadOnlyList<MatchStepMark> marks =
        [
            Mark(4, MatchStepKind.Buy, true, true), Mark(5, MatchStepKind.CombatStart, true, true), Mark(6, MatchStepKind.Attack, true, true),
            Mark(7, MatchStepKind.Deaths, false, false), Mark(8, MatchStepKind.Attack, false, true), Mark(9, MatchStepKind.CombatEnd, true, true),
        ];
        var combat = new MatchTimelineSegment(5 / 16.0, 10 / 16.0, 5, 9, MatchTimelineLook.Won, string.Empty, string.Empty);

        // The combat's events; on the deaths step the attack before it is the current row.
        var rows = MatchViewModel.StretchActions(marks, combat, 7);
        Assert.Equal([5, 6, 8, 9], rows.Select(r => r.Frame));
        Assert.Equal(6, Assert.Single(rows, r => r.Current).Frame);
        Assert.Equal(["\U000F0787", "\U000F04E5", "\U000F04E5", "\U000F023B"], rows.Select(r => r.Icon));
        Assert.Equal([true, true, false, true], rows.Select(r => r.Local));
        Assert.Equal("Attack 8", rows[2].Text);
        Assert.Null(rows[2].CardId);
    }

    [Fact]
    public void A_battlegrounds_file_of_format_3_keeps_its_rounds_and_lobby_with_a_note()
    {
        var data = new BattlegroundsReplayData
        {
            Rounds = [new BattlegroundsRound { Turn = 4, Tier = 3, Health = 28, Result = "Loss", DamageTaken = 6 }],
            Lobby = [new BattlegroundsLobbyHero("TB_BaconShop_HERO_08", 3, true)],
        };
        var game = MatchTimelineTests.Game(null) with { Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 3 };
        var name = _store.Save(game.Key, ReplayFile.WriteBattlegrounds(new ReplayHeader(), data));
        var model = Model(out _);
        model.Show(MatchPage.Load(game with { Replay = name }, _store, CardDb.Empty, null));

        Assert.True(model.IsBattlegrounds);
        Assert.False(model.HasBattlegroundsSteps || model.HasPlayer || model.ShowReplay || model.ShowSide);
        Assert.True(model.ShowRounds && model.ShowBattlegroundsOldNote);
        Assert.Empty(model.TimelineSegments);
        Assert.Null(model.BattlegroundsStep);
        model.TogglePlay.Execute(null);
        Assert.False(model.Playing);
        Assert.StartsWith(Loc.T("Match.Timeline.LegendBattlegrounds"), model.HelpText);
    }
}
