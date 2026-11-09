using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Hearthpulse.Core.CardPool;
using Hearthpulse.UI.Controls;

namespace Hearthpulse.Tests;

/// <summary>
/// The Avalonia hover card with its related cards: the big card plus one render per related card, left of it when the
/// row is at the right edge; none for Battlegrounds cards, more than twelve, a card standing for a hidden one or no
/// source (the WPF twin and the shared layout: Hearthpulse.Presentation.Tests, CardPreviewRelatedTests).
/// </summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Card_preview_shows_the_related_cards_beside_the_big_card() => Headless.Run(PreviewWithRelated);

    private static void PreviewWithRelated()
    {
        var rows = new Dictionary<string, IReadOnlyList<MatchListRow>>
        {
            ["BE_017"] = [.. new[] { "BE_017t3", "BE_017t4", "BE_017t5", "BE_017t6" }.Select(RelatedRow)],
            ["MANY"] = [.. Enumerable.Range(0, 13).Select(i => RelatedRow($"MANY_{i}"))],
        };
        var previous = CardPreview.Related;
        CardPreview.Related = id => rows.TryGetValue(id, out var r) ? r : [];
        var row = new Border { Height = 24, Background = Brushes.Transparent };
        var tracker = new StackPanel { Width = 240, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 300, 20, 0) };
        tracker.Children.Add(row);
        var host = new CardPreviewHost();
        var root = new Grid();
        root.Children.Add(tracker);
        root.Children.Add(host);
        var theme = Theme();
        var window = new Window { Width = 1920, Height = 1080, Content = root };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            CardPreview.SetCardId(row, "BE_017");
            host.Show(row);
            Assert.Equal(["BE_017t3", "BE_017t4", "BE_017t5", "BE_017t6"], host.RelatedShown);
            Assert.Equal(177, host.RelatedCardWidth, 0);
            var frame = host.Children[0];
            var grid = host.Children[1];
            Assert.True(Canvas.GetLeft(grid) + grid.DesiredSize.Width <= Canvas.GetLeft(frame));

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
            CardPreview.Related = null;
            host.Show(row);
            Assert.Empty(host.RelatedShown);
        }
        finally
        {
            CardPreview.Related = previous;
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static MatchListRow RelatedRow(string id) => new(id, id, 0, string.Empty, 1);
}
