using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Controls;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF hover card on a window page (deck page card lists) opens right beside the hovered row's list, at the row,
/// and left of the list when the right has no room, never over the sidebar. The Avalonia twin is in Hearthpulse.Tests
/// (LocBindingTests.Tables). Needs no application resources, so it does not depend on what other tests loaded.
/// </summary>
public sealed class CardPreviewPlacementTests
{
    private const double PreviewWidth = 236;

    [Fact]
    public void Wpf_card_preview_opens_beside_the_list_of_the_hovered_row()
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
            throw new InvalidOperationException("Card preview placement failed", failure);
        }
    }

    private static void Check()
    {
        // A sidebar, then a page with a list in its middle and a list on its right edge; the host covers both columns.
        var middle = List(out var middleRow);
        middle.Width = 300;
        middle.HorizontalAlignment = HorizontalAlignment.Center;
        var edge = List(out var edgeRow);
        var page = new Grid();
        page.ColumnDefinitions.Add(new ColumnDefinition());
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        Grid.SetColumn(edge, 1);
        page.Children.Add(middle);
        page.Children.Add(edge);
        var host = new CardPreviewHost();
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        root.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(page, 1);
        Grid.SetColumnSpan(host, 2);
        root.Children.Add(new Border());
        root.Children.Add(page);
        root.Children.Add(host);
        var window = new Window
        {
            Width = 1200, Height = 800, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Content = root,
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var frame = (FrameworkElement)host.Children[0];

            host.Show(middleRow);
            var list = middle.TransformToVisual(host).TransformBounds(new Rect(middle.RenderSize));
            Assert.Equal(list.Right + 10, Canvas.GetLeft(frame), 1);
            var row = middleRow.TransformToVisual(host).Transform(new Point(0, middleRow.ActualHeight / 2));
            var top = Canvas.GetTop(frame);
            Assert.InRange(row.Y, top, top + frame.DesiredSize.Height);

            // No room on the right: left of the list, still clear of it.
            host.Show(edgeRow);
            var edgeLeft = edge.TransformToVisual(host).Transform(default).X;
            Assert.Equal(edgeLeft - 10 - PreviewWidth, Canvas.GetLeft(frame), 1);
        }
        finally
        {
            window.Close();
        }
    }

    private static ItemsControl List(out FrameworkElement firstRow)
    {
        var rows = Enumerable.Range(0, 5).Select(i =>
        {
            var row = new Border { Height = 30, Background = Brushes.Transparent };
            CardPreview.SetCaption(row, "Card " + i);
            return row;
        }).ToList();
        firstRow = rows[2];
        var list = new ItemsControl { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 200, 0, 0) };
        rows.ForEach(r => list.Items.Add(r));
        return list;
    }
}
