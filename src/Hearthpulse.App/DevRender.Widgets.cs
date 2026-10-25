using System.IO;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App;

/// <summary><c>--render-ui</c>: the opponent's archetype, the turn timers and the ladder session (overlay and Overview).</summary>
public partial class App
{
    /// <summary>
    /// overlay-widgets (your turn on the rope) and overlay-widgets-opponent (their turn) on the newest kept Constructed
    /// game; overlay-ladder in the game's menus; main-overview-ladder with the session card. Each one on its own, so a
    /// failure leaves the renders after it.
    /// </summary>
    private async Task RenderWidgetsAsync(string folder)
    {
        try
        {
            var view = _installDir is { } install ? await Task.Run(() => ConstructedReplay.Latest(install, _cards, _constructedMeta)) : null;
            if (view is null)
            {
                Console.WriteLine("overlay-widgets: no kept Constructed log, skipped");
            }
            else
            {
                foreach (var (name, rope) in new[] { ("overlay-widgets", true), ("overlay-widgets-opponent", false) })
                {
                    var scene = WidgetScene.InGame(view, _constructedMeta, _cards, DateTime.Now, rope);
                    _overlayViewModel.Update(LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = scene },
                        _cards, _settings, null, null);
                    await _overlay.RenderToFileAsync(Path.Combine(folder, name + ".png"), 1920, 1080, 0);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("overlay-widgets: " + ex.Message);
        }

        try
        {
            var menu = LiveSnapshot.Empty with { Scene = "HUB", HearthstoneRunning = true, Ladder = WidgetScene.Ladder(DateTime.Now) };
            _overlayViewModel.Update(menu, _cards, _settings, null, null);
            await _overlay.RenderToFileAsync(Path.Combine(folder, "overlay-ladder.png"), 1920, 1080, 0);

            _mainViewModel.Apply(menu);
            _main.ShowPage("Overview");
            await Task.Delay(1500);
            _main.UpdateLayout();
            Save(_main, Path.Combine(folder, "main-overview-ladder.png"));
            _mainViewModel.Apply(LiveSnapshot.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine("overlay-ladder: " + ex.Message);
        }
    }
}
