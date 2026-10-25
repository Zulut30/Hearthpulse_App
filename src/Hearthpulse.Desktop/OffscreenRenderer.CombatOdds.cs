using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>Render-check scene of the combat odds panel: win/tie/loss, hero damage and lethal on a made-up fight.</summary>
internal static partial class OffscreenRenderer
{
    private static async Task RenderCombatOddsAsync(string folder, AppHost host, AppSettings settings, Window overlay, OverlayView overlayView)
    {
        try
        {
            var snapshot = LiveSnapshot.Empty with
            {
                Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_BATTLEGROUNDS", Battlegrounds = CombatOddsScene.Synthetic(),
            };
            host.Overlay.Update(snapshot, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-odds.png"));
            if (overlayView.FindControl<Control>("OddsCard") is { } card)
            {
                SaveCrop(overlay, card, Path.Combine(folder, "overlay-odds-panel.png"));
            }
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the combat odds failed", ex);
        }
    }
}
