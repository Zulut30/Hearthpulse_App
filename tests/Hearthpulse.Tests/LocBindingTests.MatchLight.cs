using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>
/// The light Avalonia match page on a Constructed game with a feed of over 200 rows (as MatchPageLightTests does for
/// WPF): the feed builds only the rows near the view and scrolls to the step's row by its index; a step that changes
/// one minion's health keeps every card and feed row on screen; the arrow's geometry is worked out for a step or a new
/// size, never for a layout pass alone.
/// </summary>
public sealed partial class LocBindingTests
{
    /// <summary>
    /// The long game's page in a window tall enough for the whole feed: the list builds the rows inside its own view
    /// and inside the window, so a window that cut the feed off would hide the rows the tests look for.
    /// </summary>
    private static (MatchView View, MatchViewModel Model, Window Window, Avalonia.Styling.Styles Theme) OpenLongMatch(string folder, double height = 1500)
    {
        var opened = OpenMatch(folder, store =>
        {
            var game = MatchTimelineTests.Game(null) with { Turns = 90 };
            var name = store.Save(game.Key, ReplayFile.Write(new ReplayHeader(), MatchTableModelTests.LongGame()));
            return MatchPage.Load(game with { Replay = name }, store, CardDb.Empty, null);
        });
        opened.Window.Height = height;
        Settle(opened.Window);
        return opened;
    }

    /// <summary>Lets bindings, layout and the feed's scrolling run (it is posted after the step when the row is not built).</summary>
    private static void Settle(Window window)
    {
        for (var round = 0; round < 3; round++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static List<(int Index, Control Row)> BuiltFeedRows(ItemsControl feed) =>
        Enumerable.Range(0, feed.ItemCount)
            .Select(i => (Index: i, Row: feed.ContainerFromIndex(i)))
            .Where(r => r.Row is { IsVisible: true })
            .Select(r => (r.Index, r.Row!))
            .ToList();

    private static (double Top, double Bottom)? FeedPlace(Control row, ScrollViewer scroll) =>
        row.TranslatePoint(default, scroll) is { } at ? (at.Y, at.Y + row.Bounds.Height) : null;

    private static bool InFeedView(Control row, ScrollViewer scroll) => FeedPlace(row, scroll) is var (top, bottom) && bottom > 0 && top < scroll.Viewport.Height;

    private static bool FullyInFeedView(Control row, ScrollViewer scroll) =>
        FeedPlace(row, scroll) is var (top, bottom) && top >= -0.5 && bottom <= scroll.Viewport.Height + 0.5;

    /// <summary>A step past the opening whose attack is followed by another attack on the same target (the same turn's second hit).</summary>
    private static int TwoHits(MatchViewModel model)
    {
        for (var frame = 20; frame < model.FrameMax; frame++)
        {
            model.GoFrame.Execute(frame);
            if (model.Step is not { IsAttack: true, TargetRef: var target })
            {
                continue;
            }

            model.GoFrame.Execute(frame + 1);
            if (model.Step is { IsAttack: true } next && next.TargetRef == target)
            {
                return frame;
            }
        }

        throw new InvalidOperationException("the game has no two hits in a row");
    }

    [Fact]
    public void Match_feed_builds_the_rows_near_the_view_and_scrolls_to_the_steps_row() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-light-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenLongMatch(folder);
        try
        {
            var feed = view.FindControl<ItemsControl>("Feed")!;
            var scroll = view.FindControl<ScrollViewer>("FeedScroll")!;
            Assert.True(model.FeedRows.Count >= 200);
            Assert.Equal(model.FeedRows.Count, feed.ItemCount);

            // Opened on the first step: the rows on screen and at most two more screens of them.
            var built = BuiltFeedRows(feed);
            var visible = built.Count(r => InFeedView(r.Row, scroll));
            Assert.True(visible > 0);
            Assert.True(built.Count <= visible * 3 + 2, $"{built.Count} rows built for {visible} on screen");
            Assert.True(built.Count < feed.ItemCount / 2, $"{built.Count} of {feed.ItemCount} rows built");

            // The last step: its row was not built; the list brings it in by its index and it stands inside the view.
            Assert.DoesNotContain(built, r => r.Index == model.FeedRows.Count - 1);
            model.GoFrame.Execute(model.FrameMax);
            Settle(window);
            var current = model.FeedCurrentIndex;
            Assert.True(current > 150);
            var row = Assert.IsAssignableFrom<Control>(feed.ContainerFromIndex(current));
            Assert.Same(model.FeedRows[current], row.DataContext);
            Assert.True(FullyInFeedView(row, scroll), "the step's row is not inside the feed's view");
            built = BuiltFeedRows(feed);
            visible = built.Count(r => InFeedView(r.Row, scroll));
            Assert.True(built.Count <= visible * 3 + 2, $"{built.Count} rows built for {visible} on screen after the jump");

            // A turn's first action comes into view with the turn's header over it (MatchFeedLayout).
            var first = Enumerable.Range(1, model.FeedRows.Count - 1).First(i => i > 40 && i < 80 && model.FeedRows[i - 1].Header && !model.FeedRows[i].Header);
            model.GoFrame.Execute(model.FeedRows[first]);
            Settle(window);
            Assert.Equal(first, model.FeedCurrentIndex);
            Assert.True(FullyInFeedView(Assert.IsAssignableFrom<Control>(feed.ContainerFromIndex(first)), scroll));
            Assert.True(FullyInFeedView(Assert.IsAssignableFrom<Control>(feed.ContainerFromIndex(first - 1)), scroll),
                "the turn's header is not in view over its first action");
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });

    /// <summary>
    /// The page scrolls in the main window, and the feed may be partly under its edge: bringing the step's row into the
    /// feed's view never scrolls the page itself.
    /// </summary>
    [Fact]
    public void Match_feed_scrolls_itself_and_never_the_page() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-light-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenLongMatch(folder, height: 700);
        try
        {
            window.Content = null;
            var page = new ScrollViewer { Content = view };
            window.Content = page;
            Settle(window);
            Assert.True(page.Extent.Height > page.Viewport.Height + 100, "the page does not scroll");
            var feed = view.FindControl<ItemsControl>("Feed")!;
            var scroll = view.FindControl<ScrollViewer>("FeedScroll")!;
            foreach (var offset in new[] { 0.0, 180.0 })
            {
                page.Offset = new Vector(0, offset);
                Settle(window);
                var at = page.Offset;
                foreach (var frame in new[] { model.FrameMax, model.FrameMax / 2, model.FrameMax / 2 + 1, model.FrameMax / 2 + 2, 3, model.FrameMax - 1 })
                {
                    model.GoFrame.Execute(frame);
                    Settle(window);
                    Assert.Equal(at, page.Offset);
                    // The feed followed the step: its row is built whenever it is inside the window too, and then inside the feed's view.
                    if (feed.ContainerFromIndex(model.FeedCurrentIndex) is { IsVisible: true } row)
                    {
                        Assert.True(InFeedView(row, scroll));
                    }
                }

                Assert.True(scroll.Offset.Y > 0);
            }
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });

