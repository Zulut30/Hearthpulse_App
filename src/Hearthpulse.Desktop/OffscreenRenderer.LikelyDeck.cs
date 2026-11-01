using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the "Likely deck" panel.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>Renders <c>overlay-likely-deck.png</c> from <see cref="LikelyDeckView.Sample"/> (a long list, played marks, off-list cards).</summary>
    private static async Task RenderLikelyDeckAsync(AppHost host, AppSettings settings, OverlayView overlayView, Window overlay, string folder)
    {
        var shown = settings.ShowLikelyDeck;
        try
        {
            settings.ShowLikelyDeck = true;
            var view = new ConstructedView(5, null, false, "HERO_09", "PRIEST", "HERO_08", "MAGE", null, [], 20, 4, [], [], 18, 5, 0, null, null)
            {
                OpponentDeck = LikelyDeckView.Sample(host.Cards),
            };
            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
            host.Overlay.Update(snapshot, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-likely-deck.png"));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // The renders after this one (overlay-layout.png) must still be written.
            Console.WriteLine("overlay-likely-deck: " + ex.Message);
        }
        finally
        {
            settings.ShowLikelyDeck = shown;
        }
    }
}
