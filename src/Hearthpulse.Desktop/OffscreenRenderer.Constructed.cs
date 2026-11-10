using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the Constructed overlay (deck panel, opponent-hand marks, hover previews).</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=metatiers</c>: the Standard meta tier list as the page opens it (<c>main-meta-tiers</c>:
    /// the flat list of tier heads and archetypes beside the chosen archetype's card), as one table
    /// (<c>main-meta-table</c>) and at the compact width, the card under the list (<c>main-meta-tiers-compact</c>).
    /// Then on made-up stats (<see cref="Hearthpulse.Core.Constructed.MetaTierScene.States"/>) the states the data copy rarely has, an archetype of tier D
    /// chosen: <c>main-meta-states</c> and <c>main-meta-states-table</c>. The page's own stats are put back after
    /// them (when it had none, the made-up ones stay). The view and the width are set back.
    /// </summary>
    private static async Task RenderMetaTiersAsync(Window main, MainView view, AppHost host, string folder)
    {
        view.ShowPage("Standard");
        view.ShowConstructedTab("Tiers");
        foreach (var (mode, name) in new[] { ("Tiers", "main-meta-tiers"), ("Table", "main-meta-table") })
        {
            host.Main.SetMetaView.Execute(mode);
            await Save(main, Path.Combine(folder, name + ".png"));
        }

        host.Main.SetMetaView.Execute("Tiers");
        var width = main.Width;
        main.Width = 760;
        await Save(main, Path.Combine(folder, "main-meta-tiers-compact.png"));
        main.Width = width;

        var own = host.Main.ShownConstructedMeta;
        host.Main.SetConstructedMeta(Hearthpulse.Core.Constructed.MetaTierScene.States());
        host.Main.SelectArchetype.Execute(host.Main.MetaArchetypes.FirstOrDefault(r => r.Tier == "D"));
        foreach (var (mode, name) in new[] { ("Tiers", "main-meta-states"), ("Table", "main-meta-states-table") })
        {
            host.Main.SetMetaView.Execute(mode);
            await Save(main, Path.Combine(folder, name + ".png"));
        }

        host.Main.SetMetaView.Execute("Tiers");

        // The page's own stats again: the scenes after this one show real numbers.
        if (own is not null)
        {
            host.Main.SetConstructedMeta(own);
        }
    }

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
        await SaveOtherScreensAsync(overlay, overlayView, folder, "overlay-constructed");

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
