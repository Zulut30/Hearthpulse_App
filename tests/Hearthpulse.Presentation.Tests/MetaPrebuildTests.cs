using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Controls;
using Hearthpulse.App.Main.Meta;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfRow = Hearthpulse.App.Main.MetaArchetypeRow;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The meta tabs of the Standard page build their fixed part (the filters, the cards' frames and heads, the chosen
/// archetype's sections) while the page still shows "My stats", a part a dispatcher turn, so the first click on
/// "Meta tiers" builds the rows alone. A click before that is done shows the tab the usual way. And the rows of the
/// lists: a click or Enter on one chooses its archetype (the lists' holder hears it, the rows carry no command).
/// </summary>
public sealed class MetaPrebuildTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    /// <summary>
    /// In the app the build ahead starts a third of a second after the page shows; the tests of this assembly pump the
    /// dispatcher instead of waiting on the clock, so here it starts at once, in both UIs.
    /// </summary>
    [ModuleInitializer]
    internal static void StartTheBuildAheadAtOnce()
    {
        ConstructedMetaView.PrebuildDelay = TimeSpan.Zero;
        Hearthpulse.UI.Main.Meta.ConstructedMetaView.PrebuildDelay = TimeSpan.Zero;
    }

    [Fact]
    public void The_fixed_part_of_the_meta_tabs_is_built_before_the_first_click_and_the_rows_at_it() => WpfThread.RunThemed(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = Open(model);
        try
        {
            window.ShowPage("Standard");
            model.SetConstructedMeta(MetaTierScene.States());
            var view = Logical<ConstructedMetaView>(window).Single();
            var list = (ItemsControl)view.FindName("MetaTierList");
            Pump();

            // The page shows "My stats": the view is collapsed again and takes its own height, with its filters built
            // (every segment has its plate), the cards' frames and the chosen archetype's sections, but no row.
            Assert.Equal(Visibility.Collapsed, view.Visibility);
            Assert.True(double.IsNaN(view.Height));
            var segments = Segments(view);
            Assert.True(segments.Count >= 8 + 6 + 6 + 2, $"{segments.Count} segments built ahead");
            Assert.All(new[] { "FilterRanks", "FilterPeriods", "FilterClasses", "MinGamesPicker", "TierCard", "ArchetypeCard" },
                name => Assert.Equal(Visibility.Visible, ((FrameworkElement)view.FindName(name)).Visibility));
            Assert.True(((FrameworkElement)view.FindName("MetaHead")).ActualWidth > 0);
            Assert.True(model.ArchetypeDetailReady);
            Assert.True(((FrameworkElement)view.FindName("ArchetypeSections")).ActualHeight > 0);
            Assert.Equal(12, list.Items.Count);
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));

            // A part a turn: the four filter bars, then each card's frame and each block inside it, the side card's
            // sections one by one; no part twice.
            var all = view.PrebuildTurns.Select(t => t.Step).ToList();
            var turns = all.Where(t => t != "rows").ToList();
            Assert.Equal(["FilterRanks", "FilterPeriods", "FilterClasses", "MinGamesPicker", "TierCard"], turns.Take(5));
            // The rows of the lists inside the parts come a few a turn: the segments of a bar after the bar, the guide's
            // twelve cards and the five best lists after their lists.
            Assert.Equal("rows", all[1]);
            Assert.True(all.Count(t => t == "rows") >= 4 + 4 + 5, $"{all.Count(t => t == "rows")} turns of rows");
            Assert.True(turns.IndexOf("MetaHead") > turns.IndexOf("TierCard"));
            Assert.True(turns.IndexOf("ArchetypeSections") > turns.IndexOf("ArchetypeCard"));
            Assert.True(turns.Count - turns.IndexOf("ArchetypeSections") > 8, "the sections come one a turn");
            Assert.True(turns.Count >= 20, $"{turns.Count} turns");
            Assert.DoesNotContain("MetaLists", turns);
            Assert.Equal(5, turns.Count(t => t is "FilterRanks" or "TierCard" or "ArchetypeCard" or "ArchetypeSections" or "MetaHead"));

            // Another meta tab first: the tier list's rows are still not built under it.
            window.ShowConstructedTab("Top");
            Pump();
            Assert.Equal(Visibility.Visible, view.Visibility);
            Assert.Equal(segments, Segments(view));
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)view.FindName("MetaLists")).Visibility);

            // The click: the same segments (nothing of the fixed part is built again), and now the rows.
            window.ShowConstructedTab("Tiers");
            Pump();
            Assert.Equal(Visibility.Visible, view.Visibility);
            Assert.Equal(segments, Segments(view));
            Assert.All(Enumerable.Range(0, 12), i => Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(i)));
            Assert.True(view.ActualHeight > 300);
        }
        finally
        {
            window.CloseForReal();
        }
    });

    [Fact]
    public void A_click_before_the_fixed_part_is_built_shows_the_whole_tab_at_once() => WpfThread.RunThemed(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = Open(model);
        try
        {
            // No dispatcher turn between the page and the tab: the view was hidden, zero high, with its parts collapsed.
            window.ShowPage("Standard");
            model.SetConstructedMeta(MetaTierScene.States());
            window.ShowConstructedTab("Tiers");
            Pump();
            var view = Logical<ConstructedMetaView>(window).Single();
            var list = (ItemsControl)view.FindName("MetaTierList");

            Assert.Equal(Visibility.Visible, view.Visibility);
            Assert.True(double.IsNaN(view.Height));
            Assert.All(new[] { "FilterRanks", "FilterPeriods", "FilterClasses", "MinGamesPicker", "TierCard", "ArchetypeCard", "MetaLists" },
                name => Assert.True(((FrameworkElement)view.FindName(name)).ActualHeight > 0, name));
            Assert.All(Enumerable.Range(0, 12), i => Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(i)));
            Assert.True(((FrameworkElement)view.FindName("ArchetypeSections")).ActualHeight > 0);

            // Back to "My stats" and to the tab again: an ordinary collapsed view, nothing left hidden or zero high.
            window.ShowConstructedTab("Decks");
            Pump();
            Assert.Equal(Visibility.Collapsed, view.Visibility);
            window.ShowConstructedTab("Top");
            Pump();
            Assert.Equal(Visibility.Visible, view.Visibility);
            Assert.True(((FrameworkElement)view.FindName("FilterRanks")).ActualHeight > 0);
        }
        finally
        {
            window.CloseForReal();
        }
    });

    [Fact]
    public void Stats_that_come_after_the_build_ahead_show_without_building_the_fixed_part_again() => WpfThread.RunThemed(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = Open(model);
        try
        {
            // Built ahead on an empty page: the filters that do not depend on the stats, the cards' frames, the heads.
            window.ShowPage("Standard");
            Pump();
            var view = Logical<ConstructedMetaView>(window).Single();
            var list = (ItemsControl)view.FindName("MetaTierList");
            Assert.Equal(Visibility.Collapsed, view.Visibility);
            var fixedBars = new[] { "FilterRanks", "FilterPeriods", "MinGamesPicker" }
                .SelectMany(name => Segments((DependencyObject)view.FindName(name))).ToList();
            Assert.True(fixedBars.Count >= 8 + 6 + 6, $"{fixedBars.Count} segments built ahead");
            var turns = view.PrebuildTurns.Count;

            // The stats arrive: nothing is built under the collapsed view, and the build ahead does not run again.
            model.SetConstructedMeta(MetaTierScene.States());
            Pump();
            Assert.Equal(Visibility.Collapsed, view.Visibility);
            Assert.Equal(turns, view.PrebuildTurns.Count);
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(0));

            // The click: the rows of the stats, the chosen archetype in the side card, the same fixed segments.
            window.ShowConstructedTab("Tiers");
            Pump();
            Assert.All(Enumerable.Range(0, 12), i => Assert.NotNull(list.ItemContainerGenerator.ContainerFromIndex(i)));
            Assert.Equal(fixedBars, new[] { "FilterRanks", "FilterPeriods", "MinGamesPicker" }.SelectMany(name => Segments((DependencyObject)view.FindName(name))));
            Assert.True(Segments((DependencyObject)view.FindName("FilterClasses")).Count >= 7, "a segment per class of the stats");
            Assert.True(((FrameworkElement)view.FindName("ArchetypeSections")).ActualHeight > 0);
            Assert.Contains(Descendants<TextBlock>((DependencyObject)view.FindName("ArchetypeCard")), t => t.Text == model.SelectedArchetype!.Name);
        }
        finally
        {
            window.CloseForReal();
        }
    });

    [Fact]
    public void Leaving_the_page_stops_the_build_ahead_and_coming_back_carries_it_on_without_building_a_part_twice() => WpfThread.RunThemed(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = Open(model);
        try
        {
            // Away before a turn of it ran: under a hidden page nothing is built, and nothing runs.
            window.ShowPage("Standard");
            model.SetConstructedMeta(MetaTierScene.States());
            window.ShowPage("Overview");
            Pump();
            var view = Logical<ConstructedMetaView>(window).Single();
            Assert.Empty(view.PrebuildTurns);
            Assert.Empty(Segments(view));
            Assert.Equal(Visibility.Hidden, view.Visibility);

            // Back: it carries on, each part once, and ends in an ordinary collapsed view.
            window.ShowPage("Standard");
            Pump();
            Assert.Equal(Visibility.Collapsed, view.Visibility);
            Assert.True(double.IsNaN(view.Height));
            var turns = view.PrebuildTurns.Select(t => t.Step).ToList();
            Assert.True(turns.Count >= 20, $"{turns.Count} turns");
            Assert.Equal(1, turns.Count(t => t == "FilterRanks"));
            Assert.Equal(1, turns.Count(t => t == "ArchetypeSections"));
            var segments = Segments(view);
            Assert.Equal(segments.Count, segments.Distinct().Count());
            Assert.Equal(model.MetaRanks.Count, Segments((DependencyObject)view.FindName("FilterRanks")).Count);

            // Away and back again: it does not start over.
            window.ShowPage("Overview");
            Pump();
            window.ShowPage("Standard");
            Pump();
            Assert.Equal(turns.Count, view.PrebuildTurns.Count);
            Assert.Equal(segments, Segments(view));
        }
        finally
        {
            window.CloseForReal();
        }
    });

    [Theory]
    [InlineData("Tiers", "MetaTierList")]
    [InlineData("Table", "MetaTable")]
    public void A_click_or_Enter_on_a_row_chooses_its_archetype_and_the_side_card_shows_it(string mode, string listName) => WpfThread.RunThemed(() =>
    {
        var model = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var window = Open(model);
        try
        {
            window.ShowPage("Standard");
            model.SetConstructedMeta(MetaTierScene.States());
            model.SetMetaView.Execute(mode);
            window.ShowConstructedTab("Tiers");
            Pump();
            var view = Logical<ConstructedMetaView>(window).Single();
            var rows = Descendants<Button>((DependencyObject)view.FindName(listName)).Where(b => b.DataContext is WpfRow).ToList();
            Assert.Equal(7, rows.Count);
            var first = (WpfRow)rows[0].DataContext;
            Assert.Same(first, model.SelectedArchetype);

            // Every row is named for a screen reader by its archetype.
            Assert.All(rows, r => Assert.Equal(((WpfRow)r.DataContext).Name, AutomationProperties.GetName(r)));

            // A click on the third row.
            var third = (WpfRow)rows[2].DataContext;
            rows[2].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, rows[2]));
            Pump();
            Assert.Same(third, model.SelectedArchetype);
            Assert.True(third.IsSelected);
            Assert.False(first.IsSelected);
            Assert.Same(third, model.DetailArchetype);
            Assert.Contains(Descendants<TextBlock>((DependencyObject)view.FindName("ArchetypeCard")), t => t.Text == third.Name);

            // Enter on the fifth row, as from the keyboard.
            var fifth = (WpfRow)rows[4].DataContext;
            Assert.True(rows[4].Focus());
            rows[4].RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rows[4])!, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Pump();
            Assert.Same(fifth, model.SelectedArchetype);
            Assert.True(fifth.IsSelected);
            Assert.False(third.IsSelected);
            Assert.Same(fifth, model.DetailArchetype);
            Assert.Contains(Descendants<TextBlock>((DependencyObject)view.FindName("ArchetypeCard")), t => t.Text == fifth.Name);
        }
        finally
        {
            window.CloseForReal();
        }
    });

    /// <summary>The filter segments whose template is applied: each is its plate and its label, nothing else.</summary>
    private static List<RadioButton> Segments(DependencyObject view)
    {
        var built = Descendants<RadioButton>(view).Where(r => VisualTreeHelper.GetChildrenCount(r) > 0).ToList();
        Assert.All(built, r => Assert.IsType<TextBlock>(Assert.IsType<SegmentPlate>(VisualTreeHelper.GetChild(r, 0)).Child));
        return built;
    }

    private static WpfWindow Open(WpfMain model)
    {
        var window = new WpfWindow(model)
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 1260, Height = 840,
            ShowActivated = false, ShowInTaskbar = false,
        };
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>In the logical tree: the page's parts are there before a layout pass put them into the visual one.</summary>
    private static IEnumerable<T> Logical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Logical<T>(child))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var nested in Descendants<T>(child))
            {
                yield return nested;
            }
        }
    }
}
