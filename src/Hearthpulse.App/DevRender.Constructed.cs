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
    /// Renders the overlay at the most telling moment of the newest kept Constructed Power.log (read-only), then the
    /// hover preview of a deck row and of an opponent-hand mark. Skipped when no kept log has a Constructed game.
    /// </summary>
    private async Task RenderConstructedOverlayAsync(string folder)
    {
        if (_installDir is not { } install)
        {
            return;
        }

        var view = await Task.Run(() => ConstructedReplay.Latest(install, _cards, _constructedMeta));
        if (view is null)
        {
            Console.WriteLine("overlay-constructed: no kept Constructed log, skipped");
            return;
        }

        var deck = await Task.Run(() => DeckRecords.Find(_history.Recent(int.MaxValue, "Constructed"), view.DeckName, view.DeckCode, view.PlayerClass,
            DecksLog.CodesOf(DecksLog.ReadAll(install), view.DeckId)));
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

        if (_overlay.PreviewHost.ShowFirst(_overlay, e => e.DataContext is DeckTile { Empty: false }))
        {
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-deck-preview.png"), 1920, 1080, 0);
        }

        if (_overlay.PreviewHost.ShowFirst(_overlay, e => e.DataContext is HandMark { PreviewCardId: not null })
            || _overlay.PreviewHost.ShowFirst(_overlay, e => e.DataContext is HandMark))
        {
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-hand-preview.png"), 1920, 1080, 0);
        }

        _overlay.PreviewHost.Hide();
    }
}
