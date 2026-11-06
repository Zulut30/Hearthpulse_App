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
using Hearthpulse.Core.Hearthstone;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>
/// Benchmarks of the WPF app's view models and windows. A plain <see cref="Application"/> carries the theme:
/// Hearthpulse.App.App would run its OnStartup (single-instance mutex, tray, log watcher) on the first frame.
/// Windows are placed off-screen.
/// </summary>
internal static partial class WpfBench
{
    public static void Overlay(Options options)
    {
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var settings = AppSettings.Load();
        var bg = BgPanels(settings);
        var wpf = new OverlayViewModel();
        OverlayRun("WPF", wpf, typeof(OverlayViewModel), s =>
        {
            wpf.Update(s, cards, settings, null, null);
            if (bg)
            {
                wpf.ArrangeLeaderboard(1920, 1080, settings);
            }
        }, cards, options);
        var avalonia = new Hearthpulse.UI.Overlay.OverlayViewModel();
        OverlayRun("Avalonia", avalonia, avalonia.GetType(), s =>
        {
            avalonia.Update(s, cards, settings, null, null);
            if (bg)
            {
                avalonia.ArrangeLeaderboard(1920, 1080, settings);
            }
        }, cards, options);
    }

    /// <summary>
    /// HP_BENCH_BG_PANELS=1: the leaderboard badges and the lobby panel on, and the leaderboard arranged after each
    /// snapshot as every overlay frame does (a 1080-unit surface). Off, the numbers stay comparable with stages 2-9.
    /// </summary>
    internal static bool BgPanels(AppSettings settings)
    {
        if (Environment.GetEnvironmentVariable("HP_BENCH_BG_PANELS") != "1")
        {
            return false;
        }

        settings.ShowBgLeaderboardBadges = true;
        settings.ShowBgLobbyPanel = true;
        settings.ShowLobbyPanel = true;
        return true;
    }

