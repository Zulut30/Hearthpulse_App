using Avalonia.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the opponent's archetype, the turn timers and the ladder session (overlay and Overview).</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>main-overview-ladder: the Overview page with the ladder session card.</summary>
    private static async Task RenderLadderPageAsync(Window main, MainView view, AppHost host, string folder)
    {
        try
        {
            host.Main.Apply(LiveSnapshot.Empty with { Scene = "HUB", HearthstoneRunning = true, Ladder = WidgetScene.Ladder(DateTime.Now) });
            view.ShowPage("Overview");
            await Save(main, Path.Combine(folder, "main-overview-ladder.png"));
            host.Main.Apply(LiveSnapshot.Empty);
        }
        catch (Exception ex)
        {
            Console.WriteLine("main-overview-ladder: " + ex.Message);
        }
    }

    /// <summary>
    /// overlay-widgets (your turn on the rope) and overlay-widgets-opponent (their turn) on the newest kept Constructed
    /// game, and overlay-ladder in the game's menus.
    /// </summary>
    private static async Task RenderWidgetsAsync(AppHost host, AppSettings settings, OverlayView overlayView, Window overlay, string folder)
    {
        try
        {
            var meta = host.ConstructedMeta;
            var view = host.InstallDirectory is { } install ? await Task.Run(() => ConstructedReplay.Latest(install, host.Cards, meta)) : null;
            if (view is null)
            {
                Console.WriteLine("overlay-widgets: no kept Constructed log, skipped");
            }
            else
            {
                foreach (var (name, rope) in new[] { ("overlay-widgets", true), ("overlay-widgets-opponent", false) })
                {
                    var scene = WidgetScene.InGame(view, meta, host.Cards, DateTime.Now, rope);
                    host.Overlay.Update(LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = scene },
                        host.Cards, settings, null, null);
                    overlayView.Fit(1920, 1080);
                    await Save(overlay, Path.Combine(folder, name + ".png"));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("overlay-widgets: " + ex.Message);
        }

        try
        {
            host.Overlay.Update(LiveSnapshot.Empty with { Scene = "HUB", HearthstoneRunning = true, Ladder = WidgetScene.Ladder(DateTime.Now) }, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            await Save(overlay, Path.Combine(folder, "overlay-ladder.png"));
        }
        catch (Exception ex)
        {
            Console.WriteLine("overlay-ladder: " + ex.Message);
        }
    }
}
