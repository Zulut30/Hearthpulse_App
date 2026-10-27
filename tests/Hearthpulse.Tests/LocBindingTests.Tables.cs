using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Hearthpulse.UI.Controls;

namespace Hearthpulse.Tests;

/// <summary>
/// The deck page's hover card sits right beside the hovered row's list (left of it when the right has no room), and a
/// table's head stays at the top of the page while its rows scroll (they share the headless app).
/// </summary>
public sealed partial class LocBindingTests
{
    private const double PreviewWidth = 236;

    [Fact]
    public void Card_preview_opens_beside_the_list_of_the_hovered_row_not_over_the_sidebar() => Headless.Run(PreviewBesideList);

    private static void PreviewBesideList()
    {
        // A sidebar, then a page with a list in its middle and a list on its right edge; the host covers both columns.
        var middle = List(out var middleRow);
        var edge = List(out var edgeRow);
        var page = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300") };
        middle.Width = 300;
        middle.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(edge, 1);
        page.Children.Add(middle);
        page.Children.Add(edge);
        var host = new CardPreviewHost();
        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*") };
        Grid.SetColumn(page, 1);
        Grid.SetColumnSpan(host, 2);
        root.Children.Add(new Border());
        root.Children.Add(page);
        root.Children.Add(host);
        var theme = Theme();
        var window = new Window { Width = 1200, Height = 800, Content = root };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var frame = host.Children[0];

            host.Show(middleRow);
            var list = middle.Bounds.Translate(page.Bounds.Position);
            Assert.Equal(list.Right + 10, Canvas.GetLeft(frame), 1);
            var row = middleRow.TranslatePoint(default, host)!.Value;
            var top = Canvas.GetTop(frame);
            Assert.InRange(row.Y + (middleRow.Bounds.Height / 2), top, top + frame.DesiredSize.Height);

            // No room on the right: left of the list, still clear of it.
            host.Show(edgeRow);
            var edgeLeft = edge.TranslatePoint(default, host)!.Value.X;
            Assert.Equal(edgeLeft - 10 - PreviewWidth, Canvas.GetLeft(frame), 1);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static ItemsControl List(out Control firstRow)
    {
        var rows = Enumerable.Range(0, 5).Select(i =>
        {
            var row = new Border { Height = 30, Background = Brushes.Transparent };
            CardPreview.SetCaption(row, "Card " + i);
            return row;
        }).ToList();
        firstRow = rows[2];
        return new ItemsControl { ItemsSource = rows, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 200, 0, 0) };
    }

    [Fact]
    public void Table_head_stays_at_the_top_of_the_page_while_its_rows_scroll() => Headless.Run(StickyHead);

    private static void StickyHead()
    {
        var head = new Border { Height = 30 };
        StickyHeader.SetIsEnabled(head, true);
        var table = new StackPanel();
        table.Children.Add(head);
        for (var i = 0; i < 40; i++)
        {
            table.Children.Add(new Border { Height = 30 });
        }

        var content = new StackPanel();
        content.Children.Add(new Border { Height = 300 });
        content.Children.Add(table);
        content.Children.Add(new Border { Height = 2000 });
        var page = new ScrollViewer { Content = content };
        var theme = Theme();
        var window = new Window { Width = 800, Height = 600, Content = page };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, Shift(head));

            page.Offset = new Vector(0, 500);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(200, Shift(head), 1);
            Assert.NotNull(head.Background);

            // Past the table's end the head stops at its last row.
            page.Offset = new Vector(0, 2000);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40 * 30, Shift(head), 1);

            page.Offset = new Vector(0, 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, Shift(head));
            Assert.Null(head.Background);
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The app's themes (lists and scroll viewers get their templates from Fluent), added for one test.</summary>
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

    private static double Shift(Control head) => ((TranslateTransform)head.RenderTransform!).Y;
}
