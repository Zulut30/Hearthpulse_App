using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>
/// <c>--render-ui &lt;folder&gt;</c>: renders the dashboard pages and the overlay to PNGs with a headless renderer,
/// using the player's own history and their latest kept Battlegrounds log. Nothing is shown on screen.
/// </summary>
internal static partial class OffscreenRenderer
{
    public static int Run(string folder)
    {
        AppBuilder.Configure<HearthpulseApp>()
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
        using var host = new AppHost(settings, rendering: true);
        await host.InitializeAsync(watchLogs: false);
        await host.LoadMetaAsync();
        await host.Main.RefreshAsync();
        host.Main.Busy = string.Empty;

        var view = new MainView { DataContext = host.Main };
        var main = new Window { Width = 1260, Height = 840, Content = view, Background = ThemeBrushes.Get("Bg") };
        main.Show();
        foreach (var page in new[] { "Overview", "Battlegrounds", "Standard", "Wild", "History", "Settings" })
        {
            view.ShowPage(page);
            await Save(main, Path.Combine(folder, $"main-{page.ToLowerInvariant()}.png"));
        }

        await RenderSettingsTabsAsync(main, view, folder);

        // The Battlegrounds counters list sits far down the Tracker tab of the settings page.
        view.ShowPage("Settings");
        view.ShowSettingsTab("Tracker");
        if (view.FindControl<Control>("BgCountersHeading") is { } heading)
        {
            heading.BringIntoView(new Rect(0, 0, heading.Bounds.Width, 640));
            await Save(main, Path.Combine(folder, "main-settings-counters.png"));
        }

        await RenderMyStatsAsync(main, view, host, folder);
        await RenderBattlegroundsTiersAsync(main, view, folder);
        await RenderDeckPageAsync(main, view, host, folder);
        await RenderMatchPageAsync(main, view, host, folder);
        await RenderArenaPageAsync(main, view, host, folder);
        await RenderLadderPageAsync(main, view, host, folder);

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
        }

