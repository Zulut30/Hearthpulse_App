using System.IO;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the Graveyard panel.</summary>
public partial class App
{
    /// <summary>Renders <c>overlay-graveyard.png</c> from <see cref="GraveyardView.Sample"/> (both sides, repeats, "+N").</summary>
    private async Task RenderGraveyardAsync(string folder)
    {
        var shown = _settings.ShowGraveyard;
        try
        {
            _settings.ShowGraveyard = true;
            var view = new ConstructedView(5, null, false, "HERO_09", "PRIEST", "HERO_08", "MAGE", null, [], 20, 4, [], [], 18, 5, 0, null, null)
            {
                Graveyard = GraveyardView.Sample(_cards),
            };
            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-graveyard.png"), 1920, 1080, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // The renders after this one (overlay-layout.png) must still be written.
            Console.WriteLine("overlay-graveyard: " + ex.Message);
        }
        finally
        {
            _settings.ShowGraveyard = shown;
        }
    }
}
