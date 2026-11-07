using Avalonia.Controls;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>
/// Render-check scenes of the combat odds panel (win/tie/loss, hero damage and lethal on a made-up fight) and of the
/// Battlegrounds bar over it: five lobby tribes, four fights, then the tier 3 list with its tribe filter
/// (<c>overlay-bar-browser</c>).
/// </summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderCombatOddsAsync(string folder, AppHost host, AppSettings settings, Window overlay, OverlayView overlayView)
    {
        try
        {
            var snapshot = LiveSnapshot.Empty with
            {
                Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_BATTLEGROUNDS", Battlegrounds = BarScene(),
            };
            host.Overlay.Update(snapshot, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-odds.png"));
            // The card lives in the panel control (shared with the OBS window): its own name scope.
            if (overlayView.GetVisualDescendants().OfType<OddsPanel>().FirstOrDefault()?.FindControl<Control>("OddsCard") is { } card)
            {
                SaveCrop(overlay, card, Path.Combine(folder, "overlay-odds-panel.png"));
            }

            // The widest state: all five cells, wide damage both ways, an estimate and the cap in the bottom line.
            host.Overlay.Update(snapshot with { Battlegrounds = BarScene() with { CombatOdds = FiveCells(BarScene().CombatOdds!) } }, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-odds-full.png"));
            if (overlayView.GetVisualDescendants().OfType<OddsPanel>().FirstOrDefault()?.FindControl<Control>("OddsCard") is { } full)
            {
                SaveCrop(overlay, full, Path.Combine(folder, "overlay-odds-full-panel.png"));
            }

            // The bar between fights with the tier 3 list open and one tribe chosen in its head.
            host.Overlay.Update(snapshot with { Battlegrounds = BarScene() with { InCombat = false, CombatOdds = null } }, host.Cards, settings, null, null);
            host.Overlay.SelectTier.Execute(3);
            host.Overlay.SelectTribe.Execute("MECHANICAL");
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-bar-browser.png"));
            host.Overlay.CloseBrowser.Execute(null);
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the combat odds failed", ex);
        }
    }

    /// <summary>The odds with every cell shown: a kill chance, a lethal risk, two-digit damage ranges, one unsimulated effect.</summary>
    private static Core.Battlegrounds.CombatOdds FiveCells(Core.Battlegrounds.CombatOdds odds) => odds with
    {
        Unsimulated = 1,
        LethalDealt = 0.197,
        LethalTaken = 0.311,
        DamageDealt = new Core.Battlegrounds.DamageSpread(19, 12, 27, 31, [1]),
        DamageTaken = new Core.Battlegrounds.DamageSpread(23, 19, 27, 30, [1]),
    };

    /// <summary>The made-up fight on turn 11 at tavern tier 5, with five lobby tribes and four fights behind it.</summary>
    private static BattlegroundsView BarScene() => CombatOddsScene.Synthetic() with
    {
        Turn = 11,
        TechLevel = 5,
        Races = ["ABERRATION", "MECHANICAL", "ELEMENTAL", "QUILBOAR", "UNDEAD"],
        RacesConfirmed = true,
        Combats = [new(7, 2, "TB_BaconShop_HERO_08", "Win", 0, 6), new(8, 3, "TB_BaconShop_HERO_11", "Loss", 9, 0), new(9, 4, "TB_BaconShop_HERO_14", "Tie", 0, 0), new(10, 5, "TB_BaconShop_HERO_17", "Win", 0, 4)],
    };
}
