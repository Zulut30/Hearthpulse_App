using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
using Hearthpulse.Core;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF History row: the Battlegrounds board shows the minions that fit whole in its room and "+N" for the rest,
/// at the minimum window (1040: a row about 678 px wide) and the default one (1260: about 898 px).
/// </summary>
public sealed class LibraryRowXamlTests
{
    [Fact]
    public void Wpf_battlegrounds_row_shows_whole_minions_and_plus_n()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
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
            throw new InvalidOperationException("History row XAML failed", failure);
        }
    }

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var view = new ReplayLibraryView();
        var card = new LibraryCard(ReplayShelf.Recent, "a.hpr", "key", battlegrounds: true) { Title = "Hero", Caption = "Tier 5 · 14 turns", When = "Today 12:49 · 27 min" };
        card.SetSummary([new LibraryChip("TB_BaconShop_HP_061", "Power")],
            Enumerable.Range(1, 7).Select(i => new LibraryMinion("BG21_00" + i, "136/63", false, false, false, "Minion " + i)).ToList());
        var row = (FrameworkElement)((DataTemplate)view.Resources["LibraryCard"]).LoadContent();
        row.DataContext = card;
        // The card's own 12 px padding on each side comes on top of the row's room.
        foreach (var (width, shown, more) in new[] { (702.0, 2, "+5"), (922.0, 7, string.Empty) })
        {
            Layout(row, width);
            var minions = Descendants(row).OfType<StackPanel>().Where(p => p.DataContext is LibraryMinion && Shown(p, row)).ToList();
            Assert.Equal(shown, minions.Count);
            var pill = Descendants(row).OfType<Border>().Single(b => b.DataContext is LibraryBoardView && b.ToolTip is not null);
            Assert.Equal(more.Length > 0, Shown(pill, row));
            if (more.Length > 0)
            {
                Assert.Equal(more, Descendants(pill).OfType<TextBlock>().Single().Text);
                Assert.Equal("Minion 3\nMinion 4\nMinion 5\nMinion 6\nMinion 7", pill.ToolTip);
            }

            // Whole minions only: the last one ends inside the board's room.
            var room = Descendants(row).OfType<Border>().Single(b => b.Name == "BoardRoom");
            var last = minions[^1];
            var end = last.TranslatePoint(new Point(last.ActualWidth, 0), room).X;
            Assert.True(end <= room.ActualWidth + 0.5, $"{width}: the last minion ends at {end}, the room is {room.ActualWidth}");
        }
    }

    private static bool Shown(DependencyObject element, DependencyObject root)
    {
        for (var at = element; at is not null && !ReferenceEquals(at, root); at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
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

    private static void Layout(FrameworkElement view, double width)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(new Size(width, double.PositiveInfinity));
            view.Arrange(new Rect(0, 0, width, view.DesiredSize.Height));
            // Bindings and templates attach at DataBind priority (the board follows its room's width): let them, then lay out again.
            var frame = new DispatcherFrame();
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            view.UpdateLayout();
        }
    }
}
