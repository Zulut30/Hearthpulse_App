using Avalonia;
using Avalonia.Controls;
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
    /// The Avalonia "bg-lobby" panel binds the lobby's tribes and one row per player: tier, the main tribe's count,
    /// nothing for an opponent never fought, and the dead player faded.
    /// </summary>
    [Fact]
    public void Bg_lobby_panel_binds_tribes_tiers_and_main_tribes() => Headless.Run(BgLobbyPanelBindings);

    private static void BgLobbyPanelBindings()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 300, Height = 400 };
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
            var unseen = new BgOpponent(3, "HERO_B", "Bob", 30, 0, 2, 3, true, 0, [], 0, null, 0, false);
            var view = new BattlegroundsView(7, 5, 6, "HERO_ME", 20, 0, 2, false, false, false, [], ["MURLOC", "BEAST"], false,
                [fought, unseen], [], [], [], null, 0, [], []);
            var model = new OverlayViewModel();
            model.Update(LiveSnapshot.Empty with { Battlegrounds = view, InGame = true, HearthstoneRunning = true }, cards, new AppSettings(), null, null);

            var panel = new BgLobbyPanel { DataContext = model };
            window.Content = panel;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts = panel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Contains("Tribes", texts);
            Assert.Contains("not confirmed", texts);
            Assert.Contains("Murloc", texts);
            Assert.Contains("Beast", texts);
            Assert.DoesNotContain(Loc.T("Overlay.Bar.ReadingLobby"), texts);

            var rows = panel.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("bgLobbyRow")).ToList();
            Assert.Equal(3, rows.Count);
            // Column 3, between the hero and the tier, waits for the opponents' rating: sized to its content, empty for now.
            Assert.True(rows[0].ColumnDefinitions[3].Width.IsAuto);
            Assert.DoesNotContain(rows[0].Children, c => Grid.GetColumn(c) == 3);
            string[] Visible(Grid row) => row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToArray();
            // Alice: place, name, tier 4, three murlocs.
            Assert.Equal(["1", "Alice", "4", model.LobbyRows[0].Tribe!.Glyph, "3"], Visible(rows[0]));
            // The player's own row: no board, no tribe; the name in the accent colour.
            Assert.Equal(["2", "Me", "5"], Visible(rows[1]));
            Assert.Equal(app.FindResource("Accent"), rows[1].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "HeroName").Foreground);
            // Bob, never fought and out of the game: nothing about his board, the row faded.
            Assert.Equal(["3", "Bob", "2"], Visible(rows[2]));
            Assert.Equal(0.35, rows[2].Opacity, 3);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
