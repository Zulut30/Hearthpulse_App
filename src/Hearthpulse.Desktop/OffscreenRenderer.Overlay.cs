using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.Core;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary><c>--render-ui</c>: the overlay window, the Battlegrounds moments on it, and a check of the overlay alone.</summary>
internal static partial class OffscreenRenderer
{
    /// <summary>
    /// <c>HEARTHPULSE_RENDER_ONLY=overlay</c>: only the overlay - the Battlegrounds moments of the newest kept log, the
    /// Constructed panels and layout mode - not the main window or the other overlay scenes.
    /// </summary>
    private static bool RenderOnlyOverlay =>
        string.Equals(Environment.GetEnvironmentVariable("HEARTHPULSE_RENDER_ONLY"), "overlay", StringComparison.OrdinalIgnoreCase);

    private static async Task RenderOverlayCheckAsync(AppHost host, AppSettings settings, string folder)
    {
        var (overlay, overlayView) = OpenOverlay(host, settings);
        await RenderBattlegroundsMomentsAsync(host, settings, overlay, overlayView, folder);
        await RenderConstructedAsync(host, settings, overlayView, overlay, folder);
        host.Overlay.Update(LiveSnapshot.Empty, host.Cards, settings, null, null);
        host.Overlay.LayoutEditing = true;
        await Save(overlay, Path.Combine(folder, "overlay-layout.png"));
        await SaveOtherScreensAsync(overlay, overlayView, folder, "overlay-layout");
        overlay.Close();
    }

    /// <summary>
    /// <paramref name="name"/> again on the 16:10 and 4:3 game screens (<see cref="VisualBaseline.Screens"/>), so no
    /// panel runs off a narrower screen or into another there (D5); the overlay is back at 1920 x 1080 after.
    /// </summary>
    private static async Task SaveOtherScreensAsync(Window overlay, OverlayView view, string folder, string name)
    {
        foreach (var (screen, width, height) in VisualBaseline.Screens.Skip(1))
        {
            overlay.Width = width;
            overlay.Height = height;
            view.Fit(width, height);
            await Save(overlay, Path.Combine(folder, $"{name}-{screen}.png"));
        }

        overlay.Width = 1920;
        overlay.Height = 1080;
        view.Fit(1920, 1080);
    }

    /// <summary>The overlay on a game screen of <paramref name="width"/> x <paramref name="height"/>, over a stand-in for the game.</summary>
    private static (Window Overlay, OverlayView View) OpenOverlay(AppHost host, AppSettings settings, int width = 1920, int height = 1080)
    {
        var view = new OverlayView { DataContext = host.Overlay, Settings = settings };
        var overlay = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            // Stand-in for the game behind the transparent overlay.
            Background = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Color.FromRgb(0x4A, 0x3A, 0x2A), 0), new GradientStop(Color.FromRgb(0x1A, 0x13, 0x0D), 1) },
                RadiusX = new RelativeScalar(0.8, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative),
            },
        };
        overlay.Show();
        view.Fit(width, height);
        return (overlay, view);
    }

    /// <summary>
    /// <c>overlay-pick</c>, <c>-trinket</c>, <c>-discover</c>, <c>-game</c> (with the third place's board open) and
    /// <c>-bg-counters</c>: moments replayed from the newest kept Battlegrounds log; a moment it never reaches is skipped.
    /// </summary>
    private static async Task RenderBattlegroundsMomentsAsync(AppHost host, AppSettings settings, Window overlay, OverlayView overlayView, string folder)
    {
        var rating = host.History.RatingHistory().LastOrDefault()?.Rating;
        var moments = new (string Name, Func<BattlegroundsView, bool> When, int Hover)[]
        {
            ("overlay-pick", v => v is { IsHeroSelection: true, HeroOptions.Count: > 1 }, 0),
            ("overlay-trinket", v => v is { IsHeroSelection: false, TrinketOptions.Count: > 1 }, 0),
            ("overlay-discover", v => v is { IsHeroSelection: false, DiscoverOptions.Count: > 1 }, 0),
            ("overlay-game", v => v is { InCombat: false, Turn: >= 6, Shop.Count: >= 3 } && v.Opponents.Count(o => o.LastBoard.Count > 0) >= 3, 3),
            ("overlay-bg-counters", v => v is { IsHeroSelection: false, HeroCardId: not null, InCombat: false, Turn: >= 2, Counters.Count: > 0 }, 0),
        };
        foreach (var (name, when, hover) in moments)
        {
            var battle = await Task.Run(() => host.ReplayLatestBattlegrounds(when));
            if (battle is null)
            {
                Console.WriteLine($"{name}: no kept Battlegrounds log reaches this moment, skipped");
                continue;
            }

            var advisor = new Core.Battlegrounds.BattlegroundsAdvisor { Meta = host.Meta };
            var snapshot = LiveSnapshot.Empty with
            {
                Scene = "GAMEPLAY",
                HearthstoneRunning = true,
                InGame = true,
                GameType = "GT_BATTLEGROUNDS",
                Battlegrounds = advisor.Enrich(battle, rating, host.Cards),
                Rating = rating is { } r ? new BattlegroundsRating(r, 0) : null,
            };
            host.Overlay.Update(snapshot, host.Cards, settings, null, null);
            overlayView.Fit(1920, 1080);
            overlayView.ShowLeaderboardSlot(hover);
            await Save(overlay, Path.Combine(folder, name + ".png"));
            if (name == "overlay-game")
            {
                // The popup stays on the narrower screens (it shares the top centre with the odds), then the leaderboard
                // labels alone: they hide while the mouse is on a tile.
                await SaveOtherScreensAsync(overlay, overlayView, folder, name);
                overlayView.Fit(1920, 1080);
                overlayView.ShowLeaderboardSlot(0);
                await Save(overlay, Path.Combine(folder, "overlay-game-badges.png"));
            }
        }
    }
}
