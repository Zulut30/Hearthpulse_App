using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>
/// Memory over a run of games in one process, the way the app lives through an evening (plan 0.2): the kept logs are
/// replayed through one <see cref="GameSession"/> whose snapshots go to the <see cref="MainViewModel"/> and to the shown
/// WPF <see cref="OverlayWindow"/> at 3840x2160 (the owner's screen), as the app shows a snapshot (LiveFeed). After the overlay is
/// shown and after every finished game: working set and private bytes as they are, then after a full collection with
/// finalizers (what is still held). Growth that levels off is caches warming up; growth with every game is a leak.
/// Off-screen Windows never composes the window: HP_BENCH_ONSCREEN=1 for its real cost (stage 11 of the perf audit).
/// HP_BENCH_MAIN=0: no main window (the app has let it go). --rounds n: the logs n times over.
/// </summary>
internal static class GamesBench
{
    public static void Run(Options options)
    {
        var (app, settings) = WpfBench.NewApplication();
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var api = new HearthpulseApi(http);
        var bgMeta = BattlegroundsMeta.LoadAsync(new HearthpulseData(api)).GetAwaiter().GetResult();
        var ctMeta = ConstructedMeta.LoadAsync(new HearthpulseData(api), cards, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, cards.Locale).GetAwaiter().GetResult();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());

        // The history the games are recorded into: a scratch copy, so the data copy's own history stays as it was.
        var db = Path.Combine(Path.GetTempPath(), $"hp-bench-games-{Guid.NewGuid():N}.db");
        var history = new HistoryStore(db);
        var vm = new MainViewModel(history, settings, Hearthpulse.Core.Hosting.AppData.FromAppPaths());
        MainWindow? main = null;
        if (Environment.GetEnvironmentVariable("HP_BENCH_MAIN") != "0")
        {
            main = WpfBench.OffScreen(new MainWindow(vm));
            main.Show();
            WpfBench.Pump();
        }

        vm.SetCards(cards);
        vm.SetMeta(bgMeta);
        vm.SetConstructedMeta(ctMeta);
        Memory("cards, meta, main window");

        var overlayVm = new OverlayViewModel();
        // HP_BENCH_ONSCREEN=1: over the whole primary screen, nearly invisible and click-through, so Windows composes it
        // as it does over the game (off-screen it is drawn but never composed).
        var onScreen = Environment.GetEnvironmentVariable("HP_BENCH_ONSCREEN") == "1";
        var (width, height) = onScreen ? (SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight) : (3840, 2160);
        var overlay = ShowOverlay(overlayVm, settings, width, height, onScreen);
        WindowBench.Run(TimeSpan.FromSeconds(3));
        var shown = string.Create(CultureInfo.InvariantCulture, $"overlay shown {width:F0}x{height:F0}{(onScreen ? " on screen" : string.Empty)}");
        Raw(shown);
        Memory(shown);

        var session = new GameSession(history, new Core.Memory.HearthstoneMemory()) { ReadsMemory = false, Cards = cards, IsHearthstoneRunning = () => true };
        session.SetMeta(bgMeta);
        session.SetConstructedMeta(ctMeta);
        var now = DateTime.UtcNow;
        session.Clock = () => now;
        LiveSnapshot? pending = null;
        session.SnapshotChanged += s => pending = s;
        var finished = new List<GameOutcome>();
        session.GameFinished += o =>
        {
            if (o.RatingAfter is null)
            {
                finished.Add(o);
            }
        };

