using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Hearthlens.Core;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Tracking;
using Hearthlens.UI;
using Hearthlens.UI.Infrastructure;
using Hearthlens.UI.Main;
using Hearthlens.UI.Overlay;

namespace Hearthlens.Desktop;

/// <summary>
/// <c>--render-ui &lt;folder&gt;</c>: renders the dashboard pages and the overlay to PNGs with a headless renderer,
/// using the player's own history and their latest kept Battlegrounds log. Nothing is shown on screen.
/// </summary>
internal static class OffscreenRenderer
{
    public static int Run(string folder)
    {
        AppBuilder.Configure<HearthlensApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        var exitCode = 0;
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await RenderAsync(folder);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                exitCode = 1;
            }
            finally
            {
                done.Cancel();
            }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return exitCode;
    }

    private static async Task RenderAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = AppSettings.Load();
        var host = new AppHost(settings);
        await host.InitializeAsync(watchLogs: false);
        await host.LoadMetaAsync();
        await host.Main.RefreshAsync();
        host.Main.Busy = string.Empty;

        var view = new MainView { DataContext = host.Main };
        var main = new Window { Width = 1260, Height = 840, Content = view, Background = ThemeBrushes.Get("Bg") };
        main.Show();
        foreach (var page in new[] { "Overview", "Battlegrounds", "Constructed", "History", "Settings" })
        {
            view.ShowPage(page);
            await Save(main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}.png"));
        }

        // Phone-sized dashboard: the compact layout used on narrow windows and mobile.
        main.Width = 420;
        main.Height = 860;
        view.ShowPage("Overview");
        await Save(main, Path.Combine(folder, "main-compact.png"));
        main.Close();

        var overlayView = new OverlayView { DataContext = host.Overlay, Settings = settings };
        var overlay = new Window
        {
            Width = 1920,
            Height = 1080,
            Content = overlayView,
            // Stand-in for the game behind the transparent overlay.
            Background = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(Color.FromRgb(0x4A, 0x3A, 0x2A), 0), new GradientStop(Color.FromRgb(0x1A, 0x13, 0x0D), 1) },
                RadiusX = new RelativeScalar(0.8, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.8, RelativeUnit.Relative),
            },
        };
        overlay.Show();
        overlayView.Fit(1920, 1080);

        var rating = host.History.RatingHistory().LastOrDefault()?.Rating;
        var moments = new (string Name, Func<BattlegroundsView, bool> When, int Hover)[]
        {
            ("overlay-pick", v => v is { IsHeroSelection: true, HeroOptions.Count: > 1 }, 0),
            ("overlay-trinket", v => v is { IsHeroSelection: false, TrinketOptions.Count: > 1 }, 0),
            ("overlay-discover", v => v is { IsHeroSelection: false, DiscoverOptions.Count: > 1 }, 0),
            ("overlay-game", v => v is { InCombat: false, Turn: >= 6, Shop.Count: >= 3 } && v.Opponents.Count(o => o.LastBoard.Count > 0) >= 3, 3),
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
        }

        host.Overlay.Update(LiveSnapshot.Empty, host.Cards, settings, null, null);
        host.Overlay.LayoutEditing = true;
        await Save(overlay, Path.Combine(folder, "overlay-layout.png"));
        overlay.Close();
    }

    /// <summary>Lets layout settle and card art load, then writes the window's rendered frame.</summary>
    private static async Task Save(Window window, string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        do
        {
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
        }
        while (CardArt.InFlight > 0 && DateTime.UtcNow < deadline);

        await Task.Delay(300);
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()?.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Console.WriteLine("wrote " + path);
    }
}
