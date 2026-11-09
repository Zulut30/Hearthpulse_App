using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Controls;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The hover card with its related cards (N'Zoth's four abilities, Choose One options, tokens): the shared grid layout,
/// and the WPF host showing the big card plus one render per related card, beside it and away from the row. The
/// Avalonia host's twin is in Hearthpulse.Tests (LocBindingTests.CardPreviewRelated), on its headless app.
/// </summary>
[Collection(nameof(CardPreviewRelatedTests))]
public sealed class CardPreviewRelatedTests
{
    [Fact]
    public void The_grid_follows_the_design_table()
    {
        Assert.Null(CardPreviewLayout.RelatedGrid(0));
        Assert.Equal((1, 1, 0.75), CardPreviewLayout.RelatedGrid(1));
        Assert.Equal((3, 1, 0.75), CardPreviewLayout.RelatedGrid(3));
        Assert.Equal((2, 2, 0.75), CardPreviewLayout.RelatedGrid(4));
        Assert.Equal((3, 2, 0.75), CardPreviewLayout.RelatedGrid(6));
        Assert.Equal((4, 2, 0.5), CardPreviewLayout.RelatedGrid(7));
        Assert.Equal((4, 3, 0.5), CardPreviewLayout.RelatedGrid(12));
        Assert.Null(CardPreviewLayout.RelatedGrid(13));
    }

    [Fact]
    public void The_grid_stands_away_from_the_row_shrinks_when_short_of_room_and_else_is_left_out()
    {
        // The deck tracker at the right edge of a 1080p overlay: the big card left of it, the grid further left.
        var row = new PreviewRect(1660, 500, 240, 24);
        var card = new PreviewRect(1660 - 10 - 236, 333, 236, CardPreviewLayout.CardHeight);
        var four = CardPreviewLayout.PlaceRelated(4, card, row, 1920, 1080, 24, 1)!.Value;
        Assert.Equal((2, 2), (four.Columns, four.Rows));
        Assert.Equal(177, four.CardWidth, 0);
        Assert.Equal(card.X - 8 - ((2 * 177) + 4), four.Left, 0);
        Assert.Equal(card.Y, four.Top, 0);

        // Twelve: small cards, four by three.
        var twelve = CardPreviewLayout.PlaceRelated(12, card, row, 1920, 1080, 24, 1)!.Value;
        Assert.Equal((4, 3, 118), (twelve.Columns, twelve.Rows, Math.Round(twelve.CardWidth)));
        // A small overlay scale: never smaller than M.
        Assert.Equal(177, CardPreviewLayout.PlaceRelated(12, card, row, 1920, 1080, 24, 0.8)!.Value.CardWidth, 0);

        // A narrow window page, the list on the left, the card right of it: 6 at M do not fit, at S they do; at
        // 4 columns S not even that, so the preview stays one card. Never on the row's side.
        var listRow = new PreviewRect(240, 300, 300, 30);
        var pageCard = new PreviewRect(550, 100, 236, CardPreviewLayout.CardHeight);
        Assert.Equal(118, CardPreviewLayout.PlaceRelated(6, pageCard, listRow, 1200, 660, 24, 1)!.Value.CardWidth, 0);
        Assert.Null(CardPreviewLayout.PlaceRelated(12, pageCard, listRow, 1200, 660, 24, 1));

        // Near the bottom: bottom-aligned with the big card instead of running off.
        var low = card with { Y = 1080 - 4 - CardPreviewLayout.CardHeight };
        var placed = CardPreviewLayout.PlaceRelated(4, low, row, 1920, 1080, 24, 1)!.Value;
        var height = 24 + 4 + (2 * CardPreviewLayout.CardHeight * 0.75) + 4;
        Assert.Equal(low.Bottom - height, placed.Top, 0);
    }

    [Fact]
    public void The_wpf_host_shows_the_big_card_and_one_render_per_related_card() => Run(() =>
    {
        var rows = new Dictionary<string, IReadOnlyList<MatchListRow>>
        {
            ["BE_017"] = [.. new[] { "BE_017t3", "BE_017t4", "BE_017t5", "BE_017t6" }.Select(Row)],
            ["MANY"] = [.. Enumerable.Range(0, 13).Select(i => Row($"MANY_{i}"))],
        };
        var previous = CardPreview.Related;
        CardPreview.Related = id => rows.TryGetValue(id, out var r) ? r : [];
        var tracker = new StackPanel { Width = 240, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 300, 20, 0) };
        var row = new Border { Height = 24, Background = Brushes.Transparent };
        tracker.Children.Add(row);
        var host = new CardPreviewHost();
        var root = new Grid();
        root.Children.Add(tracker);
        root.Children.Add(host);
        var window = new Window
        {
            Width = 1920, Height = 1080, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            ShowActivated = false, Content = root,
        };
        try
        {
            window.Show();
            window.UpdateLayout();

            CardPreview.SetCardId(row, "BE_017");
            host.Show(row);
            Assert.Equal(["BE_017t3", "BE_017t4", "BE_017t5", "BE_017t6"], host.RelatedShown);
            Assert.Equal(177, host.RelatedCardWidth, 0);

            // The grid stands left of the big card, which stands left of the tracker: the row is never covered.
            var frame = (FrameworkElement)host.Children[0];
            var grid = (FrameworkElement)host.Children[1];
            Assert.True(Canvas.GetLeft(grid) + grid.DesiredSize.Width <= Canvas.GetLeft(frame));

            // Battlegrounds cards, more than twelve, a card standing for a hidden one, no related cards: one card.
            CardPreview.SetBattlegrounds(row, true);
            host.Show(row);
            Assert.Empty(host.RelatedShown);
            CardPreview.SetBattlegrounds(row, false);
            CardPreview.SetCardId(row, "MANY");
            host.Show(row);
            Assert.Empty(host.RelatedShown);
            CardPreview.SetCardId(row, "BE_017");
            CardPreview.SetShowRelated(row, false);
            host.Show(row);
            Assert.Empty(host.RelatedShown);
            CardPreview.SetShowRelated(row, true);
            CardPreview.SetCardId(row, "CS2_029");
            host.Show(row);
            Assert.Empty(host.RelatedShown);

            // No source (no card data yet): as before.
            CardPreview.Related = null;
            CardPreview.SetCardId(row, "BE_017");
            host.Show(row);
            Assert.Empty(host.RelatedShown);
        }
        finally
        {
            CardPreview.Related = previous;
            window.Close();
        }
    });

    private static MatchListRow Row(string id) => new(id, id, 0, string.Empty, 1);

    private static void Run(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
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
            throw new InvalidOperationException("Card preview with related cards failed", failure);
        }
    }
}
