using System.IO;
using System.Windows;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary>Render-check scene of the combat odds panel: win/tie/loss, hero damage and lethal on a made-up fight.</summary>
public partial class App
{
    private async Task RenderCombatOddsAsync(string folder)
    {
        try
        {
            var snapshot = LiveSnapshot.Empty with
            {
                Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_BATTLEGROUNDS", Battlegrounds = CombatOddsScene.Synthetic(),
            };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-odds.png"), 1920, 1080, 0);
            // The card lives in the panel control (shared with the OBS window): its own name scope.
            if (FindLogical<Overlay.OddsPanel>(_overlay)?.FindName("OddsCard") is FrameworkElement card)
            {
                SaveScaled(card, Path.Combine(folder, "overlay-odds-panel.png"));
            }
        }
        catch (IOException ex)
        {
            Diag.Error("Render of the combat odds failed", ex);
        }
    }

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
