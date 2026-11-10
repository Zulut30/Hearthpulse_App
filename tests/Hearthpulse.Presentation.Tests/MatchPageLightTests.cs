using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The light WPF match page on a Constructed game with a feed of over 200 rows: the feed builds only the rows near the
/// view and scrolls to the step's row by its index; a step that changes one minion's health keeps every card and feed
/// row on screen (the same containers); the arrow's geometry is worked out for a step or a new size, never for a
/// layout pass alone.
/// </summary>
public sealed class MatchPageLightTests
{
    /// <summary>
    /// Both sides play a minion, then each turn the side's minion hits the other one twice: the damage grows, nobody
    /// dies, the table's cards stay the same (as MatchTableModelTests.LongGame of the Avalonia suite).
    /// </summary>
    internal static string[] LongGame(int turns = 90)
    {
        var events = new List<string>
        {
            "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30",
            "S BEGIN_MULLIGAN", "S MAIN_READY",
            "V L RESOURCES 1", "B", "E 3 L PLAY 1 CS2_120 MINION ATK=1 HEALTH=900", "P 3 -",
            "T 2 O 30", "V O RESOURCES 2", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=1 HEALTH=900", "P 4 -",
        };
        var damage = new Dictionary<int, int> { [3] = 0, [4] = 0 };
        for (var turn = 3; turn <= turns; turn++)
        {
            var local = turn % 2 == 1;
            var (attacker, target) = local ? (3, 4) : (4, 3);
            events.Add(FormattableString.Invariant($"T {turn} {(local ? 'L' : 'O')} {turn * 30}"));
            for (var hit = 0; hit < 2; hit++)
            {
                events.Add(FormattableString.Invariant($"A {attacker} {target}"));
                events.Add(FormattableString.Invariant($"V {target} DAMAGE {++damage[target]}"));
            }
        }

        events.Add("R WON");
        return [.. events];
    }

    [Fact]
    public void Wpf_feed_builds_the_rows_near_the_view_and_scrolls_to_the_steps_row() => OnPage((model, view, window) =>
    {
        var feed = (ItemsControl)view.FindName("Feed");
        var scroll = view.FeedScroll!;
        var page = (ScrollViewer)window.Content;
        Assert.True(page.ExtentHeight > page.ViewportHeight + 100, "the page does not scroll");
        page.ScrollToVerticalOffset(120);
        Settle(window);
        var pageOffset = page.VerticalOffset;
        Assert.True(pageOffset > 0);
        Assert.True(model.FeedRows.Count >= 200);
        Assert.Equal(model.FeedRows.Count, feed.Items.Count);

        // Opened on the first step: the rows on screen and at most two more screens of them.
        var built = Built(feed);
        var visible = built.Count(r => InView(r.Row, scroll));
        Assert.True(visible > 0);
        Assert.True(built.Count <= visible * 3 + 2, $"{built.Count} rows built for {visible} on screen");
        Assert.True(built.Count < feed.Items.Count / 2, $"{built.Count} of {feed.Items.Count} rows built");

        // The last step: its row was not built; the list brings it in by its index and it stands inside the view.
        Assert.Null(feed.ItemContainerGenerator.ContainerFromIndex(model.FeedRows.Count - 1));
        model.GoFrame.Execute(model.FrameMax);
        Settle(window);
        var current = model.FeedCurrentIndex;
        Assert.True(current > 150);
        var row = Assert.IsAssignableFrom<FrameworkElement>(feed.ItemContainerGenerator.ContainerFromIndex(current));
        Assert.Same(model.FeedRows[current], row.DataContext);
        Assert.True(FullyInView(row, scroll), "the step's row is not inside the feed's view");
        built = Built(feed);
        visible = built.Count(r => InView(r.Row, scroll));
        Assert.True(built.Count <= visible * 3 + 2, $"{built.Count} rows built for {visible} on screen after the jump");

        // A turn's first action comes into view with the turn's header over it (MatchFeedLayout).
        var first = Enumerable.Range(1, model.FeedRows.Count - 1).First(i => i > 40 && i < 80 && model.FeedRows[i - 1].Header && !model.FeedRows[i].Header);
        model.GoFrame.Execute(model.FeedRows[first]);
        Settle(window);
        Assert.Equal(first, model.FeedCurrentIndex);
        Assert.True(FullyInView(Assert.IsAssignableFrom<FrameworkElement>(feed.ItemContainerGenerator.ContainerFromIndex(first)), scroll));
        Assert.True(FullyInView(Assert.IsAssignableFrom<FrameworkElement>(feed.ItemContainerGenerator.ContainerFromIndex(first - 1)), scroll),
            "the turn's header is not in view over its first action");

        // The feed scrolled itself, never the page.
        Assert.Equal(pageOffset, page.VerticalOffset);
    });

