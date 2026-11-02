using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// Plan R2 in the shared view model (Avalonia build): the Constructed game's event feed is built once per game and each
/// step only moves its marks in place; a feed row goes to its step. The arrow's geometry and the feed's scrolling rule
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
    public void The_feed_is_the_pages_feed_at_every_step_and_changes_only_the_rows_whose_mark_moves()
    {
        var model = Model();
        var page = Page();
        model.Show(page);
        var feed = model.Feed;
        Assert.NotEmpty(feed);

        for (var frame = 0; frame <= model.FrameMax; frame++)
        {
            var before = model.Feed.ToList();
            model.GoFrame.Execute(frame);

            // The same list the view binds, equal to the page's feed for the step: one action and its turn's header marked.
            Assert.Same(feed, model.Feed);
            Assert.Equal(page.Feed(frame), model.Feed);
            Assert.Equal(frame, Assert.Single(model.Feed, r => r.Current && !r.Header).Frame);
            Assert.Single(model.Feed, r => r.Current && r.Header);

            // A row whose mark did not move keeps its instance (its container on screen is not built again).
            for (var i = 0; i < before.Count; i++)
            {
                if (before[i].Current == model.Feed[i].Current)
                {
                    Assert.Same(before[i], model.Feed[i]);
                }
            }
        }

        // Another side at the bottom does not touch the feed.
        var rows = model.Feed.ToList();
        model.BottomOpponent = true;
        Assert.Equal(rows.Select(r => (object)r), model.Feed.Select(r => (object)r), ReferenceEqualityComparer.Instance);

        model.Clear();
        Assert.Empty(feed);
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
        Assert.Contains(nameof(MatchViewModel.Feed), raised);

        var header = model.Feed.First(r => r.Header && r.Frame > 0);
        model.GoFrame.Execute(header);
        Assert.Equal(header.Frame, model.Frame);
        Assert.True(model.Feed.Single(r => r.Header && r.Frame == header.Frame).Current);

        // The same step again changes nothing: no row and no notification.
        raised.Clear();
        model.GoFrame.Execute(header);
        Assert.DoesNotContain(nameof(MatchViewModel.Feed), raised);
    }

    [Fact]
    public void Loading_another_game_empties_the_feed()
    {
        var model = Model();
        model.Show(Page());
        Assert.NotEmpty(model.Feed);
        model.BeginLoading();
        Assert.Empty(model.Feed);
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
    }

    private static double Distance(MatchArrowPoint a, MatchArrowPoint b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
