using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the Graveyard panel.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>Renders <c>overlay-graveyard.png</c> from <see cref="GraveyardView.Sample"/> (both sides, repeats, "+N").</summary>
    private static async Task RenderGraveyardAsync(AppHost host, AppSettings settings, OverlayView overlayView, Window overlay, string folder)
    {
        var shown = settings.ShowGraveyard;
        try
        {
            settings.ShowGraveyard = true;
            var view = new ConstructedView(5, null, false, "HERO_09", "PRIEST", "HERO_08", "MAGE", null, [], 20, 4, [], [], 18, 5, 0, null, null)
            {
                Graveyard = GraveyardView.Sample(host.Cards),
            };
            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
            host.Overlay.Update(snapshot, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-graveyard.png"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // The renders after this one (overlay-layout.png) must still be written.
            Console.WriteLine("overlay-graveyard: " + ex.Message);
        }
        finally
        {
            settings.ShowGraveyard = shown;
        }
    }
}
