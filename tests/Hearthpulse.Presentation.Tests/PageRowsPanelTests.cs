using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Controls;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The list panel of the long main-window lists builds the rows near the page's viewport, and a screen more at
/// background priority, while the page keeps the height and row positions a StackPanel would give it.
/// </summary>
public sealed class PageRowsPanelTests
{
    [Fact]
    public void Rows_near_the_viewport_come_first_the_rest_follow_and_the_page_keeps_its_height()
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
            throw new InvalidOperationException("Panel check failed", failure);
        }
    }

    private static void Check()
    {
        var items = Enumerable.Range(0, 100).Select(i => $"row {i}").ToList();
        var rows = List(items, new ItemsPanelTemplate(new FrameworkElementFactory(typeof(PageRowsPanel))));
        var stack = List(items, new ItemsPanelTemplate(new FrameworkElementFactory(typeof(StackPanel))));
        var page = Page(rows, out var window);
        var reference = Page(stack, out var referenceWindow);
        try
        {
            Pump(DispatcherPriority.Input);
            var panel = Descendant<PageRowsPanel>(rows)!;
            // Header 300 + 100 rows of 20: the same extent as the StackPanel's, with only the visible rows built.
            Assert.Equal(reference.ExtentHeight, page.ExtentHeight);
            Assert.Equal(2300, page.ExtentHeight);
            Assert.InRange(VisualTreeHelper.GetChildrenCount(panel), 1, 30);

            // A screen ahead follows at background priority; rows nobody scrolled to stay unbuilt.
            Pump(DispatcherPriority.ApplicationIdle);
            Assert.InRange(VisualTreeHelper.GetChildrenCount(panel), 15, 60);
            Assert.Null(rows.ItemContainerGenerator.ContainerFromIndex(99));

            // At the end of the page its last rows are there, in their StackPanel places; the rows built on the way stay.
            page.ScrollToEnd();
            Pump(DispatcherPriority.ApplicationIdle);
            foreach (var index in new[] { 0, 99, 90 })
            {
                var row = (FrameworkElement)rows.ItemContainerGenerator.ContainerFromIndex(index);
                Assert.Equal(index * 20.0, row.TranslatePoint(default, panel).Y);
                Assert.Equal($"row {index}", row.DataContext);
            }

            Assert.Equal(reference.ExtentHeight, page.ExtentHeight);

            // A new list starts over: its rows only, and the height of its rows.
            rows.ItemsSource = items.Take(10).ToList();
            Pump(DispatcherPriority.ApplicationIdle);
            Assert.Equal(300 + (10 * 20), page.ExtentHeight);
            Assert.Equal(10, VisualTreeHelper.GetChildrenCount(panel));
        }
        finally
        {
            window.Close();
            referenceWindow.Close();
        }
    }

    private static ItemsControl List(IEnumerable<string> items, ItemsPanelTemplate panel) => new()
    {
        ItemsSource = items,
        ItemsPanel = panel,
        ItemTemplate = (DataTemplate)XamlReader.Parse(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><TextBlock Height=\"20\" Text=\"{Binding}\" /></DataTemplate>"),
    };

    /// <summary>A 400 px high scrolling page, off screen: a 300 px header, then the list.</summary>
    private static ScrollViewer Page(ItemsControl list, out Window window)
    {
        var content = new StackPanel();
        content.Children.Add(new Border { Height = 300 });
        content.Children.Add(list);
        var page = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        window = new Window
        {
            Content = page, Width = 500, Height = 400, Left = -20000, Top = -20000, ShowActivated = false,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        return page;
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Pump(DispatcherPriority until)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(until, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
