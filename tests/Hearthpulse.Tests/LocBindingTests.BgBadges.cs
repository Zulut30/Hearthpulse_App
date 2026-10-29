using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The Avalonia leaderboard badges stand on the calibrated tiles and bind the tier, the main tribe and its count:
    /// nothing about a board never seen, the dead faded, no badge for the player.
    /// </summary>
    [Fact]
    public void Bg_badges_stand_on_the_leaderboard_tiles_and_bind_tier_and_tribe() => Headless.Run(BgBadgesBindings);

    private static void BgBadgesBindings()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 1920, Height = 1080 };
        try
        {
            var cards = CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""
                [
                  {"id":"BG_MURLOC","dbfId":21,"name":"Murloc","type":"MINION","races":["MURLOC"]},
                  {"id":"HERO_ME","dbfId":26,"name":"Me","type":"HERO"}
                ]
                """)));
            BgMinion murloc = new("BG_MURLOC", "Murloc", 2, 2, false, 1, []);
            var fought = new BgOpponent(2, "HERO_A", "Alice", 20, 0, 4, 1, false, 0, [murloc, murloc, murloc], 6, "Win", 3, false);
            var unseen = new BgOpponent(3, "HERO_B", "Bob", 0, 0, 2, 3, true, 0, [], 0, null, 0, false);
            var view = new BattlegroundsView(7, 5, 6, "HERO_ME", 20, 0, 2, false, false, false, [], ["MURLOC", "BEAST"], true,
                [fought, unseen], [], [], [murloc], null, 0, [], []);
            var settings = new AppSettings();
            var model = new OverlayViewModel();
            model.ArrangeLeaderboard(1920, 1080, settings);
            model.Update(LiveSnapshot.Empty with { Battlegrounds = view, InGame = true, HearthstoneRunning = true }, cards, settings, null, null);

            var layer = new BgBadgesLayer { DataContext = model };
            window.Content = layer;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.False(layer.IsHitTestVisible);
            var items = layer.GetVisualDescendants().OfType<ContentPresenter>().Where(p => p.DataContext is BgBadge).ToList();
            // The player (second place) has none.
            Assert.Equal(2, items.Count);
            var slot = 0.69 / 8 * 1080;
            Assert.Equal(240 + (0.112 * 1080), Canvas.GetLeft(items[0]), 6);
            Assert.Equal(162, Canvas.GetTop(items[0]), 6);
            Assert.Equal(162 + (2 * slot), Canvas.GetTop(items[1]), 6);
            // Layout rounding moves the laid-out box by under a unit.
            Assert.InRange(items[0].Bounds.Height, slot - 1, slot + 1);
            Assert.InRange(items[0].Bounds.X, 240 + (0.112 * 1080) - 1, 240 + (0.112 * 1080) + 1);

            string[] Visible(Control item) => item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToArray();
            // Alice: tier 4, three murlocs.
            Assert.Equal(["4", model.LobbyRows[0].Tribe!.Glyph, "3"], Visible(items[0]));
            // Bob: never fought, out of the game: the tier only, faded.
            Assert.Equal(["2"], Visible(items[1]));
            var badges = layer.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("bgBadge")).ToList();
            Assert.Equal(1, badges[0].Opacity, 3);
            Assert.Equal(0.35, badges[1].Opacity, 3);
            // Column 2 waits for the opponents' rating: sized to its content, empty for now.
            Assert.True(badges[0].ColumnDefinitions[2].Width.IsAuto);
            Assert.DoesNotContain(badges[0].Children, c => Grid.GetColumn(c) == 2);
            // Centred on the tile.
            var tier = badges[0].GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Tier");
            var centre = tier.TranslatePoint(new Point(0, tier.Bounds.Height / 2), layer)!.Value.Y;
            Assert.InRange(centre, 162 + (slot / 2) - 1, 162 + (slot / 2) + 1);

            settings.ShowBgLeaderboardBadges = false;
            model.Update(LiveSnapshot.Empty with { Battlegrounds = view, InGame = true, HearthstoneRunning = true }, cards, settings, null, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(layer.GetVisualDescendants().OfType<ItemsControl>().Single().IsVisible);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
