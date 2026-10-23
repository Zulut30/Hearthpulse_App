using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>
/// Benchmarks of the WPF app's view models and windows. A plain <see cref="Application"/> carries the theme:
/// Hearthpulse.App.App would run its OnStartup (single-instance mutex, tray, log watcher) on the first frame.
/// Windows are placed off-screen.
/// </summary>
internal static class WpfBench
{
    public static void Overlay(Options options)
    {
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var settings = AppSettings.Load();
        var json = new JsonSerializerOptions { MaxDepth = 16, ReferenceHandler = ReferenceHandler.IgnoreCycles };
        var overlay = new OverlayViewModel();
        var last = new Dictionary<string, string?>();
        var changed = new Dictionary<string, (int Real, int Same, int Unknown)>();
        overlay.PropertyChanged += (_, e) =>
        {
            var name = e.PropertyName ?? "*";
            string? now;
            try
            {
                now = JsonSerializer.Serialize(typeof(OverlayViewModel).GetProperty(name)?.GetValue(overlay), json);
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or TargetInvocationException or ArgumentException)
            {
                now = null;
            }

            var c = changed.GetValueOrDefault(name);
            if (now is null)
            {
                c.Unknown++;
            }
            else if (last.TryGetValue(name, out var before) && before == now)
            {
                c.Same++;
            }
            else
            {
                c.Real++;
            }

            last[name] = now;
            changed[name] = c;
        };

        var times = new List<double>();
        foreach (var (dir, path, start) in LogReplay.Logs(options.Logs))
        {
            var (session, setClock, db) = LogReplay.NewSession(cards);
            session.SnapshotChanged += s =>
            {
                var t = Stopwatch.GetTimestamp();
                overlay.Update(s, cards, settings, null, null);
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            };
            LogReplay.Run(session, setClock, dir, path, start);
            LogReplay.Delete(db);
        }

        Console.WriteLine($"[overlay] {times.Count:N0} snapshots; OverlayViewModel.Update {Spread(times)}");
        foreach (var (name, c) in changed.OrderByDescending(x => x.Value.Real + x.Value.Same + x.Value.Unknown).Take(20))
        {
            Console.WriteLine($"[overlay] {name,-26} real {c.Real,6:N0}  same content {c.Same,6:N0}  unknown {c.Unknown,4:N0}");
        }

        var total = changed.Values.Sum(c => c.Real + c.Same + c.Unknown);
        var same = changed.Values.Sum(c => c.Same);
        Console.WriteLine($"[overlay] PropertyChanged {total:N0} ({(double)total / Math.Max(1, times.Count):N1}/snapshot), same content {same:N0} ({100.0 * same / Math.Max(1, total):N0}%)");
    }

    public static void Startup()
    {
        using (var process = Process.GetCurrentProcess())
        {
            Console.WriteLine($"[start] process start -> Main {(DateTime.Now - process.StartTime).TotalMilliseconds,8:N0} ms");
        }

        var sw = Stopwatch.StartNew();
        void Step(string what)
        {
            Console.WriteLine($"[start] {what,-36} {sw.Elapsed.TotalMilliseconds,8:N0} ms");
            sw.Restart();
        }

        var (app, settings) = NewApplication();
        Step("Application + settings + theme");
        var history = new HistoryStore(AppPaths.Database);
        Step("HistoryStore open");
        var main = OffScreen(new MainWindow(new MainViewModel(history, settings)));
        Step("new MainWindow");
        var overlay = new OverlayWindow(new OverlayViewModel(), settings);
        Step("new OverlayWindow");
        var frame = new DispatcherFrame();
        main.ContentRendered += (_, _) => frame.Continue = false;
        main.Show();
        Dispatcher.PushFrame(frame);
        Step("Show -> ContentRendered");
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        Step("CardDb.LoadAsync on this thread");
        Memory("after start-up");
        Close(main, settings);
        overlay.Close();
        GC.KeepAlive(app);
        GC.KeepAlive(cards);
    }

