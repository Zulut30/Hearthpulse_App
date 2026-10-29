using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;
using Bg = Hearthpulse.Tests.BattlegroundsReplayTests;

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

    /// <summary>A Battlegrounds game recorded step by step (format 4) as the app saves it, or the same file marked cut short.</summary>
    private MatchPage BattlegroundsPage(string key, string[] bodies, bool truncated = false)
    {
        var (state, recorder, tracker) = Bg.Play(bodies);
        var text = recorder.Battlegrounds.Text(new ReplayHeader(), tracker.ReplayData(state));
        if (truncated)
        {
            var (header, body) = ReplayFile.Read(text)!.Value;
            text = ReplayFile.Write(header with { Truncated = true }, body);
        }

        var game = MatchTimelineTests.Game(null) with { Key = key, Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5 };
        return MatchPage.Load(game with { Replay = _store.Save(key, text) }, _store, CardDb.Empty, null);
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
    public void Playback_stops_when_the_window_is_hidden_and_keeps_the_step() => WithoutContext(() =>
    {
        var model = Model(out var clock);
        model.Show(Page());
        model.TogglePlay.Execute(null);
        clock.Tick();
        Assert.Equal(1, model.Frame);

        // The window hidden to the tray (the views call it): the wait is cancelled, no step comes, the page stays.
        model.StopPlayback();
        Assert.False(model.Playing);
        var (_, token, done) = clock.Waits[^1];
        Assert.True(token.IsCancellationRequested);
        Assert.True(done.Task.IsCanceled);
        var waits = clock.Waits.Count;
        Assert.Equal(1, model.Frame);
        Assert.NotEmpty(model.TimelineSegments);

        // Stopping again is harmless; play goes on from the same step once the window is back.
        model.StopPlayback();
        Assert.Equal(waits, clock.Waits.Count);
        model.TogglePlay.Execute(null);
        Assert.True(model.Playing);
        Assert.Equal(waits + 1, clock.Waits.Count);
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
    public void A_battlegrounds_game_plays_step_by_step_with_combats_in_their_result_colour()
    {
        var model = Model(out _);
        var page = BattlegroundsPage("bg-5th", Bg.Game5th());
        model.Show(page);

        Assert.True(model.ShowReplay && model.ShowBattlegroundsTable && model.HasPlayer);
        Assert.False(model.ShowConstructedTable || model.ShowSide || model.ShowBattlegroundsOldNote || model.Truncated);
        Assert.Equal(page.FrameCount - 1, model.FrameMax);
        // Won on turn 1, lost on turn 2: each combat takes the result of its last step.
        Assert.Equal([MatchTimelineLook.Won, MatchTimelineLook.Lost], model.TimelineSegments.Where(s => s.IsCombat).Select(s => s.Look));

        // Every step is drawn on the table; the list beside it holds the events of the step's stretch, the latest one so
        // far marked.
        for (var frame = 0; frame <= model.FrameMax; frame++)
        {
            model.GoFrame.Execute(frame);
            Assert.Equal(frame, model.BattlegroundsStep!.Index);
            var stretch = model.TimelineSegments[model.TimelineCurrent];
            Assert.InRange(frame, stretch.FirstFrame, stretch.LastFrame);
            Assert.All(model.Actions, a => Assert.InRange(a.Frame, stretch.FirstFrame, stretch.LastFrame));
            var latest = page.Marks.LastOrDefault(m => m.IsEvent && m.Frame >= stretch.FirstFrame && m.Frame <= frame)?.Frame;
            Assert.Equal(latest, model.Actions.SingleOrDefault(a => a.Current)?.Frame);
        }

        // Ctrl+arrows go tavern to tavern over the combats.
        model.First.Execute(null);
        model.NextTurn.Execute(null);
        Assert.Equal(MatchPhase.Shop, model.BattlegroundsStep!.Phase);
        model.NextTurn.Execute(null);
        Assert.Equal((MatchPhase.Shop, 2), (model.BattlegroundsStep!.Phase, model.BattlegroundsStep.Turn));
    }

    /// <summary>
    /// A step changes the table's lists in place: the items controls keep the rows of cards, leaders and events that did
    /// not change. A new list on every step built every row again, about 50 ms and 3.5 MB a step (audit, stage 10).
    /// </summary>
    [Fact]
    public void Each_battlegrounds_step_changes_the_lists_on_screen_in_place()
    {
        var model = Model(out _);
        var page = BattlegroundsPage("bg-5th", Bg.Game5th());
        model.Show(page);
        var first = model.BattlegroundsStep!;
        var lists = new object[] { first.Board, first.Shop, first.Hand, first.OpponentBoard, first.Choices, model.Leaders, model.Actions, model.Turns };
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var before = first;
        var powers = first.Hero?.HeroPowers;
        for (var frame = 1; frame <= model.FrameMax; frame++)
        {
            model.GoFrame.Execute(frame);
            var step = model.BattlegroundsStep!;
            var shown = page.BattlegroundsStep(frame)!;
            Assert.Equal(lists, new object[] { step.Board, step.Shop, step.Hand, step.OpponentBoard, step.Choices, model.Leaders, model.Actions, model.Turns });
            Assert.Equal(shown.Board, step.Board);
            Assert.Equal(shown.Shop, step.Shop);
            Assert.Equal(shown.Hand, step.Hand);
            Assert.Equal(shown.OpponentBoard, step.OpponentBoard);
            Assert.Equal(shown.Choices.Count, step.Choices.Count);
            Assert.Equal(shown.Leaderboard.Select(MatchViewModel.Leader), model.Leaders);
            Assert.Equal(shown.Hero?.HeroPowers ?? [], step.Hero?.HeroPowers ?? []);
            Assert.Equal(shown.Hero?.Trinkets ?? [], step.Hero?.Trinkets ?? []);
            if (step.Hero is { } hero)
            {
                powers ??= hero.HeroPowers;
                Assert.Same(powers, hero.HeroPowers);
            }

            // A card equal to the one shown at its place a step before is still that row.
            var board = step.Board.ToList();
            for (var i = 0; i < Math.Min(board.Count, before.Board.Count) && board[i] == before.Board[i]; i++)
            {
                Assert.Same(before.Board[i], board[i]);
            }

            before = step with { Board = board };
        }

        // The views keep the current event in view when the list says it changed.
        Assert.Contains(nameof(MatchViewModel.Actions), raised);

        // Leaving the game lets go of its rows.
        model.Clear();
        Assert.Null(model.BattlegroundsStep);
        Assert.All(lists, list => Assert.Empty((System.Collections.IEnumerable)list));
    }

    [Fact]
    public void A_minion_used_on_the_table_is_an_event_with_the_power_icon()
    {
        var model = Model(out _);
        var page = BattlegroundsPage("bg-use",
        [
            .. Bg.Setup,
            .. Bg.Entity(60, "BG36_311", Bg.Me, "PLAY", "MINION", "ATK=2", "HEALTH=2", "ZONE_POSITION=1"),
            .. Bg.Entity(61, "BG34_604", Bg.Me, "HAND", "MINION", "ATK=3", "HEALTH=3", "ZONE_POSITION=1"),
            Bg.Game("STEP", "MAIN_END"),
            Bg.Start("PLAY", 60), Bg.Tag(60, "CARD_TARGET", 61),
            Bg.Start("POWER", 60), Bg.Tag(61, "ZONE", "REMOVEDFROMGAME"), Bg.Tag(60, "ATK", 5), Bg.Tag(60, "HEALTH", 5), Bg.End,
            Bg.Start("PLAY", 40), Bg.Tag(40, "CARD_TARGET", 51), Bg.Tag(51, "CONTROLLER", Bg.Me), Bg.Tag(51, "ZONE", "HAND"), Bg.End,
        ]);
        model.Show(page);
        var use = page.Marks.Single(m => m.Kind == MatchStepKind.Use);

        // A tick on the timeline, an arrow stop, a row of the stretch's list with the hero power's icon.
        Assert.True(use.IsEvent && use.Local);
        Assert.Contains(model.TimelineTicks, t => t.Frame == use.Frame);
        model.GoFrame.Execute(use.Frame - 1);
        model.NextEvent.Execute(null);
        Assert.Equal(use.Frame, model.Frame);
        var row = model.Actions.Single(a => a.Current);
        Assert.Equal((use.Frame, "󱐋", use.Caption), (row.Frame, row.Icon, row.Text));
        Assert.Equal(MatchStepKind.Use, model.BattlegroundsStep!.Kind);
    }

    [Fact]
    public void A_replay_cut_short_says_so_in_both_modes()
    {
        var model = Model(out _);
        var cut = BattlegroundsPage("bg-cut", Bg.Game5th(), truncated: true);
        Assert.True(cut.Truncated);
        Assert.False(BattlegroundsPage("bg-whole", Bg.Game5th()).Truncated);

        model.Show(cut);
        Assert.True(model.HasBattlegroundsSteps && model.ShowBattlegroundsTable);
        Assert.True(model.Truncated);

        // A Constructed game reads its own header.
        var name = _store.Save("constructed-cut", ReplayFile.Write(new ReplayHeader { Truncated = true }, MatchTimelineTests.Events));
        model.Show(MatchPage.Load(MatchTimelineTests.Game(name), _store, CardDb.Empty, null));
        Assert.True(model.ShowConstructedTable && model.Truncated);
        model.Show(Page());
        Assert.False(model.Truncated);
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
