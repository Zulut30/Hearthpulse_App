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

    [Fact]
    public void The_pool_popup_shows_one_header_note_and_the_movable_panel_the_other() => Headless.Run(PoolPanelNotes);

    private static void PoolPanelNotes()
    {
        var entries = Enumerable.Range(0, 6).Select(i => new PoolEntry($"P_{i}", $"Card {i}", i, 1, 1, "MINION", "COMMON", "MAGE", 0)).ToList();
        var (cost, attack, health) = PoolStats.Histograms([]);
        var pool = new CardPoolView("GEN", "Generator", PoolTrigger.Hover, "Discover a minion", "Mage + Neutral · Standard", entries.Count,
            cost, attack, health, [], null, null, entries);
        var constructed = new Hearthpulse.Core.Tracking.ConstructedView(5, null, false, "HERO_03", "ROGUE", "HERO_01", "WARRIOR", null, [], 0, 0, [], [], 0, 0, 0, null, null)
        {
            CardPool = pool,
            CardPoolAnchor = new CardPoolAnchor(PoolAnchorZone.Hand, 1, 3),
        };
        var vm = new Hearthpulse.UI.Overlay.OverlayViewModel();
        vm.Update(Hearthpulse.Core.Tracking.LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = constructed },
            Hearthpulse.Core.Cards.CardDb.Empty, new Hearthpulse.Core.AppSettings(), null, null);
        // The theme first: the panel's markup looks its tokens up when it is created.
        var theme = Theme();
        var popup = new Hearthpulse.UI.Overlay.CardPoolPanel { Anchored = true, DataContext = vm };
        var panel = new Hearthpulse.UI.Overlay.CardPoolPanel { DataContext = vm };
        var window = new Window { Width = 1200, Height = 800, Content = new StackPanel { Children = { popup, panel } } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal((true, false), (popup.FindControl<TextBlock>("PopupNote")!.IsVisible, popup.FindControl<TextBlock>("PanelNote")!.IsVisible));
            Assert.Equal((false, true), (panel.FindControl<TextBlock>("PopupNote")!.IsVisible, panel.FindControl<TextBlock>("PanelNote")!.IsVisible));
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static MatchListRow RelatedRow(string id) => new(id, id, 0, string.Empty, 1);
}