    public static void Ram(Options options)
    {
        var n = options.Games > 0 ? options.Games : 3000;
        Memory("process start");
        var (app, settings) = NewApplication();
        Memory("WPF Application + theme");
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        Memory("CardDb ruRU (+hsbg)");
        var api = new HearthpulseApi(http);
        var bgMeta = BattlegroundsMeta.LoadAsync(new HearthpulseData(api)).GetAwaiter().GetResult();
        Memory("+ Battlegrounds meta");
        var ctMeta = ConstructedMeta.LoadAsync(new HearthpulseData(api), cards, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, cards.Locale).GetAwaiter().GetResult();
        Memory("+ Constructed meta");
        var catalog = CardPoolCatalog.LoadAsync(api, http).GetAwaiter().GetResult();
        Memory($"+ card pool catalog ({catalog.Count})");

        // The history is kept between runs (same n): filling it takes about 13 ms a game.
        var history = new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-bench-ram-{n}.db"));
        if (history.GameCount() < n)
        {
            CoreBench.Fill(history, CoreBench.SeedGames(options.Logs, cards), n);
        }

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var vm = new MainViewModel(history, settings);
        var main = OffScreen(new MainWindow(vm));
        Memory($"+ history {history.GameCount()} games, new MainWindow");
        main.Show();
        Pump();
        Memory("MainWindow shown");

        var sw = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes();
        var paused = GC.GetTotalPauseDuration();
        var gen2 = GC.CollectionCount(2);
        void Step(string what)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[pages] {what,-38} {sw.Elapsed.TotalMilliseconds,8:N0} ms  alloc {(GC.GetTotalAllocatedBytes() - allocated) / 1048576.0,6:N1} MB  GC pause {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds,5:N0} ms  gen2 {GC.CollectionCount(2) - gen2}"));
            sw.Restart();
            allocated = GC.GetTotalAllocatedBytes();
            paused = GC.GetTotalPauseDuration();
            gen2 = GC.CollectionCount(2);
        }

        vm.SetCards(cards);
        vm.SetMeta(bgMeta);
        vm.SetConstructedMeta(ctMeta);
        Step("SetCards + SetMeta + SetConstructedMeta");
        var refresh = vm.RefreshAsync();
        while (!refresh.IsCompleted)
        {
            Pump(DispatcherPriority.Background);
        }

        Pump();
        Step("RefreshAsync (Overview)");
        foreach (var page in new[] { "Battlegrounds", "Constructed", "Arena", "History", "Settings", "Overview" })
        {
            main.ShowPage(page);
            Pump();
            Step($"open {page}");
        }

        Memory("every page opened");

        // History filter changes: time and allocations (all threads) until the new rows are in. Each filter must change
        // the rows (the synthetic history is Constructed only, so "Constructed" would show the same rows as "All").
        main.ShowPage("History");
        Pump();
        foreach (var filter in new[] { "Battlegrounds", "All" })
        {
            var rowsChanged = false;
            void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => rowsChanged |= e.PropertyName == nameof(MainViewModel.HistoryRows);
            vm.PropertyChanged += OnChanged;
            Step("(before filter)");
            vm.SetHistoryFilter.Execute(filter);
            var wait = Stopwatch.StartNew();
            while (!rowsChanged && wait.ElapsedMilliseconds < 10_000)
            {
                Pump(DispatcherPriority.Background);
            }

            Pump();
            Step($"history filter {filter}");
            vm.PropertyChanged -= OnChanged;
        }
        foreach (var page in new[] { "Battlegrounds", "Constructed" })
        {
            main.ShowPage(page);
            Pump();
            foreach (var tab in new[] { "TabHeroes", "TabComps", "TabTrinkets", "TabCards", "TabMine", "TabMetaTiers", "TabMetaDecks" })
            {
                if (main.FindName(tab) is ToggleButton { IsVisible: true } toggle)
                {
                    toggle.IsChecked = true;
                    Pump();
                }
            }
        }