    [Fact]
    public void Match_step_that_changes_a_minions_health_keeps_every_card_and_feed_row() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-light-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenLongMatch(folder);
        try
        {
            var table = view.FindControl<Border>("TableCard")!;
            var feed = view.FindControl<ItemsControl>("Feed")!;

            // Two hits of the same turn: the second only takes one more health off the same minion.
            var frame = TwoHits(model);
            model.GoFrame.Execute(frame);
            Settle(window);
            var sides = new[] { model.Table.Top, model.Table.Bottom };
            var target = sides.SelectMany(s => s.Board).Single(c => c.Targeted);
            var health = target.Health;
            List<ItemsControl> Lists() => table.GetVisualDescendants().OfType<ItemsControl>().Where(i => i.IsEffectivelyVisible && i.ItemCount > 0 && i.ItemsPanelRoot is not null).ToList();
            static IEnumerable<Control> Containers(ItemsControl list) => Enumerable.Range(0, list.ItemCount).Select(i => Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(i)));
            var lists = Lists();
            var cards = lists.SelectMany(Containers).ToList();
            Assert.Equal(sides.Sum(s => s.Board.Count + s.Hand.Count + s.Secrets.Count), cards.Count);
            var targetRow = cards.Single(c => ReferenceEquals(c.DataContext, target));
            string Text(int value) => value.ToString(System.Globalization.CultureInfo.CurrentCulture);
            Assert.Contains(Text(health), targetRow.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
            var rows = BuiltFeedRows(feed);
            var current = model.FeedCurrentIndex;

            model.GoFrame.Execute(frame + 1);
            Settle(window);

            // The table: every card's container is the same object, the health shown is the new one.
            Assert.Equal(cards, Lists().SelectMany(Containers), ReferenceEqualityComparer.Instance);
            Assert.Equal(health - 1, target.Health);
            var shown = targetRow.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains(Text(health - 1), shown);
            Assert.DoesNotContain(Text(health), shown);

            // The feed: the mark went to the next row; the rows built before are the same containers on the same rows.
            Assert.Equal(current + 1, model.FeedCurrentIndex);
            Assert.False(model.FeedRows[current].Current);
            Assert.True(model.FeedRows[current + 1].Current);
            var after = BuiltFeedRows(feed).ToDictionary(r => r.Index, r => r.Row);
            var kept = rows.Where(r => after.ContainsKey(r.Index)).ToList();
            // (The feed may have scrolled: the rows that went out of its reach are let go, the others stay.)
            Assert.True(kept.Count > 0 && kept.Exists(r => r.Index == current),
                $"the feed built its rows again: {rows[0].Index}-{rows[^1].Index} before, {after.Keys.Min()}-{after.Keys.Max()} after, the step's row was {current}");
            Assert.All(kept, r => Assert.Same(r.Row, after[r.Index]));
            Assert.Same(model.FeedRows[current + 1], after[current + 1].DataContext);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });

    [Fact]
    public void Match_arrow_is_worked_out_for_a_step_or_a_new_size_and_not_for_a_layout_pass() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-light-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenLongMatch(folder);
        try
        {
            var layer = view.GetVisualDescendants().OfType<MatchArrowLayer>().Single();
            Rect End(int id)
            {
                var element = view.GetVisualDescendants().OfType<Control>().First(c => MatchArrowLayer.GetRef(c) == id && c.IsEffectivelyVisible);
                return new Rect(element.TranslatePoint(default, layer)!.Value, element.Bounds.Size);
            }

            var frame = TwoHits(model);
            model.GoFrame.Execute(frame);
            Settle(window);
            Assert.True(layer.Arrow() is { Attack: true });

            // A step with an attack: the arrow joins the attacker and its target, worked out at most twice.
            var computed = layer.Computed;
            model.GoFrame.Execute(frame + 1);
            Settle(window);
            var arrow = layer.Arrow();
            Assert.True(arrow is { Attack: true });
            Assert.InRange(layer.Computed - computed, 1, 2);
            var (from, to) = (End(model.Table.ActorRef), End(model.Table.TargetRef));
            Assert.InRange(arrow!.Start.X, from.Left, from.Right);
            Assert.InRange(arrow.Tip.X, to.Left, to.Right);
            Assert.True(Math.Abs(arrow.Tip.Y - arrow.Start.Y) > 40);

            // Layout passes without a step or a new size (the page and a text elsewhere on it are measured again).
            computed = layer.Computed;
            for (var pass = 0; pass < 3; pass++)
            {
                view.FindControl<Border>("FeedCard")!.InvalidateMeasure();
                view.InvalidateMeasure();
                view.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible).InvalidateMeasure();
                Settle(window);
            }

            Assert.Equal(computed, layer.Computed);
            Assert.Equal(arrow, layer.Arrow());

            // A new size: the cards move, the arrow follows.
            window.Width -= 120;
            Settle(window);
            Assert.True(layer.Computed > computed);
            Assert.True(layer.Arrow() is { Attack: true });
            var moved = End(model.Table.ActorRef);
            Assert.InRange(layer.Arrow()!.Start.X, moved.Left, moved.Right);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });

    /// <summary>
    /// The Battlegrounds game of <see cref="BattlegroundsReplayTests.Game5th"/> with a long first combat: fourteen more
    /// attacks, so its list of events is taller than the list's view, and the tavern after it has one event.
    /// </summary>
    internal static string LongCombat()
    {
        var (state, recorder, tracker) = BattlegroundsReplayTests.Play(BattlegroundsReplayTests.Game5th());
        var text = recorder.Battlegrounds.Text(new ReplayHeader(), tracker.ReplayData(state)).ReplaceLineEndings("\n");
        const string attack = "M ATTACK 6 8\n";
        var at = text.IndexOf(attack, StringComparison.Ordinal);
        Assert.True(at > 0, "the recorded game has no first attack line");
        // The header counts the file's event lines: 28 more.
        var count = System.Text.RegularExpressions.Regex.Match(text, "\"events\":(\\d+)");
        Assert.True(count.Success, "the recorded game's header has no event count");
        var events = int.Parse(count.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) + 28;
        return text.Insert(at, string.Concat(Enumerable.Repeat(attack + "V 8 DAMAGE 1\n", 14)))
            .Replace(count.Value, FormattableString.Invariant($"\"events\":{events}"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Review M2: a step back across a stretch's border (from a tavern with one event to the last step of the combat
    /// before it, whose marked event is far down a longer list) shows other rows. The list scrolls to the marked one
    /// after they are laid out: scrolled at once, it used the last stretch's layout and left the event out of sight.
    /// </summary>
    [Fact]
    public void Match_battlegrounds_events_keep_the_marked_one_in_view_across_a_stretchs_border() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-light-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder, store =>
        {
            var game = MatchTimelineTests.Game(null) with { Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5 };
            return MatchPage.Load(game with { Replay = store.Save(game.Key, LongCombat()) }, store, CardDb.Empty, null);
        });
        try
        {
            window.Height = 1500;
            Settle(window);
            Assert.True(model.ShowBattlegroundsTable);
            var list = view.FindControl<ItemsControl>("BgActions")!;
            var scroll = view.FindControl<ScrollViewer>("BgActionsScroll")!;

            // The first tavern step after the first combat, then one step back.
            var tavern = Enumerable.Range(1, model.FrameMax).First(f =>
            {
                model.GoFrame.Execute(f - 1);
                var before = model.BattlegroundsStep!.InCombat;
                model.GoFrame.Execute(f);
                return before && !model.BattlegroundsStep!.InCombat;
            });
            Settle(window);
            Assert.True(model.ActionRows.Count <= 3);
            Assert.Equal(0, scroll.Offset.Y);

            model.Previous.Execute(null);
            Settle(window);
            Assert.Equal(tavern - 1, model.Frame);
            var marked = model.ActionCurrentIndex;
            Assert.True(marked >= 14, $"the combat's marked event is row {marked}");
            var row = Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(marked));
            Assert.Same(model.ActionRows[marked], row.DataContext);
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "the combat's list does not scroll");
            Assert.True(FullyInFeedView(row, scroll), $"the marked event is out of the list's view (offset {scroll.Offset.Y:0} of {scroll.Extent.Height:0}, view {scroll.Viewport.Height:0})");

            // Within the stretch only the mark moves: the list follows it in the step's own frame.
            model.GoFrame.Execute(model.ActionRows[1].Frame);
            Settle(window);
            Assert.True(FullyInFeedView(Assert.IsAssignableFrom<Control>(list.ContainerFromIndex(model.ActionCurrentIndex)), scroll));
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });

    /// <summary>
    /// QA Q5 / review m1: the table is hidden with an ancestor (another tab of the page), a step changes and the page is
    /// built again in another language meanwhile. The arrow is not worked out on cards that are not laid out, and when
    /// the table comes back it joins the step's attacker and target where they stand.
    /// </summary>
    [Fact]
    public void Match_arrow_waits_while_the_table_is_hidden_and_is_right_when_it_comes_back() => Headless.Run(() =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-light-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenLongMatch(folder);
        try
        {
            var layer = view.GetVisualDescendants().OfType<MatchArrowLayer>().Single();
            Rect End(int id)
            {
                var element = view.GetVisualDescendants().OfType<Control>().First(c => MatchArrowLayer.GetRef(c) == id && c.IsEffectivelyVisible);
                return new Rect(element.TranslatePoint(default, layer)!.Value, element.Bounds.Size);
            }

            var frame = TwoHits(model);
            // An opponent's attack first (top to bottom), shown and settled.
            model.GoFrame.Execute(frame);
            Settle(window);
            var shown = layer.Arrow();
            Assert.True(shown is { Attack: true });

            // The Decks tab hides the table. A step to our own attack (bottom to top) and the page rebuilt, as a change
            // of the language does: nothing is worked out while the cards are not on screen.
            model.ShowDecks = true;
            Settle(window);
            Assert.False(layer.IsEffectivelyVisible);
            var computed = layer.Computed;
            var other = Enumerable.Range(frame + 2, 12).First(f =>
            {
                model.GoFrame.Execute(f);
                return model.Step is { IsAttack: true } step && step.LocalActs != shown!.Local;
            });
            Settle(window);
            Assert.NotNull(view.DataContext);
            model.GoFrame.Execute(other);
            Settle(window);
            Assert.Equal(computed, layer.Computed);

            model.ShowReplay = true;
            Settle(window);
            Assert.True(layer.IsEffectivelyVisible);
            Assert.True(layer.Computed > computed);
            var arrow = layer.Arrow();
            Assert.True(arrow is { Attack: true });
            Assert.Equal(model.Table.LocalActs, arrow!.Local);
            Assert.NotEqual(shown, arrow);
            var (from, to) = (End(model.Table.ActorRef), End(model.Table.TargetRef));
            Assert.InRange(arrow.Start.X, from.Left, from.Right);
            Assert.InRange(arrow.Start.Y, from.Top, from.Bottom);
            Assert.InRange(arrow.Tip.X, to.Left, to.Right);
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    });
}
