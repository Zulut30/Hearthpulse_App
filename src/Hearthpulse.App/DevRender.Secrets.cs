using System.IO;
using Hearthpulse.Core.Secrets;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the opponent's Secrets panel.</summary>
public partial class App
{
    /// <summary>Renders <c>overlay-secrets.png</c> from <see cref="SecretsScene"/> (two Mage Secrets, some ruled out).</summary>
    private async Task RenderSecretsAsync(string folder)
    {
        try
        {
            if (SecretsScene.Synthetic(_cards) is not { } view)
            {
                Console.WriteLine("overlay-secrets: no card data, skipped");
                return;
            }

            var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
            _overlayViewModel.Update(snapshot, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-secrets.png"), 1920, 1080, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            // The renders after this one (overlay-layout.png) must still be written.
            Console.WriteLine("overlay-secrets: " + ex.Message);
        }
    }
}
