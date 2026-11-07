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
            // The player (second place) has none, nor Bob, who is out.
            Assert.Single(items);
            var slot = 0.69 / 8 * 1080;
            Assert.Equal(240 + (0.112 * 1080), Canvas.GetLeft(items[0]), 6);
            Assert.Equal(162, Canvas.GetTop(items[0]), 6);
            // Layout rounding moves the laid-out box by under a unit.
            Assert.InRange(items[0].Bounds.Height, slot - 1, slot + 1);
            Assert.InRange(items[0].Bounds.X, 240 + (0.112 * 1080) - 1, 240 + (0.112 * 1080) + 1);

            string[] Visible(Control item) => item.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToArray();
            // Alice: "Tavern 4" and her three murlocs of three, in words.
            Assert.Equal(["Tavern", "4", "Murlocs: 3 of 3"], Visible(items[0]));
            var badges = layer.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("bgBadge")).ToList();
            // One label, nothing held empty beside it.
            Assert.Empty(badges[0].ColumnDefinitions);
            // Centred on the tile.
            var label = badges[0].GetVisualDescendants().OfType<Control>().Single(b => b.Name == "Label");
            var centre = label.TranslatePoint(new Point(0, label.Bounds.Height / 2), layer)!.Value.Y;
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