        // Art decodes run in the background; give them time to land.
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 6000)
        {
            Pump(DispatcherPriority.Background);
        }

        Memory("every tab visited, art loaded");
        ImageCaches();
        Close(main, settings);
        main = null!;
        settle.Restart();
        while (settle.ElapsedMilliseconds < 2000)
        {
            Pump(DispatcherPriority.Background);
        }

        Memory("main window closed");
        GC.KeepAlive(app);
        GC.KeepAlive(catalog);
    }

    /// <summary>
    /// The overlay window with no Hearthstone window to follow (the app idling in the tray): CPU time of this process
    /// over 20 s. Run it while Hearthstone is closed.
    /// </summary>
    public static void OverlayIdle()
    {
        var (app, settings) = NewApplication();
        var overlay = new OverlayWindow(new OverlayViewModel(), settings);
        overlay.Show();
        Pump();
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 3000)
        {
            Pump(DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var operations = 0;
        Dispatcher.CurrentDispatcher.Hooks.OperationCompleted += (_, _) => operations++;
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        stop.Start();
        Dispatcher.PushFrame(frame);
        stop.Stop();
        process.Refresh();
        var used = process.TotalProcessorTime - cpu;
        Console.WriteLine($"[idle] overlay without a game window: CPU {used.TotalMilliseconds:N0} ms in 20 s ({used.TotalMilliseconds / 20:N1} ms/s), {operations} dispatcher operations");
        overlay.Close();
        GC.KeepAlive(app);
    }

    private static (Application App, AppSettings Settings) NewApplication()
    {
        // Relative pack URIs (Themes/Theme.xaml) resolve against the entry assembly; point them at the app's.
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(Hearthpulse.App.App).Assembly);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = AppSettings.Load();
        ThemeEngine.Apply(app, ThemePalette.Resolve(settings.Theme, settings.ThemeAccentHue, settings.ThemeBaseHue, settings.ThemeBaseTint));
        return (app, settings);
    }

    private static MainWindow OffScreen(MainWindow window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Width = 1400;
        window.Height = 900;
        window.ShowActivated = false;
        return window;
    }

    /// <summary>Closes for real: with "minimize to tray" on, Close() only hides the window.</summary>
    private static void Close(MainWindow window, AppSettings settings)
    {
        var tray = settings.MinimizeToTray;
        settings.MinimizeToTray = false;
        window.Close();
        settings.MinimizeToTray = tray;
    }

    private static void Pump(DispatcherPriority until = DispatcherPriority.ApplicationIdle)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), until);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Pixel bytes held by the in-memory image caches, per kind (a cropped image counts its source).</summary>
    private static void ImageCaches()
    {
        foreach (var type in new[] { typeof(CardArt), typeof(CardRender) })
        {
            if (type.GetField("Memory", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) is not SizedCache<System.Windows.Media.ImageSource> cache)
            {
                Console.WriteLine($"[art] {type.Name}: no Memory cache field");
                continue;
            }

            Console.WriteLine($"[art] {type.Name,-10} cache {Mb(cache.Size)} of {Mb(cache.Budget)} budget, {cache.Count} images");
            var groups = new SortedDictionary<string, (int Count, long Bytes, string Size)>(StringComparer.Ordinal);
            foreach (var (key, value, _) in cache.Snapshot())
            {
                var kind = key.Contains(':') ? key[..key.IndexOf(':')] : "render";
                long bytes = 0;
                var size = string.Empty;
                if (value is BitmapSource bitmap)
                {
                    var source = bitmap is CroppedBitmap { Source: BitmapSource whole } ? whole : bitmap;
                    bytes = (long)source.PixelWidth * source.PixelHeight * Math.Max(4, source.Format.BitsPerPixel / 8);
                    size = $"{source.PixelWidth}x{source.PixelHeight}{(source != bitmap ? " (cropped, source kept)" : string.Empty)}";
                }

                var g = groups.GetValueOrDefault(kind);
                groups[kind] = (g.Count + 1, g.Bytes + bytes, g.Size is { Length: > 0 } ? g.Size : size);
            }

            foreach (var (kind, g) in groups)
            {
                Console.WriteLine($"[art] {type.Name,-10} {kind,-20} {g.Count,4} images  {Mb(g.Bytes),9} pixels  e.g. {g.Size}");
            }
        }
    }
}