    /// <summary>
    /// Replays every kept log into one overlay view model: time and bytes allocated per Update, PropertyChanged per
    /// property and how many of them carried the same content in a new object.
    /// </summary>
    private static void OverlayRun(string ui, System.ComponentModel.INotifyPropertyChanged overlay, Type type, Action<Core.Tracking.LiveSnapshot> update, CardDb cards, Options options)
    {
        var json = new JsonSerializerOptions { MaxDepth = 16, ReferenceHandler = ReferenceHandler.IgnoreCycles };
        var last = new Dictionary<string, string?>();
        var changed = new Dictionary<string, (int Real, int Same, int Unknown)>();
        var counting = false;
        overlay.PropertyChanged += (_, e) =>
        {
            if (!counting)
            {
                return;
            }

            var name = e.PropertyName ?? "*";
            string? now;
            try
            {
                now = JsonSerializer.Serialize(type.GetProperty(name)?.GetValue(overlay), json);
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

        // Collect the snapshots first: the timed pass then measures Update alone, without the replay or the counting.
        var snapshots = new List<Core.Tracking.LiveSnapshot>();
        foreach (var (dir, path, start) in LogReplay.Logs(options.Logs))
        {
            var (session, setClock, db) = LogReplay.NewSession(cards);
            session.SnapshotChanged += snapshots.Add;
            LogReplay.Run(session, setClock, dir, path, start);
            LogReplay.Delete(db);
        }

        counting = true;
        foreach (var s in snapshots)
        {
            update(s);
        }

        counting = false;
        for (var round = 0; round < Math.Max(1, options.Rounds); round++)
        {
            var times = new List<double>(snapshots.Count);
            long bytes = 0;
            var gen0 = GC.CollectionCount(0);
            foreach (var s in snapshots)
            {
                var a = GC.GetAllocatedBytesForCurrentThread();
                var t = Stopwatch.GetTimestamp();
                update(s);
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                bytes += GC.GetAllocatedBytesForCurrentThread() - a;
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[overlay {ui}] {times.Count:N0} snapshots; Update {Spread(times)}; alloc {bytes / (double)Math.Max(1, times.Count) / 1024:N1} KB/snapshot; gen0 {GC.CollectionCount(0) - gen0}"));
        }

        foreach (var (name, c) in changed.OrderByDescending(x => x.Value.Real + x.Value.Same + x.Value.Unknown).Take(12))
        {
            Console.WriteLine($"[overlay {ui}] {name,-26} real {c.Real,6:N0}  same content {c.Same,6:N0}  unknown {c.Unknown,4:N0}");
        }

        var total = changed.Values.Sum(c => c.Real + c.Same + c.Unknown);
        var same = changed.Values.Sum(c => c.Same);
        Console.WriteLine($"[overlay {ui}] PropertyChanged {total:N0} ({(double)total / Math.Max(1, snapshots.Count):N1}/snapshot), same content {same:N0} ({100.0 * same / Math.Max(1, total):N0}%)");
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

        var fromMain = Stopwatch.StartNew();
        var (app, settings) = NewApplication();
        Step("Application + settings + theme");
        // The app's order since stage 6: the game and the cards are looked up while the windows are built
        // (HP_BENCH_SERIAL=1: the order before, all of it after the main window's first frame).
        var serial = Environment.GetEnvironmentVariable("HP_BENCH_SERIAL") == "1";
        using var http = Http();
        var prefetch = serial ? null : StartupPrefetch.Start(settings, http);
        var history = new HistoryStore(AppPaths.Database);
        Step("HistoryStore open");
        if (Environment.GetEnvironmentVariable("HP_BENCH_PAGE_COST") == "1")
        {
            // Each page's view built on its own before the window (cold: its BAML, types and styles load here).
            foreach (var (name, make) in new (string, Func<object>)[]
            {
                ("OverviewPage", () => new Hearthpulse.App.Main.Pages.OverviewPage()),
                ("BattlegroundsPage", () => new Hearthpulse.App.Main.Pages.BattlegroundsPage()),
                ("ConstructedPageView", () => new Hearthpulse.App.Main.Pages.ConstructedPageView()),
                ("HistoryPage", () => new Hearthpulse.App.Main.Pages.HistoryPage()),
                ("DeckDetailsView", () => new Hearthpulse.App.Main.Decks.DeckDetailsView()),
                ("MatchView", () => new Hearthpulse.App.Main.Match.MatchView()),
                ("ArenaStatsView", () => new Hearthpulse.App.Main.Stats.ArenaStatsView()),
                ("BrawlView", () => new Hearthpulse.App.Main.Stats.BrawlView()),
                ("GeneralSettings", () => new Hearthpulse.App.Main.SettingsTabs.GeneralSettings()),
                ("OverlaySettings", () => new Hearthpulse.App.Main.SettingsTabs.OverlaySettings()),
                ("TrackerSettings", () => new Hearthpulse.App.Main.SettingsTabs.TrackerSettings()),
                ("StreamSettings", () => new Hearthpulse.App.Main.SettingsTabs.StreamSettings()),
                ("AccountSettings", () => new Hearthpulse.App.Main.SettingsTabs.AccountSettings()),
                ("DataSettings", () => new Hearthpulse.App.Main.SettingsTabs.DataSettings()),
                ("AboutSettings", () => new Hearthpulse.App.Main.SettingsTabs.AboutSettings()),
                ("SetupWizard", () => new Hearthpulse.App.Main.Setup.SetupWizard()),
            })
            {
                GC.KeepAlive(make());
                Step($"  new {name}");
            }
        }
        var main = OffScreen(new MainWindow(new MainViewModel(history, settings, Hearthpulse.Core.Hosting.AppData.FromAppPaths())));
        Step("new MainWindow");
        var overlay = new OverlayWindow(new OverlayViewModel(), settings);
        Step("new OverlayWindow");
        // The app's order: the main window first, the overlay after its first frame (HP_BENCH_OVERLAY_FIRST=1: the order
        // before stage 4, the overlay first).
        var overlayAfter = Environment.GetEnvironmentVariable("HP_BENCH_OVERLAY_FIRST") != "1";
        if (!overlayAfter)
        {
            overlay.Show();
            Step("overlay.Show");
        }

        var frame = new DispatcherFrame();
        main.ContentRendered += (_, _) => frame.Continue = false;
        main.Show();
        Dispatcher.PushFrame(frame);
        Step("Show -> ContentRendered");
        Console.WriteLine($"[start] Main -> first frame of the main window   {fromMain.Elapsed.TotalMilliseconds,8:N0} ms, GCs gen0 {GC.CollectionCount(0)} gen1 {GC.CollectionCount(1)} gen2 {GC.CollectionCount(2)}, paused {GC.GetTotalPauseDuration().TotalMilliseconds:N0} ms");
        if (overlayAfter)
        {
            overlay.Show();
            Step("overlay.Show (after the first frame)");
        }

        // What InitializeAsync waits for before the log watcher starts (the first overlay values).
        string? install;
        string client;
        if (prefetch is null)
        {
            install = HearthstoneInstall.FindInstallDirectory(settings.HearthstonePath);
            client = HearthstoneInstall.DetectLocale(install);
        }
        else
        {
            (install, client) = prefetch.Game.GetAwaiter().GetResult();
        }

        settings.ClientLocale = client;
        var locale = settings.CardLocale();
        var cards = prefetch is null
            ? CardDb.LoadAsync(locale, http).GetAwaiter().GetResult()
            : prefetch.CardsAsync(locale).GetAwaiter().GetResult();
        Step($"{(serial ? "game + cards, serial" : "game + cards, prefetched")} ({locale})");
        Console.WriteLine($"[start] Main -> log watcher can start          {fromMain.Elapsed.TotalMilliseconds,8:N0} ms ({cards.Count} cards, install {install ?? "none"})");
        Memory("after start-up");
        Close(main, settings);
        overlay.Close();
        GC.KeepAlive(app);
        GC.KeepAlive(cards);
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

    internal static (Application App, AppSettings Settings) NewApplication()
    {
        // Relative pack URIs (Themes/Theme.xaml) resolve against the entry assembly; point them at the app's.
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, typeof(Hearthpulse.App.App).Assembly);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var load = Stopwatch.StartNew();
        var settings = AppSettings.Load();
        Console.WriteLine($"[start] AppSettings.Load in {load.Elapsed.TotalMilliseconds:N0} ms");
        // As the app does before any window: the language decides every text (and a later switch rebuilds pages).
        Core.Localization.Loc.Instance.SetLanguage(settings.InterfaceLanguage());
        var theme = Stopwatch.StartNew();
        ThemeEngine.Apply(app, ThemePalette.Resolve(settings.Theme, settings.ThemeAccentHue, settings.ThemeBaseHue, settings.ThemeBaseTint));
        Console.WriteLine($"[start] theme \"{settings.Theme}\" applied in {theme.Elapsed.TotalMilliseconds:N0} ms");
        return (app, settings);
    }

    internal static MainWindow OffScreen(MainWindow window)
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
    internal static void Close(MainWindow window, AppSettings settings)
    {
        var tray = settings.MinimizeToTray;
        settings.MinimizeToTray = false;
        window.Close();
        settings.MinimizeToTray = tray;
    }

    /// <summary>
    /// Waits for the card art a step asked for (downloads and background decodes) so it does not bleed into the next
    /// step, and reports what the burst cost the UI thread: the longest stall of a 16 ms timer, GCs and their pauses.
    /// </summary>
    private static void SettleArt(string what)
    {
        // Loads in flight only: a finished task that stayed in the dictionary (fixed in CardArt.GetAsync) is not waited for.
        var tasks = (System.Collections.IDictionary)typeof(CardArt).GetField("Pending", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var pending = new InFlight(tasks);
        if (pending.Count == 0)
        {
            return;
        }

        var gen0 = GC.CollectionCount(0);
        var gen2 = GC.CollectionCount(2);
        var paused = GC.GetTotalPauseDuration();
        var allocated = GC.GetTotalAllocatedBytes();
        var sw = Stopwatch.StartNew();
        var last = sw.Elapsed;
        var worst = TimeSpan.Zero;
        var ticks = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
        {
            var at = sw.Elapsed;
            worst = at - last > worst ? at - last : worst;
            last = at;
            ticks++;
        }, Dispatcher.CurrentDispatcher);
        timer.Start();
        var count = pending.Count;
        while ((pending.Count > 0 || sw.ElapsedMilliseconds < 200) && sw.ElapsedMilliseconds < 15_000)
        {
            Pump(DispatcherPriority.Background);
            count = Math.Max(count, pending.Count);
        }

        Pump();
        timer.Stop();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[art]   {what,-40} {sw.Elapsed.TotalMilliseconds,8:N0} ms to load {count} images  alloc {(GC.GetTotalAllocatedBytes() - allocated) / 1048576.0,6:N1} MB  gen0 {GC.CollectionCount(0) - gen0} gen2 {GC.CollectionCount(2) - gen2}  GC pause {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:N0} ms  UI stall max {worst.TotalMilliseconds:N0} ms"));
    }

    /// <summary>The image loads of CardArt.Pending that have not finished.</summary>
    private sealed class InFlight(System.Collections.IDictionary tasks)
    {
        public int Count => tasks.Values.Cast<Task>().ToList().Count(t => !t.IsCompleted);
    }

    /// <summary>A step named by HP_BENCH_TRACE_STEP: its own frame, so a trace can be cut to it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void TracedStep(Action action)
    {
        action();
        Pump();
    }

    /// <summary>
    /// Visible list panels that build their rows later (PageRowsPanel, found by name: the bench also builds against
    /// commits without it).
    /// </summary>
    private static void RowPanels(System.Windows.Media.Visual root, List<System.Windows.Controls.Panel> found)
    {
        if (root is System.Windows.Controls.Panel { IsVisible: true } panel && panel.GetType().Name == "PageRowsPanel")
        {
            found.Add(panel);
        }

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(root, i) is System.Windows.Media.Visual child)
            {
                RowPanels(child, found);
            }
        }
    }

    /// <summary>True while the panel still has rows queued to build around what the page shows.</summary>
    private static bool RowsPending(System.Windows.Controls.Panel panel) =>
        panel.GetType().GetField("_aheadQueued", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(panel) is true;

    /// <summary>
    /// A named part of the window, also one inside a page or settings tab control (Main/Pages, Main/SettingsTabs):
    /// those keep their own namescope, where the window's FindName does not look.
    /// </summary>
    /// <summary>
    /// The History list (the replay library's virtualised ListBox) wheeled 48 px a notch to the end and back, 30 ms
    /// between notches: the time each notch takes to be ready, and how many miss a 60 Hz frame (16.7 ms).
    /// </summary>
    private static void HistoryWheel(MainWindow main)
    {
        if (Part(main, "PageHistory") is not System.Windows.Media.Visual page || Descendant<System.Windows.Controls.ListBox>(page) is not { } list
            || Descendant<System.Windows.Controls.ScrollViewer>(list) is not { } scroll)
        {
            Console.WriteLine("[pages] History wheel: no list");
            return;
        }

        foreach (var direction in new[] { "down", "up" })
        {
            var notches = new List<double>();
            var wheel = Stopwatch.StartNew();
            bool More() => direction == "down" ? scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 1 : scroll.VerticalOffset > 0;
            while (More() && notches.Count < 2000)
            {
                var step = Stopwatch.StartNew();
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + (direction == "down" ? 48 : -48));
                Pump(DispatcherPriority.Input);
                notches.Add(step.Elapsed.TotalMilliseconds);
                var gap = new DispatcherFrame();
                var resume = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Normal, (sender, _) =>
                {
                    ((DispatcherTimer)sender!).Stop();
                    gap.Continue = false;
                }, Dispatcher.CurrentDispatcher);
                resume.Start();
                Dispatcher.PushFrame(gap);
            }

            var dropped = notches.Count(n => n > 16.7);
            Console.WriteLine($"[pages] History wheel {direction}: {list.Items.Count} games, {notches.Count} notches in {wheel.Elapsed.TotalMilliseconds:N0} ms, {Spread(notches)}, over 16.7 ms {dropped}, visuals {Elements(main):N0}");
        }
    }

    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static DependencyObject? Part(MainWindow main, string name) => LogicalTreeHelper.FindLogicalNode(main, name);

