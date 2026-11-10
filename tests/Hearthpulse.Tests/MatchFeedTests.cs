using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// Plan R2 in the shared view model (Avalonia build): the Constructed game's event feed is built once per game, its rows
/// stay the same objects and each step only moves their marks; a feed row goes to its step. The arrow's geometry and the feed's scrolling rule
/// (<c>MatchArrowLayout</c>, <c>MatchFeedLayout</c>) shared by both UIs.
/// </summary>
public sealed class MatchFeedTests : IDisposable
{
    private readonly string _folder = TestDataFolder.Scratch("hp-match-feed");
    private readonly ReplayStore _store;

    public MatchFeedTests() => _store = new ReplayStore(Path.Combine(_folder, "replays"));

    public void Dispose() => TestDataFolder.TryDelete(_folder);

    private MatchViewModel Model()
    {
        Directory.CreateDirectory(_folder);
        return new MatchViewModel(new MainViewModel(new HistoryStore(Path.Combine(_folder, $"{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App),
            (_, token) => Task.Delay(Timeout.Infinite, token));
    }

    private MatchPage Page()
    {
        var name = _store.Save(MatchTimelineTests.Game(null).Key, ReplayFile.Write(new ReplayHeader(), MatchTimelineTests.Events));
        return MatchPage.Load(MatchTimelineTests.Game(name), _store, CardDb.Empty, null);
    }

    [Fact]
    public void The_feed_rows_stay_for_the_game_and_a_step_moves_only_the_marks()
    {
        var model = Model();
        var page = Page();
        model.Show(page);
        var rows = model.FeedRows;
        Assert.NotEmpty(rows);
        var objects = rows.ToList();
        var raised = new List<(MatchFeedItem Row, string? Name)>();
        foreach (var row in rows)
        {
            row.PropertyChanged += (_, e) => raised.Add((row, e.PropertyName));
        }

        for (var frame = 0; frame <= model.FrameMax; frame++)
        {
            var before = rows.Select(r => r.Current).ToList();
            raised.Clear();
            model.GoFrame.Execute(frame);

            // The list and the rows the view binds are the same objects; what they show is the page's feed for the
            // step: one action and its turn's header marked, the action's index told to the view (it scrolls to it).
            Assert.Same(rows, model.FeedRows);
            Assert.Equal(objects, model.FeedRows, ReferenceEqualityComparer.Instance);
            Assert.Equal(page.Feed(frame), model.Feed);
            Assert.Equal(page.Feed(frame), rows.Select(r => r.Row));
            Assert.Equal(frame, Assert.Single(rows, r => r.Current && !r.Header).Frame);
            Assert.Single(rows, r => r.Current && r.Header);
            Assert.Equal(objects.FindIndex(r => r.Current && !r.Header), model.FeedCurrentIndex);

            // Only the rows whose mark moved told their bindings, and only about the mark.
            var moved = objects.Where((r, i) => r.Current != before[i]).ToList();
            Assert.Equal(moved, raised.Select(r => r.Row), ReferenceEqualityComparer.Instance);
            Assert.All(raised, r => Assert.Equal(nameof(MatchFeedItem.Current), r.Name));
        }

        // Another side at the bottom does not touch the feed.
        raised.Clear();
        model.BottomOpponent = true;
        Assert.Empty(raised);
        Assert.Same(rows, model.FeedRows);

        model.Clear();
        Assert.Empty(model.FeedRows);
        Assert.Empty(model.Feed);
        Assert.Equal(-1, model.FeedCurrentIndex);
    }

    [Fact]
    public void A_feed_row_goes_to_its_step_and_a_turns_header_to_the_turns_first_step()
    {
        var model = Model();
        model.Show(Page());
        var raised = new List<string?>();
        model.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var action = model.Feed.Last(r => !r.Header);
        model.GoFrame.Execute(action);
        Assert.Equal(action.Frame, model.Frame);
        Assert.True(model.Feed.Single(r => r.Frame == action.Frame && !r.Header).Current);
        Assert.Contains(nameof(MatchViewModel.FeedCurrentIndex), raised);

        var header = model.Feed.First(r => r.Header && r.Frame > 0);
        model.GoFrame.Execute(header);
        Assert.Equal(header.Frame, model.Frame);
        Assert.True(model.Feed.Single(r => r.Header && r.Frame == header.Frame).Current);

        // The same step again changes nothing: no row and no notification.
        raised.Clear();
        model.GoFrame.Execute(header);
        Assert.DoesNotContain(nameof(MatchViewModel.FeedCurrentIndex), raised);

        // The rows the view binds go to their steps the same way (a row is the command's parameter).
        var item = model.FeedRows.Last(r => !r.Header);
        model.GoFrame.Execute(item);
        Assert.Equal(item.Frame, model.Frame);
        Assert.True(item.Current);
        Assert.Contains(nameof(MatchViewModel.FeedCurrentIndex), raised);
        Assert.Same(item, model.FeedRows[model.FeedCurrentIndex]);
    }

    [Fact]
    public void Loading_another_game_empties_the_feed()
    {
        var model = Model();
        model.Show(Page());
        Assert.NotEmpty(model.Feed);
        Assert.NotEmpty(model.FeedRows);
        model.BeginLoading();
        Assert.Empty(model.Feed);
        Assert.Empty(model.FeedRows);
    }

    [Fact]
    public void An_arrow_between_the_sides_is_straight_and_stops_off_both_ends()
    {
        // A minion of the top row attacks the hero below the step bar.
        var from = new MatchArrowPoint(100, 100);
        var to = new MatchArrowPoint(100, 400);
        var arrow = MatchArrowLayout.Build(from, MatchArrowAnchor.Minion, to, MatchArrowAnchor.Hero, middleY: 250, attack: true, local: true)!;

        Assert.Null(arrow.Control);
        Assert.Equal(new MatchArrowPoint(100, 120), arrow.Start);
        Assert.Equal(new MatchArrowPoint(100, 364), arrow.Tip);
        // The triangle's base sits 14 before the tip, 14 wide; the shaft ends inside it.
        Assert.Equal(350, arrow.HeadLeft.Y, 6);
        Assert.Equal(14, Math.Abs(arrow.HeadLeft.X - arrow.HeadRight.X), 6);
        Assert.InRange(arrow.ShaftEnd.Y, 350, 364);
        Assert.True(arrow.Attack && arrow.Local);
    }

    [Fact]
    public void An_arrow_on_one_side_bends_towards_the_step_bar_and_a_play_ends_in_a_chevron()
    {
        // Two minions of the bottom row (below the bar at 250): the curve bulges up, by 20 % of 200 = 40.
        var arrow = MatchArrowLayout.Build(new(100, 400), MatchArrowAnchor.Chip, new(300, 400), MatchArrowAnchor.Minion, middleY: 250, attack: false, local: false)!;

        Assert.Equal(new MatchArrowPoint(200, 360), arrow.Control);
        Assert.Equal(arrow.Tip, arrow.ShaftEnd);
        Assert.Equal(MatchArrowLayout.PlayArm, Distance(arrow.Tip, arrow.HeadLeft), 6);
        Assert.Equal(MatchArrowLayout.PlayArm, Distance(arrow.Tip, arrow.HeadRight), 6);

        // A long one bends at most 48; one too short for the insets is not drawn.
        var wide = MatchArrowLayout.Build(new(0, 100), MatchArrowAnchor.Minion, new(600, 100), MatchArrowAnchor.Minion, middleY: 250, attack: true, local: true)!;
        Assert.Equal(148, wide.Control!.Value.Y, 6);
        Assert.Null(MatchArrowLayout.Build(new(0, 0), MatchArrowAnchor.Minion, new(40, 0), MatchArrowAnchor.Minion, 250, true, true));
        Assert.Equal(new MatchArrowPoint(43, 46), MatchArrowLayout.Anchor(MatchArrowAnchor.Minion, 0, 0, 86, 129));
        Assert.Equal(new MatchArrowPoint(31, 31), MatchArrowLayout.Anchor(MatchArrowAnchor.Hero, 0, 0, 62, 62));
    }

    [Fact]
    public void The_feed_brings_a_row_out_of_sight_a_third_down_and_moves_a_row_near_an_edge_just_off_it()
    {
        // Below the visible 300: its top a third of the way down.
        Assert.Equal(500 + 600 - 100, MatchFeedLayout.Offset(500, 300, 600, 640));
        // Above it, clamped at the start.
        Assert.Equal(0, MatchFeedLayout.Offset(20, 300, -200, -160));
        // Visible but 10 from the bottom: just far enough to stand 40 off it.
        Assert.Equal(100 + 30, MatchFeedLayout.Offset(100, 300, 250, 290));
        // 20 from the top: up by 20.
        Assert.Equal(80, MatchFeedLayout.Offset(100, 300, 20, 60));
        // Well inside: stays.
        Assert.Null(MatchFeedLayout.Offset(100, 300, 120, 160));
        // A row that had to be built first was out of sight: a third down, wherever bringing it in left it.
        Assert.Equal(500 + 250 - 100, MatchFeedLayout.Brought(500, 300, 250));
        Assert.Equal(0, MatchFeedLayout.Brought(20, 300, 10));
    }

    /// <summary>
    /// Review M4: the timeline draws its tick marks itself. A mark stands where the layout put the two borders it was
    /// (a zone and the mark centred in it), on whole device pixels at 100, 125 and 150 %: the expected rectangle is
    /// worked out here step by step as the layouts do it (WPF rounds sizes to the nearest pixel, Avalonia up; both
    /// round a value half way to the even pixel).
    /// </summary>
    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    [InlineData(1.5, false)]
    [InlineData(1.0, true)]
    [InlineData(1.25, true)]
    [InlineData(1.5, true)]
    public void A_timeline_tick_mark_stands_on_device_pixels_where_its_borders_stood(double scale, bool avalonia)
    {
        const int count = 100;
        var halves = 0;
        foreach (var width in new[] { 900.0, 913.0, 1131.5 })
        {
            for (var frame = 0; frame < count; frame++)
            {
                var tick = new MatchTimelineTick((frame + 0.5) / count, frame, MatchTimelineLook.Mine, string.Empty);
                var (x, y, w, h) = MatchTimelineLayout.TickMark(tick, width, scale, sizesUp: avalonia);

                // The old layout: the zone's corner rounded, the sizes rounded, the mark's offset in the zone rounded.
                double Pixels(double v) => Math.Round(v * scale, MidpointRounding.ToEven);
                double SizePixels(double v) => avalonia ? Math.Ceiling(v * scale) : Pixels(v);
                var zoneLeft = (tick.At * width) - (MatchTimelineLayout.TickZone / 2);
                var left = Pixels(zoneLeft) + Pixels((SizePixels(8) - SizePixels(2)) / scale / 2);
                var top = Pixels(MatchTimelineLayout.TickTop) + Pixels((SizePixels(10) - SizePixels(8)) / scale / 2);
                Assert.Equal(left, x * scale, 6);
                Assert.Equal(top, y * scale, 6);
                Assert.Equal(SizePixels(2), w * scale, 6);
                Assert.Equal(SizePixels(8), h * scale, 6);

                // All four edges on whole device pixels: nothing is drawn between two of them.
                foreach (var edge in new[] { x, y, x + w, y + h })
                {
                    Assert.Equal(Math.Round(edge * scale), edge * scale, 6);
                }

                halves += Math.Abs((zoneLeft * scale) - Math.Floor(zoneLeft * scale) - 0.5) < 1e-9 ? 1 : 0;
            }
        }

        // The case the review named is in the set: a zone's edge exactly half way between two pixels (100 steps, 900 wide).
        Assert.True(scale != 1.0 || halves >= count, $"{halves} marks half way");

        // At 100 % a mark is 3 in from its zone's rounded corner and 1 down, 2 by 8, as before.
        if (scale == 1.0)
        {
            var tick = new MatchTimelineTick(0.305, 30, MatchTimelineLook.Theirs, string.Empty);
            Assert.Equal((Math.Round((0.305 * 900) - 4, MidpointRounding.ToEven) + 3, 5.0, 2.0, 8.0), MatchTimelineLayout.TickMark(tick, 900, 1, avalonia));
        }

        // Without layout rounding the mark is where the numbers put it.
        var plain = new MatchTimelineTick(0.3333, 33, MatchTimelineLook.Mine, string.Empty);
        Assert.Equal(((0.3333 * 900) - 4 + 3, 5.0, 2.0, 8.0), MatchTimelineLayout.TickMark(plain, 900, scale, avalonia, rounded: false));
    }

    private static double Distance(MatchArrowPoint a, MatchArrowPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
