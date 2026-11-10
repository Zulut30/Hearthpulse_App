using System.IO;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the Constructed overlay (deck panel, opponent-hand marks, hover previews).</summary>
public partial class App
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=metatiers</c>: the Standard meta tier list as the page opens it (<c>main-meta-tiers</c>:
    /// the flat list of tier heads and archetypes beside the chosen archetype's card) and as one table
    /// (<c>main-meta-table</c>), then on made-up stats (<see cref="Hearthpulse.Core.Constructed.MetaTierScene.States"/>) the states the data copy rarely
    /// has, an archetype of tier D chosen: <c>main-meta-states</c> and <c>main-meta-states-table</c>. The page's own
    /// stats are put back after them (when it had none, the made-up ones stay).
    /// </summary>
    private async Task RenderMetaTiersAsync(string folder)
    {
        _main.ShowPage("Standard");
        _main.ShowConstructedTab("Tiers");
        foreach (var (view, name) in new[] { ("Tiers", "main-meta-tiers"), ("Table", "main-meta-table") })
        {
            _mainViewModel.SetMetaView.Execute(view);
            await Task.Delay(2500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, name + ".png"));
        }

        var own = _mainViewModel.ShownConstructedMeta;
        _mainViewModel.SetConstructedMeta(Hearthpulse.Core.Constructed.MetaTierScene.States());
        _mainViewModel.SelectArchetype.Execute(_mainViewModel.MetaArchetypes.FirstOrDefault(r => r.Tier == "D"));
        foreach (var (view, name) in new[] { ("Tiers", "main-meta-states"), ("Table", "main-meta-states-table") })
        {
            _mainViewModel.SetMetaView.Execute(view);
            await Task.Delay(1500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, name + ".png"));
        }

        _mainViewModel.SetMetaView.Execute("Tiers");

        // The page's own stats again: the scenes after this one show real numbers.
        if (own is not null)
        {
            _mainViewModel.SetConstructedMeta(own);
        }
    }

    /// <summary>
    /// Renders the overlay at the most telling moment of the newest kept Constructed Power.log (read-only), then the
    /// hover preview of a deck row and of an opponent-hand mark. Skipped when no kept log has a Constructed game.
    /// </summary>
    private async Task RenderConstructedOverlayAsync(string folder)
    {
        if (_installDir is not { } install)
        {
            return;
        }

        ConstructedView? view;
        DeckAggregate? deck;
        try
        {
            view = await Task.Run(() => ConstructedReplay.Latest(install, _cards, _constructedLoader.Default));
            deck = view is null
                ? null
                : await Task.Run(() => DeckRecords.Find(_history.Recent(int.MaxValue, "Constructed"), view.DeckName, view.DeckCode, view.PlayerClass,
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
        _overlayViewModel.Update(snapshot, _cards, _settings, deck, null);
        await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-constructed.png"), 1920, 1080, 0);
        await RenderOtherScreensAsync(folder, "overlay-constructed");

        try
        {
            if (_overlay.PreviewHost.ShowFirst(_overlay, e => e.DataContext is DeckTile { Empty: false }))
            {
                await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-deck-preview.png"), 1920, 1080, 0);
            }

            if (_overlay.PreviewHost.ShowFirst(_overlay, e => e.DataContext is HandMark { PreviewCardId: not null })
                || _overlay.PreviewHost.ShowFirst(_overlay, e => e.DataContext is HandMark))
            {
                await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-hand-preview.png"), 1920, 1080, 0);
            }
        }
        finally
        {
            _overlay.PreviewHost.Hide();
        }
    }
}
