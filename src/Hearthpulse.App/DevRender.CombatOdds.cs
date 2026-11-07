using System.IO;
using System.Windows;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary>
/// Render-check scenes of the combat odds panel (win/tie/loss, hero damage and lethal on a made-up fight) and of the
/// Battlegrounds bar over it: five lobby tribes, four fights, then the tier 3 list with its tribe filter
/// (<c>overlay-bar-browser</c>).
/// </summary>
public partial class App
{
    private async Task RenderCombatOddsAsync(string folder)
    {
        try
        {
            var snapshot = LiveSnapshot.Empty with
            {
                Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_BATTLEGROUNDS", Battlegrounds = BarScene(),
            };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-odds.png"), 1920, 1080, 0);
            // The card lives in the panel control (shared with the OBS window): its own name scope.
            if (FindLogical<Overlay.OddsPanel>(_overlay)?.FindName("OddsCard") is FrameworkElement card)
            {
                SaveScaled(card, Path.Combine(folder, "overlay-odds-panel.png"));
            }

            // The bar between fights with the tier 3 list open and one tribe chosen in its head.
            _overlayViewModel.Update(snapshot with { Battlegrounds = BarScene() with { InCombat = false, CombatOdds = null } }, _cards, _settings, null, null);
            _overlayViewModel.SelectTier.Execute(3);
            _overlayViewModel.SelectTribe.Execute("MECHANICAL");
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-bar-browser.png"), 1920, 1080, 0);
            _overlayViewModel.CloseBrowser.Execute(null);
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the combat odds failed", ex);
        }
    }

    /// <summary>The made-up fight on turn 11 at tavern tier 5, with five lobby tribes and four fights behind it.</summary>
    private static Core.Tracking.BattlegroundsView BarScene() => CombatOddsScene.Synthetic() with
    {
        Turn = 11,
        TechLevel = 5,
        Races = ["ABERRATION", "MECHANICAL", "ELEMENTAL", "QUILBOAR", "UNDEAD"],
        RacesConfirmed = true,
        Combats = [new(7, 2, "TB_BaconShop_HERO_08", "Win", 0, 6), new(8, 3, "TB_BaconShop_HERO_11", "Loss", 9, 0), new(9, 4, "TB_BaconShop_HERO_14", "Tie", 0, 0), new(10, 5, "TB_BaconShop_HERO_17", "Win", 0, 4)],
    };

    /// <summary>The first <typeparamref name="T"/> in the logical tree under <paramref name="root"/>.</summary>
    private static T? FindLogical<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T found)
            {
                return found;
            }

            if (FindLogical<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
