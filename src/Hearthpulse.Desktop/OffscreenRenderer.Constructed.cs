using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the Constructed overlay (deck panel, opponent-hand marks, hover previews).</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>
    /// Renders the overlay at the most telling moment of the newest kept Constructed Power.log (read-only), then the
    /// hover preview of a deck row and of an opponent-hand mark. Skipped when no kept log has a Constructed game.
    /// </summary>
    private static async Task RenderConstructedAsync(AppHost host, AppSettings settings, OverlayView overlayView, Window overlay, string folder)
    {
        if (host.InstallDirectory is not { } install)
        {
            return;
        }

        // The stats LoadMetaAsync already fetched, as the WPF render uses: no second download here.
        var meta = host.ConstructedMeta;
        ConstructedView? view;
        DeckAggregate? deck;
        try
        {
            view = await Task.Run(() => ConstructedReplay.Latest(install, host.Cards, meta));
            deck = view is null
                ? null
                : await Task.Run(() => DeckRecords.Find(host.History.Recent(int.MaxValue, "Constructed"), view.DeckName, view.DeckCode, view.PlayerClass,
                    DecksLog.CodesOf(DecksLog.ReadAll(install), view.DeckId)));
        }
        catch (Exception ex)
        {
            // The renders after this one (overlay-layout.png) must still be written.
            Console.WriteLine("overlay-constructed: " + ex.Message);
            return;
        }

        if (view is null)
        {
            Console.WriteLine("overlay-constructed: no kept Constructed log, skipped");
            return;
        }

        var snapshot = LiveSnapshot.Empty with
        {
            Scene = "GAMEPLAY",
            HearthstoneRunning = true,
            InGame = true,
            GameType = "GT_RANKED",
            Constructed = view,
        };
        host.Overlay.Update(snapshot, host.Cards, settings, deck, null);
        overlayView.Fit(1920, 1080);
        await Save(overlay, Path.Combine(folder, "overlay-constructed.png"));

        try
        {
            if (overlayView.ShowPreviewFor(c => c.DataContext is DeckTile { Empty: false }))
            {
                await Save(overlay, Path.Combine(folder, "overlay-deck-preview.png"));
            }

            if (overlayView.ShowPreviewFor(c => c.DataContext is HandMark { PreviewCardId: not null }) || overlayView.ShowPreviewFor(c => c.DataContext is HandMark))
            {
                await Save(overlay, Path.Combine(folder, "overlay-hand-preview.png"));
            }
        }
        finally
        {
            overlayView.HidePreview();
        }
    }
}