        await RenderCountersAsync(folder, host, settings, overlay, overlayView);
        await RenderCombatOddsAsync(folder, host, settings, overlay, overlayView);
        // The mulligan guide over the newest kept Standard game's opening hand (the meta's archetypes as the app's fallback).
        var constructedMeta = await Core.Constructed.ConstructedMeta.LoadAsync(new HearthpulseData(host.Api), host.Cards, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, host.Cards.Locale);
        // 3 cards going first, 4 on the coin, and the first one again without numbers (no API, no meta).
        foreach (var (offered, name, withData) in new[] { (3, "overlay-mulligan", true), (4, "overlay-mulligan-4", true), (3, "overlay-mulligan-nodata", false) })
        {
            if (host.InstallDirectory is { } install
                && await MulliganReplay.LatestAsync(install, host.Cards, withData ? new HearthpulseData(host.Api) : null, withData ? constructedMeta : null, offered) is { } mulligan)
            {
                host.Overlay.Update(LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = mulligan }, host.Cards, settings, null, null);
                overlayView.Fit(1920, 1080);
                await Save(overlay, Path.Combine(folder, name + ".png"));
                if (withData)
                {
                    // The bars stay over the cards at any overlay scale; only the bars themselves grow or shrink.
                    var scale = settings.OverlayScale;
                    foreach (var (s, suffix) in new[] { (0.6, "60"), (1.6, "160") })
                    {
                        settings.OverlayScale = s;
                        host.Overlay.Update(LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = mulligan }, host.Cards, settings, null, null);
                        overlayView.Fit(1920, 1080);
                        await Save(overlay, Path.Combine(folder, $"{name}-scale{suffix}.png"));
                    }

                    settings.OverlayScale = scale;
                    overlayView.Fit(1920, 1080);
                }
            }
            else
            {
                Console.WriteLine($"{Path.GetFileNameWithoutExtension(name)}: no kept Standard log with a known deck reaches a {offered}-card mulligan, skipped");
            }
        }
        await RenderConstructedAsync(host, settings, overlayView, overlay, folder);
        await RenderArenaOverlayAsync(host, settings, overlayView, overlay, folder);
        await RenderSecretsAsync(host, settings, overlayView, overlay, folder);
        await CardPoolRender.RenderAsync(host, settings, folder, path =>
        {
            overlayView.Fit(1920, 1080);
            return Save(overlay, path);
        });

        await RenderWidgetsAsync(host, settings, overlayView, overlay, folder);

        host.Overlay.Update(LiveSnapshot.Empty, host.Cards, settings, null, null);
        host.Overlay.LayoutEditing = true;
        await Save(overlay, Path.Combine(folder, "overlay-layout.png"));
        overlay.Close();
    }

    /// <summary>
    /// <c>overlay-counters*.png</c>: the Constructed counters of the latest kept game (or a made-up one) on the overlay,
    /// the panel alone, the panel with one counter opened by a click, and that counter's tooltip.
    /// </summary>
    private static async Task RenderCountersAsync(string folder, AppHost host, AppSettings settings, Window overlay, OverlayView overlayView)
    {
        var installDir = host.InstallDirectory;
        var view = (installDir is null ? null : await Task.Run(() => CounterScene.FromLatestLog(installDir, host.Cards))) ?? CounterScene.Synthetic(host.Cards);
        var snapshot = LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view };
        host.Overlay.Update(snapshot, host.Cards, settings, null, null);
        overlayView.Fit(1920, 1080);
        await Save(overlay, Path.Combine(folder, "overlay-counters.png"));
        if (overlayView.FindControl<Control>("CountersCard") is not { } card)
        {
            return;
        }

        SaveCrop(overlay, card, Path.Combine(folder, "overlay-counters-panel.png"));
        var chips = host.Overlay.OpponentCounters.Concat(host.Overlay.MyCounters).ToList();
        var shown = chips.FirstOrDefault(c => c.Id == "cthun") ?? chips.FirstOrDefault();
        if (shown is null)
        {
            return;
        }

        // The goal's parts (C'Thun) and, when a counter has them, the counted cards.
        var tips = new[] { (Chip: shown, Name: "overlay-counters-tooltip"), (Chip: chips.Where(c => c.Counted.Count > 0).MaxBy(c => c.Counted.Count), Name: "overlay-counters-tooltip-counted") };
        foreach (var (chip, name) in tips)
        {
            if (chip is null || !overlayView.TryFindResource("CounterTipTemplate", out var template) || template is not Avalonia.Controls.Templates.IDataTemplate tipTemplate)
            {
                continue;
            }

            // Dressed like the theme's ToolTip, which opens in a popup that the headless capture does not include; it sits on
            // the overlay itself so the overlay's styles apply.
            var tip = new Border
            {
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                Margin = new Thickness(40),
                Background = ThemeBrushes.Get("Surface4"),
                BorderBrush = ThemeBrushes.Get("Outline"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 7),
                Child = new ContentControl { Content = chip, ContentTemplate = tipTemplate },
            };
            var root = overlayView.FindControl<Panel>("Root")!;
            root.Children.Add(tip);
            await Save(overlay, Path.Combine(folder, name + "-full.png"));
            SaveCrop(overlay, tip, Path.Combine(folder, name + ".png"));
            root.Children.Remove(tip);
        }

        host.Overlay.ToggleCounter.Execute(shown.Id);
        await Save(overlay, Path.Combine(folder, "overlay-counters-open-full.png"));
        SaveCrop(overlay, card, Path.Combine(folder, "overlay-counters-open.png"));
        host.Overlay.ToggleCounter.Execute(shown.Id);
    }

    /// <summary>Writes the part of the window's last frame that shows the control, with a margin around it.</summary>
    private static void SaveCrop(Window window, Control control, string path)
    {
        const int pad = 12;
        if (window.CaptureRenderedFrame() is not { } frame || control.TranslatePoint(default, window) is not { } origin)
        {
            return;
        }

        var scale = frame.PixelSize.Width / window.Bounds.Width;
        var source = new Rect(origin.X * scale - pad, origin.Y * scale - pad, control.Bounds.Width * scale + 2 * pad, control.Bounds.Height * scale + 2 * pad)
            .Intersect(new Rect(0, 0, frame.PixelSize.Width, frame.PixelSize.Height));
        using var crop = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize((int)source.Width, (int)source.Height));
        using (var context = crop.CreateDrawingContext())
        {
            context.DrawImage(frame, source, new Rect(0, 0, source.Width, source.Height));
        }

        crop.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Console.WriteLine("wrote " + path);
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