        var pace = int.TryParse(Environment.GetEnvironmentVariable("HP_BENCH_PACE"), CultureInfo.InvariantCulture, out var p) ? p : 0;
        var historyChanged = false;
        history.Changed += () => historyChanged = true;
        var logs = LogReplay.Logs(options.Logs);
        var games = 0;
        var started = Stopwatch.StartNew();
        for (var round = 0; round < Math.Max(1, options.Rounds); round++)
        {
            foreach (var (dir, path, start) in logs)
            {
                DateTime? paced = null;
                LogReplay.Run(session, t => now = t, dir, path, start, onTick: tick =>
                {
                    tick();
                    // HP_BENCH_PACE=n: the dispatcher runs for the log time / n (quiet gaps cut to 5 s), so the overlay
                    // draws, animates and hovers for as long as in a game, n times faster.
                    if (pace > 0)
                    {
                        var gap = paced is { } last ? Math.Min(5000, (now - last).TotalMilliseconds) : 0;
                        paced = now;
                        if (gap / pace >= 1)
                        {
                            WindowBench.Run(TimeSpan.FromMilliseconds(gap / pace));
                        }
                    }

                    if (pending is { } snapshot)
                    {
                        pending = null;
                        vm.Apply(snapshot);
                        overlayVm.Update(snapshot, cards, settings, null, null);
                        WpfBench.Pump(DispatcherPriority.Background);
                    }

                    if (finished.Count > 0)
                    {
                        foreach (var outcome in finished)
                        {
                            if (settings.ShowGameToasts)
                            {
                                overlayVm.ShowOutcome(outcome);
                            }
                        }

                        finished.Clear();
                        games++;
                        // App: history.Changed -> the debounced History refresh of the main window.
                        if (historyChanged)
                        {
                            historyChanged = false;
                            var refresh = vm.RefreshAsync();
                            while (!refresh.IsCompleted)
                            {
                                WpfBench.Pump(DispatcherPriority.Background);
                            }
                        }

                        // Let the page refreshes, the art loads and the toast run out as they would between games.
                        WindowBench.Run(TimeSpan.FromSeconds(2));
                        // HP_BENCH_DROPART=1 (experiment): the image caches emptied before the count, so what is left
                        // of the growth is not the card art they hold.
                        if (Environment.GetEnvironmentVariable("HP_BENCH_DROPART") == "1")
                        {
                            foreach (var cache in new[] { typeof(CardArt), typeof(CardRender) })
                            {
                                (cache.GetField("Memory", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as SizedCache<ImageSource>)?.Clear();
                            }

                            WindowBench.Run(TimeSpan.FromMilliseconds(500));
                        }

                        var label = string.Create(CultureInfo.InvariantCulture, $"game {games,2} ({Path.GetFileName(dir)[12..]}, {started.Elapsed.TotalSeconds:N0} s)");
                        Raw(label);
                        Memory(label);
                    }
                });
            }
        }

        WindowBench.Quiet();
        WindowBench.Run(TimeSpan.FromSeconds(3));
        Raw($"after {games} games, quiet");
        Memory($"after {games} games, quiet");
        WpfBench.ImageCaches();

        // Hearthstone closed: OverlayWindow.Tick hides the content (Root) and leaves the window at the game's size.
        var root = (Grid)typeof(OverlayWindow).GetField("Root", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay)!;
        root.Visibility = Visibility.Hidden;
        WindowBench.Run(TimeSpan.FromSeconds(3));
        Memory("game closed: overlay content hidden");

        overlay.Width = overlay.Height = 1;
        WindowBench.Run(TimeSpan.FromSeconds(3));
        Memory("game closed: overlay window 1x1");

        overlay.Close();
        if (main is not null)
        {
            WpfBench.Close(main, settings);
        }

        WindowBench.Run(TimeSpan.FromSeconds(2));
        Memory("windows closed");
        LogReplay.Delete(db);
        GC.KeepAlive(app);
        GC.KeepAlive(vm);
    }

    /// <summary>Working set and private bytes as they are, before any collection.</summary>
    private static void Raw(string label)
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[raw] {label,-40} WS {process.WorkingSet64 / 1048576,4} MB  private {process.PrivateMemorySize64 / 1048576,4} MB  managed {GC.GetTotalMemory(false) / 1048576,4} MB"));
    }

    /// <summary>The real overlay window off-screen at the game's size, laid out as OverlayWindow does over the game.</summary>
    private static OverlayWindow ShowOverlay(OverlayViewModel vm, Core.AppSettings settings, double width, double height, bool onScreen)
    {
        var window = new OverlayWindow(vm, settings)
        {
            Offscreen = true,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = onScreen ? 0 : -20000 - width,
            Top = onScreen ? 0 : -20000,
            Width = width,
            Height = height,
            Opacity = onScreen ? 0.02 : 1,
        };
        window.Show();
        var type = typeof(OverlayWindow);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var root = (Grid)type.GetField("Root", flags)!.GetValue(window)!;
        var surface = (Grid)type.GetField("Surface", flags)!.GetValue(window)!;
        var scaleTransform = (ScaleTransform)type.GetField("SurfaceScale", flags)!.GetValue(window)!;
        var scale = height / 1080.0;
        root.Visibility = Visibility.Visible;
        type.GetField("_scale", flags)!.SetValue(window, scale);
        type.GetField("_dpi", flags)!.SetValue(window, onScreen ? VisualTreeHelper.GetDpi(window).DpiScaleY : 1.0);
        scaleTransform.ScaleX = scaleTransform.ScaleY = scale;
        surface.Width = width / scale;
        surface.Height = height / scale;
        type.GetField("_layoutSize", flags)!.SetValue(window, new Size(surface.Width, surface.Height));
        type.GetMethod("ApplyLayout", flags)!.Invoke(window, null);
        // HP_BENCH_SOFTWARE=1 (experiment, as in frames): the overlay drawn without Direct3D.
        if (Environment.GetEnvironmentVariable("HP_BENCH_SOFTWARE") == "1")
        {
            ((System.Windows.Interop.HwndSource)PresentationSource.FromVisual(window)!).CompositionTarget!.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        return window;
    }
}
