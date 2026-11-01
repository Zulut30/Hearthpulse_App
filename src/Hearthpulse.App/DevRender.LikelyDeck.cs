using System.IO;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the "Likely deck" panel.</summary>
public partial class App
{
    /// <summary>Renders <c>overlay-likely-deck.png</c> from <see cref="LikelyDeckView.Sample"/> (a long list, played marks, off-list cards).</summary>
    private async Task RenderLikelyDeckAsync(string folder)
    {
        var shown = _settings.ShowLikelyDeck;
        try
        {
            _settings.ShowLikelyDeck = true;
            var view = new ConstructedView(5, null, false, "HERO_09", "PRIEST", "HERO_08", "MAGE", null, [], 20, 4, [], [], 18, 5, 0, null, null)
            {
                OpponentDeck = LikelyDeckView.Sample(_cards),
            };
            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-likely-deck.png"), 1920, 1080, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // The renders after this one (overlay-layout.png) must still be written.
            Console.WriteLine("overlay-likely-deck: " + ex.Message);
        }
        finally
        {
            _settings.ShowLikelyDeck = shown;
        }
    }
}
