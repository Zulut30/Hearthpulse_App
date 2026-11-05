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
/// The WPF History row: the Battlegrounds board shows the minions that fit whole in its room and "+N" for the rest. A
/// card is the window less 374 px (nav 250, page margins 80, the list card's border and padding 34, list chrome 2,
/// scroll bar 6, item border 2): 666 at the minimum window (1040: one minion and "+6"), 886 at the default (1260: five
/// and "+2"); all seven from a card of 910 (a window of about 1285). The caption wraps to two lines, not trimmed at one.
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
        var card = new LibraryCard(ReplayShelf.Recent, "a.hpr", "key", battlegrounds: true) { Title = "Hero", Caption = "Уровень 5 · 14 ходов", When = "Today 12:49 · 27 min" };
        card.SetSummary([Chip("A"), Chip("B"), Chip("C"), Chip("D"), Chip("E")],
            Enumerable.Range(1, 7).Select(i => new LibraryMinion("BG21_00" + i, "136/63", false, false, false, "Minion " + i)).ToList());
        var row = (FrameworkElement)((DataTemplate)view.Resources["LibraryCard"]).LoadContent();
        row.DataContext = card;
        // The width is the card's: its own 12 px padding on each side comes off the row's room.
        // Minions are 52 wide (stat badges) next to a 150 px name: none at the minimum window, four at the default, all from 968.
        foreach (var (width, shown, more) in new[] { (666.0, 0, "+7"), (886.0, 4, "+3"), (968.0, 7, string.Empty) })
        {
            Layout(row, width);
            var minions = Descendants(row).OfType<StackPanel>().Where(p => p.DataContext is LibraryMinion && p.Orientation == Orientation.Vertical && Shown(p, row)).ToList();
            Assert.Equal(shown, minions.Count);
            var pill = Descendants(row).OfType<Border>().Single(b => b.DataContext is LibraryBoardView && b.ToolTip is not null);
            Assert.Equal(more.Length > 0, Shown(pill, row));
            if (more.Length > 0)
            {
                var rest = string.Join("\n", Enumerable.Range(shown + 1, 7 - shown).Select(i => "Minion " + i));
                Assert.Equal(more, Descendants(pill).OfType<TextBlock>().Single().Text);
                Assert.Equal(rest, pill.ToolTip);
                Assert.Equal(rest, System.Windows.Automation.AutomationProperties.GetName(pill));
            }

            // The Russian caption fits the 150 px name column on one line.
            var caption = Descendants(row).OfType<TextBlock>().Single(t => t.Name == "BgCaption");
            Assert.Equal(TextWrapping.NoWrap, caption.TextWrapping);
            Assert.True(caption.ActualHeight < 20, $"{caption.ActualHeight}");

            // The chips' "+N" is named for screen readers as its tooltip names the rest.
            var chipsMore = Descendants(row).OfType<Border>().Single(b => Equals(b.ToolTip, "D, E"));
            Assert.Equal("D, E", System.Windows.Automation.AutomationProperties.GetName(chipsMore));

            // Whole minions only: the last one ends inside the board's room.
            var room = Descendants(row).OfType<Border>().Single(b => b.Name == "BoardRoom");
            if (minions.Count > 0)
            {
                var last = minions[^1];
                var end = last.TranslatePoint(new Point(last.ActualWidth, 0), room).X;
                Assert.True(end <= room.ActualWidth + 0.5, $"{width}: the last minion ends at {end}, the room is {room.ActualWidth}");
            }
        }
    }

    private static LibraryChip Chip(string name) => new("TB_BaconShop_HP_061", name);

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
