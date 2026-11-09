using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.UI.Controls;

namespace Hearthpulse.Tests;

/// <summary>
/// The Avalonia list panel of the long main-window lists (the twin of the WPF PageRowsPanelTests): it builds the rows
/// near what the page shows and a screen more at background priority, while the page keeps the height and row
/// positions a StackPanel would give it; a list that changes in place keeps the rows that stay.
/// </summary>
public sealed class PageRowsPanelAvaloniaTests
{
    private const double RowHeight = 20;
    private const double HeaderHeight = 300;

    [Fact]
    public void Rows_near_the_viewport_come_first_the_rest_follow_and_the_page_keeps_its_height() => LocBindingTests.Headless.Run(() =>
    {
        var theme = Theme();
        var items = Enumerable.Range(0, 100).Select(i => $"row {i}").ToList();
        var rows = List(items, new PageRowsPanel());
        var page = Page(rows, out var window);
        var reference = Page(List(items, new StackPanel()), out var referenceWindow);
        try
        {
            // The layout alone, no background work yet: header 300 + 100 rows of 20, the same extent as the StackPanel's,
            // with only the rows the 400 px page shows (five) and the spare ones built.
            window.UpdateLayout();
            referenceWindow.UpdateLayout();
            var panel = Assert.IsType<PageRowsPanel>(rows.ItemsPanelRoot);
            Assert.Equal(reference.Extent.Height, page.Extent.Height);
            Assert.Equal(HeaderHeight + (100 * RowHeight), page.Extent.Height);
            Assert.InRange(panel.Children.Count, 5, 7);

            // A screen ahead follows at background priority; rows nobody scrolled to stay unbuilt.
            Settle(window);
            Assert.InRange(panel.Children.Count, 8, 15);
            Assert.Null(rows.ContainerFromIndex(99));

            // At the end of the page its last rows are there, in their StackPanel places; the rows built on the way stay.
            page.Offset = new Vector(0, page.Extent.Height - page.Viewport.Height);
            Settle(window);
            foreach (var index in new[] { 0, 99, 90 })
            {
                var row = Assert.IsType<ContentPresenter>(rows.ContainerFromIndex(index));
                Assert.Equal(index * RowHeight, row.TranslatePoint(default, panel)!.Value.Y);
                Assert.Equal($"row {index}", row.Content);
            }

            var last = rows.ContainerFromIndex(99)!;
            var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), page)!.Value.Y;
            Assert.InRange(bottom, RowHeight, page.Viewport.Height + 0.5);
            // The 400 px the page now shows are all rows (twenty), with a screen of rows above them; nothing in between.
            Assert.InRange(panel.Children.Count, 40, 55);
            Assert.Null(rows.ContainerFromIndex(40));
            Assert.Equal(reference.Extent.Height, page.Extent.Height);

            // A new list starts over: its rows only, and the height of its rows.
            rows.ItemsSource = items.Take(10).ToList();
            Settle(window);
            Assert.Equal(HeaderHeight + (10 * RowHeight), page.Extent.Height);
            Assert.InRange(panel.Children.Count, 1, 10);
        }
        finally
        {
            window.Close();
            referenceWindow.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void A_whole_page_shot_builds_every_row_and_leaves_the_page_where_it_was() => LocBindingTests.Headless.Run(() =>
    {
        var theme = Theme();
        var rows = List(Enumerable.Range(0, 100).Select(i => $"row {i}").ToList(), new PageRowsPanel());
        var page = Page(rows, out var window);
        try
        {
            window.UpdateLayout();
            Assert.Null(rows.ContainerFromIndex(99));

            PageRowsPanel.BuildAllRows(page);

            Assert.All(Enumerable.Range(0, 100), i => Assert.NotNull(rows.ContainerFromIndex(i)));
            Assert.Equal(99 * RowHeight, rows.ContainerFromIndex(99)!.Bounds.Y);
            Assert.Equal(0, page.Offset.Y);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public void A_list_changed_in_place_keeps_the_rows_that_stay() => LocBindingTests.Headless.Run(() =>
    {
        var theme = Theme();
        var items = new ObservableCollection<string>(Enumerable.Range(0, 100).Select(i => $"row {i}"));
        var rows = List(items, new PageRowsPanel());
        var page = Page(rows, out var window);
        try
        {
            Settle(window);
            var panel = Assert.IsType<PageRowsPanel>(rows.ItemsPanelRoot);
            var first = rows.ContainerFromIndex(0)!;
            var second = rows.ContainerFromIndex(1)!;
            var third = rows.ContainerFromIndex(2)!;
            var text = Assert.IsType<TextBlock>(Assert.IsType<ContentPresenter>(second).Child);

            // Another item in the same place: the row and the controls of its template stay, the text is the new one.
            items[1] = "changed";
            Settle(window);
            Assert.Same(second, rows.ContainerFromIndex(1));
            Assert.Same(text, ((ContentPresenter)second).Child);
            Assert.Equal("changed", text.Text);
            Assert.Same(first, rows.ContainerFromIndex(0));

            // A row inserted above moves the built ones down a place, a removed one brings them back up.
            items.Insert(0, "new");
            Settle(window);
            Assert.Same(first, rows.ContainerFromIndex(1));
            Assert.Same(second, rows.ContainerFromIndex(2));
            Assert.Equal(2 * RowHeight, second.Bounds.Y);
            Assert.Equal("new", Assert.IsType<ContentPresenter>(rows.ContainerFromIndex(0)).Content);
            Assert.Equal(HeaderHeight + (101 * RowHeight), page.Extent.Height);

            items.RemoveAt(1);
            Settle(window);
            Assert.Same(second, rows.ContainerFromIndex(1));
            Assert.Same(third, rows.ContainerFromIndex(2));
            Assert.DoesNotContain(first, panel.Children);
            Assert.Equal(RowHeight, second.Bounds.Y);

            items.Move(1, 3);
            Settle(window);
            Assert.Equal(["new", "row 2", "row 3", "changed"], Enumerable.Range(0, 4).Select(i => ((ContentPresenter)rows.ContainerFromIndex(i)!).Content));
            Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(i * RowHeight, rows.ContainerFromIndex(i)!.Bounds.Y));

            items.Clear();
            Settle(window);
            Assert.Empty(panel.Children);
            Assert.Equal(0, panel.Bounds.Height);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// <summary>Layout, the background build it queued, and the layout of what that built, until nothing is left to do.</summary>
    internal static void Settle(Window window)
    {
        for (var pass = 0; pass < 50; pass++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        window.UpdateLayout();
    }

    private static ItemsControl List(IEnumerable<string> items, Panel panel) => new()
    {
        ItemsSource = items,
        ItemsPanel = new FuncTemplate<Panel?>(() => panel),
        ItemTemplate = new FuncDataTemplate<string>((_, _) => new TextBlock { Height = RowHeight, [!TextBlock.TextProperty] = new Avalonia.Data.Binding() }, supportsRecycling: true),
    };

    /// <summary>A 400 px high scrolling page: a 300 px header, then the list.</summary>
    private static ScrollViewer Page(ItemsControl list, out Window window)
    {
        var page = new ScrollViewer { Content = new StackPanel { Children = { new Border { Height = HeaderHeight }, list } } };
        window = new Window { Content = page, Width = 500, Height = 400 };
        window.Show();
        return page;
    }

    private static Styles Theme()
    {
        var theme = new Styles
        {
            new FluentTheme(),
            new StyleInclude(new Uri("avares://Hearthpulse.UI/")) { Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml") },
        };
        Application.Current!.Styles.Add(theme);
        return theme;
    }
}