    [Fact]
    public void Wpf_step_that_changes_a_minions_health_keeps_every_card_and_feed_row() => OnPage((model, view, window) =>
    {
        var table = (FrameworkElement)view.FindName("TableCard");
        var feed = (ItemsControl)view.FindName("Feed");

        // Two hits of the same turn: the second only takes one more health off the same minion.
        var frame = TwoHits(model);
        model.GoFrame.Execute(frame);
        Settle(window);
        var target = Cards(model.Table).Single(c => c.Targeted);
        var health = target.Health;
        var lists = Descendants(table).OfType<ItemsControl>().Where(i => i.IsVisible && i.Items.Count > 0).ToList();
        Assert.True(lists.Count >= 2);
        var cards = lists.SelectMany(Containers).ToList();
        Assert.Equal(new[] { model.Table.Top, model.Table.Bottom }.Sum(s => s.Board.Count + s.Hand.Count + s.Secrets.Count), cards.Count);
        var targetRow = cards.Single(c => ReferenceEquals(c.DataContext, target));
        Assert.Contains(Descendants(targetRow).OfType<TextBlock>(), t => t.Name == "Hp" && t.Text == health.ToString(System.Globalization.CultureInfo.CurrentCulture));
        var rows = Built(feed);
        var current = model.FeedCurrentIndex;

        model.GoFrame.Execute(frame + 1);
        Settle(window);

        // The table: every card's container is the same object, the health shown is the new one.
        Assert.Equal(cards, lists.SelectMany(Containers), ReferenceEqualityComparer.Instance);
        Assert.Equal(health - 1, target.Health);
        Assert.Contains(Descendants(targetRow).OfType<TextBlock>(), t => t.Name == "Hp" && t.Text == (health - 1).ToString(System.Globalization.CultureInfo.CurrentCulture));

        // The feed: the mark went to the next row; the rows built before are the same containers on the same rows.
        Assert.Equal(current + 1, model.FeedCurrentIndex);
        Assert.False(model.FeedRows[current].Current);
        Assert.True(model.FeedRows[current + 1].Current);
        var after = Built(feed).ToDictionary(r => r.Index, r => r.Row);
        var kept = rows.Where(r => after.ContainsKey(r.Index)).ToList();
        // (The feed may have scrolled: the rows that went out of its reach are let go, the others stay.)
        Assert.True(kept.Count > 0 && kept.Exists(r => r.Index == current), "the feed built its rows again");
        Assert.All(kept, r => Assert.Same(r.Row, after[r.Index]));
        Assert.Same(model.FeedRows[current + 1], after[current + 1].DataContext);
    });

    [Fact]
    public void Wpf_arrow_is_worked_out_for_a_step_or_a_new_size_and_not_for_a_layout_pass() => OnPage((model, view, window) =>
    {
        var layer = (MatchArrowLayer)view.FindName("Arrow");
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
        var (from, to) = (End(view, model.Table.ActorRef), End(view, model.Table.TargetRef));
        Assert.InRange(arrow!.Start.X, from.Left, from.Right);
        Assert.InRange(arrow.Tip.X, to.Left, to.Right);
        Assert.True(Math.Abs(arrow.Tip.Y - arrow.Start.Y) > 40);

        // Layout passes without a step or a new size (a text elsewhere on the page changes, the page is measured again).
        computed = layer.Computed;
        var feedCard = (FrameworkElement)view.FindName("FeedCard");
        for (var pass = 0; pass < 3; pass++)
        {
            feedCard.InvalidateMeasure();
            view.InvalidateMeasure();
            Descendants(view).OfType<TextBlock>().First(t => t.IsVisible).InvalidateMeasure();
            Settle(window);
        }

        Assert.Equal(computed, layer.Computed);
        Assert.Equal(arrow, layer.Arrow());

        // A new size: the cards move, the arrow follows.
        window.Width -= 120;
        Settle(window);
        Assert.True(layer.Computed > computed);
        Assert.True(layer.Arrow() is { Attack: true });
        var moved = End(view, model.Table.ActorRef);
        Assert.InRange(layer.Arrow()!.Start.X, moved.Left, moved.Right);
    });

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