    /// <summary>A page's scrolling area: a page control holds its ScrollViewer, the other pages are one.</summary>
    private static System.Windows.Controls.ScrollViewer? Scroller(MainWindow main, string name) => Part(main, name) switch
    {
        System.Windows.Controls.ScrollViewer scroll => scroll,
        System.Windows.Controls.UserControl { Content: System.Windows.Controls.ScrollViewer scroll } => scroll,
        _ => null,
    };

    /// <summary>Content height of the scrolling page on screen: a list that builds fewer rows must keep it.</summary>
    private static string PageExtent(MainWindow main) =>
        new[] { "PageOverview", "PageBattlegrounds", "PageConstructed", "PageDeck", "PageArena", "PageMatch", "PageSettings" }
            .Select(name => Scroller(main, name)).FirstOrDefault(p => p is { IsVisible: true }) is { } page
            ? page.ExtentHeight.ToString("N1", CultureInfo.InvariantCulture)
            : "-";

    /// <summary>Visuals in the window's tree (templates that were never applied add none).</summary>
    internal static int Elements(System.Windows.Media.Visual root)
    {
        var count = 1;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (System.Windows.Media.VisualTreeHelper.GetChild(root, i) is System.Windows.Media.Visual child)
            {
                count += Elements(child);
            }
        }

        return count;
    }

    internal static void Pump(DispatcherPriority until = DispatcherPriority.ApplicationIdle)
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), until);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Pixel bytes held by the in-memory image caches, per kind (a cropped image counts its source).</summary>
    internal static void ImageCaches()
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
