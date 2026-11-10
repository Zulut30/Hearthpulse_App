using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Controls;
using Hearthpulse.App.Main.Meta;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using WpfWindow = Hearthpulse.App.Main.MainWindow;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The meta tabs of the Standard page build their fixed part (the filters, the cards' frames and heads, the chosen
/// archetype's sections) while the page still shows "My stats", a part a dispatcher turn, so the first click on
/// "Meta tiers" builds the rows alone. A click before that is done shows the tab the usual way.
/// </summary>
public sealed class MetaPrebuildTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

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