    /// <summary>The element carrying a replay id (a card or a hero portrait), as the arrow layer sees it.</summary>
    private static Rect End(MatchView view, int id)
    {
        var layer = (MatchArrowLayer)view.FindName("Arrow");
        var element = Descendants((FrameworkElement)view.FindName("TableCard")).OfType<FrameworkElement>().First(e => MatchArrowLayer.GetRef(e) == id && e.IsVisible);
        return new Rect(element.TransformToVisual(layer).Transform(default), new Size(element.ActualWidth, element.ActualHeight));
    }

    private static List<MatchCardItem> Cards(MatchTable table) =>
        new[] { table.Top, table.Bottom }.SelectMany(s => s.Board.Concat(s.Hand).Concat(s.Secrets).Concat(new[] { s.Weapon, s.HeroPower }.OfType<MatchCardItem>())).ToList();

    private static IEnumerable<FrameworkElement> Containers(ItemsControl list) =>
        Enumerable.Range(0, list.Items.Count).Select(i => Assert.IsAssignableFrom<FrameworkElement>(list.ItemContainerGenerator.ContainerFromIndex(i)));

    /// <summary>The feed rows that are built (a container of a row away from the view is let go).</summary>
    private static List<(int Index, FrameworkElement Row)> Built(ItemsControl feed) =>
        Enumerable.Range(0, feed.Items.Count)
            .Select(i => (Index: i, Row: feed.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement))
            .Where(r => r.Row is not null)
            .Select(r => (r.Index, r.Row!))
            .ToList();

    private static (double Top, double Bottom) Place(FrameworkElement row, ScrollViewer scroll)
    {
        var top = row.TransformToAncestor(scroll).Transform(default).Y;
        return (top, top + row.ActualHeight);
    }

    private static bool InView(FrameworkElement row, ScrollViewer scroll) => Place(row, scroll) is var (top, bottom) && bottom > 0 && top < scroll.ViewportHeight;

    private static bool FullyInView(FrameworkElement row, ScrollViewer scroll) => Place(row, scroll) is var (top, bottom) && top >= -0.5 && bottom <= scroll.ViewportHeight + 0.5;

    /// <summary>Runs <paramref name="check"/> on an STA thread with the long game's match page shown off screen.</summary>
    private static void OnPage(Action<MatchViewModel, MatchView, Window> check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
                var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
                var store = new ReplayStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N")));
                var game = new GameRecord
                {
                    Key = "match-light-game",
                    StartedAt = new DateTime(2026, 10, 23, 10, 0, 0),
                    EndedAt = new DateTime(2026, 10, 23, 10, 40, 0),
                    Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Win",
                    PlayerClass = "MAGE", OpponentClass = "WARRIOR", DeckName = "Test deck", Turns = 90,
                };
                game = game with { Replay = store.Save(game.Key, ReplayFile.Write(new ReplayHeader(), LongGame())) };
                var page = MatchPage.Load(game, store, CardDb.Empty, null);
                Assert.Null(page.Missing);
                var model = new MatchViewModel(new WpfMain(history, new AppSettings(), TestApp.Data));
                var view = new MatchView { DataContext = model };
                // As in the main window: the page scrolls, and the feed may be partly under the window's edge.
                var window = new Window
                {
                    Content = new ScrollViewer { Content = view, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                    Width = 1280, Height = 760, Left = -20000, Top = -20000, ShowActivated = false,
                    WindowStyle = WindowStyle.None, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
                };
                window.Show();
                try
                {
                    model.Show(page);
                    Settle(window);
                    check(model, view, window);
                }
                finally
                {
                    model.Clear();
                    window.Content = null;
                    window.Close();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The match page check failed", failure);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child))
            {
                yield return below;
            }
        }
    }

    /// <summary>Lets bindings, templates, layout and the feed's scrolling run (it is posted after the step).</summary>
    private static void Settle(Window window)
    {
        for (var round = 0; round < 2; round++)
        {
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            window.UpdateLayout();
        }
    }
}
