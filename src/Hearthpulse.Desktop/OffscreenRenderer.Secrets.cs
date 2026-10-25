using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Secrets;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the opponent's Secrets panel.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>Renders <c>overlay-secrets.png</c> from <see cref="SecretsScene"/> (two Mage Secrets, some ruled out).</summary>
    private static async Task RenderSecretsAsync(AppHost host, AppSettings settings, OverlayView overlayView, Window overlay, string folder)
    {
        try
        {
            if (SecretsScene.Synthetic(host.Cards) is not { } view)
            {
                Console.WriteLine("overlay-secrets: no card data, skipped");
                return;
            }

            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
            host.Overlay.Update(snapshot, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-secrets.png"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // The renders after this one (overlay-layout.png) must still be written.
            Console.WriteLine("overlay-secrets: " + ex.Message);
        }
    }
}
