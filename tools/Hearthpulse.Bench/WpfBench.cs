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
        var wpf = new OverlayViewModel();
        OverlayRun("WPF", wpf, typeof(OverlayViewModel), s => wpf.Update(s, cards, settings, null, null), cards, options);
        var avalonia = new Hearthpulse.UI.Overlay.OverlayViewModel();
        OverlayRun("Avalonia", avalonia, avalonia.GetType(), s => avalonia.Update(s, cards, settings, null, null), cards, options);
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

        var (app, settings) = NewApplication();
        Step("Application + settings + theme");
        var history = new HistoryStore(AppPaths.Database);
        Step("HistoryStore open");
        var main = OffScreen(new MainWindow(new MainViewModel(history, settings)));
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
        if (overlayAfter)
        {
            overlay.Show();
            Step("overlay.Show (after the first frame)");
        }
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
                    // HP_BENCH_TAB_RAM=1: memory after each tab, once its art has had time to load.
                    if (Environment.GetEnvironmentVariable("HP_BENCH_TAB_RAM") == "1")
                    {
                        var tabSettle = Stopwatch.StartNew();
                        while (tabSettle.ElapsedMilliseconds < 2000)
                        {
                            Pump(DispatcherPriority.Background);
                        }

                        Memory($"  {page}/{tab}");
                    }
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
    /// Decodes every portrait and deck tile in the data copy's image cache through CardArt, all asked for at once as a
    /// page does: time, bytes allocated, GCs (the large object heap drives gen2) and their pauses. Nothing is downloaded.
    /// </summary>
    public static void Art(Options options)
    {
        var (app, _) = NewApplication();
        foreach (var kind in new[] { ArtKind.Portrait, ArtKind.Tile })
        {
            var dir = Path.Combine(AppPaths.Images, kind.ToString().ToLowerInvariant());
            var ids = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).Select(Path.GetFileNameWithoutExtension).OfType<string>().Take(options.Games > 0 ? options.Games : 400).ToList() : [];
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var paused = GC.GetTotalPauseDuration();
            var sw = Stopwatch.StartNew();
            var images = Task.WhenAll(ids.Select(id => CardArt.GetAsync(kind, id))).GetAwaiter().GetResult();
            sw.Stop();
            var bytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[art] {kind,-9} {images.Count(i => i is not null),4} of {ids.Count,4} decoded in {sw.Elapsed.TotalMilliseconds,6:N0} ms  alloc {bytes / 1048576.0,6:N1} MB ({bytes / 1024.0 / Math.Max(1, ids.Count),5:N0} KB/image)  gen0 {GC.CollectionCount(0) - gen0} gen1 {GC.CollectionCount(1) - gen1} gen2 {GC.CollectionCount(2) - gen2}  GC pause {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:N0} ms"));
        }

        GC.KeepAlive(app);
    }

    /// <summary>
    /// How long each page, Settings tab and Battlegrounds/Constructed tab takes to open the first time and again (until
    /// the dispatcher is idle), with the bytes allocated, then a deck page and a match page. Same data as <see cref="Ram"/>.
    /// </summary>
    public static void Pages(Options options)
    {
        var n = options.Games > 0 ? options.Games : 3000;
        var (app, settings) = NewApplication();
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var api = new HearthpulseApi(http);
        var bgMeta = BattlegroundsMeta.LoadAsync(new HearthpulseData(api)).GetAwaiter().GetResult();
        var ctMeta = ConstructedMeta.LoadAsync(new HearthpulseData(api), cards, settings.MetaFormat, settings.MetaRank, settings.MetaPeriod, cards.Locale).GetAwaiter().GetResult();
        var history = new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-bench-ram-{n}.db"));
        if (history.GameCount() < n)
        {
            CoreBench.Fill(history, CoreBench.SeedGames(options.Logs, cards), n);
        }

        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        // Settings open on the tab saved last; start from General so every tab is measured on its own.
        var savedTab = settings.SettingsTab;
        settings.SettingsTab = "General";
        var vm = new MainViewModel(history, settings);
        var main = OffScreen(new MainWindow(vm));
        main.Show();
        Pump();
        vm.SetCards(cards);
        vm.SetMeta(bgMeta);
        vm.SetConstructedMeta(ctMeta);
        var refresh = vm.RefreshAsync();
        while (!refresh.IsCompleted)
        {
            Pump(DispatcherPriority.Background);
        }

        Pump();
        var sw = new Stopwatch();
        var elements = Elements(main);
        // The app saves and re-applies every setting on this event (App.OnSettingsChanged): opening a page must not raise it.
        var settingsChanged = 0;
        main.SettingsChanged += () => settingsChanged++;
        void Time(string what, Action action)
        {
            var allocated = GC.GetTotalAllocatedBytes();
            var mine = GC.GetAllocatedBytesForCurrentThread();
            var gen0 = GC.CollectionCount(0);
            var gen2 = GC.CollectionCount(2);
            var paused = GC.GetTotalPauseDuration();
            var changed = settingsChanged;
            sw.Restart();
            if (what.Contains(Environment.GetEnvironmentVariable("HP_BENCH_TRACE_STEP") ?? "\0", StringComparison.Ordinal))
            {
                TracedStep(action);
            }
            else
            {
                action();
                // Ready: everything above input priority is done (layout, render, bindings), so a click is handled now.
                Pump(DispatcherPriority.Input);
            }

            var elapsed = sw.Elapsed.TotalMilliseconds;
            // Then whatever the page still builds at background priority, and how long input waits meanwhile.
            var clock = Stopwatch.StartNew();
            var last = clock.Elapsed;
            var worst = TimeSpan.Zero;
            var input = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
            {
                worst = clock.Elapsed - last > worst ? clock.Elapsed - last : worst;
                last = clock.Elapsed;
            }, Dispatcher.CurrentDispatcher);
            input.Start();
            var lists = new List<System.Windows.Controls.Panel>();
            RowPanels(main, lists);
            while (lists.Any(RowsPending) && clock.ElapsedMilliseconds < 10_000)
            {
                Pump(DispatcherPriority.Background);
            }

            input.Stop();
            Pump();
            var built = sw.Elapsed.TotalMilliseconds;
            var now = Elements(main);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[pages] {what,-40} {elapsed,8:N0} ms ready, all built {built,6:N0} ms (input waited ≤ {worst.TotalMilliseconds,3:N0} ms)  alloc {(GC.GetTotalAllocatedBytes() - allocated) / 1048576.0,6:N1} MB (UI {(GC.GetAllocatedBytesForCurrentThread() - mine) / 1048576.0,5:N1})  gen0 {GC.CollectionCount(0) - gen0,2} gen2 {GC.CollectionCount(2) - gen2}  GC pause {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds,4:N0} ms  visuals {now,6:N0} ({now - elements:+#,0;-#,0;0})  SettingsChanged {settingsChanged - changed}  extent {PageExtent(main)}"));
            elements = now;
            SettleArt(what);
        }

        foreach (var round in new[] { "first", "again" })
        {
            foreach (var page in new[] { "Battlegrounds", "Constructed", "Arena", "History", "Settings", "Overview" })
            {
                Time($"{round} {page}", () => main.ShowPage(page));
            }
        }

        main.ShowPage("Settings");
        Pump();
        foreach (var round in new[] { "first", "again" })
        {
            foreach (var tab in new[] { "Overlay", "Tracker", "Account", "Data", "About", "General" })
            {
                Time($"{round} Settings/{tab}", () => main.ShowSettingsTab(tab));
            }
        }

        foreach (var (page, tabs) in new[] { ("Battlegrounds", new[] { "TabComps", "TabTrinkets", "TabCards", "TabMine", "TabHeroes" }), ("Constructed", new[] { "TabMetaTiers", "TabMetaDecks", "TabMine" }) })
        {
            main.ShowPage(page);
            Pump();
            foreach (var tab in tabs)
            {
                if (main.FindName(tab) is ToggleButton { IsVisible: true } toggle)
                {
                    Time($"{page}/{tab}", () => toggle.IsChecked = true);
                }
            }
        }

        // The longest lists scrolled to their end and back: the rows a page builds on the way, and its full height.
        foreach (var (page, tab, scroller) in new[] { ("Settings", "Tracker", "PageSettings"), ("Battlegrounds", "TabHeroes", "PageBattlegrounds"), ("Constructed", "TabMetaTiers", "PageConstructed") })
        {
            main.ShowPage(page);
            if (page == "Settings")
            {
                main.ShowSettingsTab(tab);
            }
            else if (main.FindName(tab) is ToggleButton toggle)
            {
                toggle.IsChecked = true;
            }

            Pump();
            // A moment on the page before scrolling, as a player would take: rows a list builds while idle land here.
            // The worst lateness of a 16 ms input-priority timer is how long a click or key would wait meanwhile.
            {
                var before = Elements(main);
                var clock = Stopwatch.StartNew();
                var last = clock.Elapsed;
                var worst = TimeSpan.Zero;
                var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) =>
                {
                    worst = clock.Elapsed - last > worst ? clock.Elapsed - last : worst;
                    last = clock.Elapsed;
                }, Dispatcher.CurrentDispatcher);
                timer.Start();
                // One frame for the whole wait: posting work in a loop would keep idle-priority work from ever running.
                var frame = new DispatcherFrame();
                var stop = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
                stop.Start();
                Dispatcher.PushFrame(frame);
                stop.Stop();
                timer.Stop();
                Console.WriteLine($"[pages] {page}/{tab} 2 s on the page: visuals {Elements(main) - before:+#,0;-#,0;0}, input waited at most {worst.TotalMilliseconds:N0} ms");
            }

            var scroll = (System.Windows.Controls.ScrollViewer)main.FindName(scroller);
            // Wheel notches (3 lines of 16 px each) through the whole page: the slowest one is what a stutter would be.
            var notches = new List<double>();
            var wheel = Stopwatch.StartNew();
            while (scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 1 && notches.Count < 400)
            {
                // A notch is ready once everything above input priority is done; then the 30 ms a spinning wheel
                // leaves between notches, in which background work (rows built ahead) may run.
                var step = Stopwatch.StartNew();
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 48);
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

            Console.WriteLine($"[pages] {page}/{tab} wheeled to the end: {notches.Count} notches in {wheel.Elapsed.TotalMilliseconds:N0} ms, {Spread(notches)}");
            scroll.ScrollToHome();
            Pump();
            Time($"{page}/{tab} scrolled to the end", scroll.ScrollToEnd);
            Time($"{page}/{tab} scrolled to the top", scroll.ScrollToHome);
        }

        main.ShowPage("Constructed");
        Pump();
        if (vm.Decks.FirstOrDefault() is { } deck)
        {
            Time($"deck page ({vm.Decks.Count} decks)", () =>
            {
                // The deck's statistics are built on a pool thread: wait for its card rows before the page counts as open.
                vm.OpenDeck.Execute(deck);
                var wait = Stopwatch.StartNew();
                while (vm.DeckPage.CardRows.Count == 0 && wait.ElapsedMilliseconds < 5000)
                {
                    Pump(DispatcherPriority.Background);
                }
            });
            Time("back to Constructed", () => vm.GoBack(main.CurrentPage));
        }

        main.ShowPage("History");
        Pump();
        if (vm.HistoryRows.FirstOrDefault() is { } game)
        {
            Time("match page", () =>
            {
                var open = vm.OpenMatchPageAsync(game.Key);
                while (!open.IsCompleted)
                {
                    Pump(DispatcherPriority.Background);
                }
            });
            Time("back to History", () => vm.GoBack(main.CurrentPage));
        }

        settings.SettingsTab = savedTab;
        Close(main, settings);
        GC.KeepAlive(app);
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
        var settings = AppSettings.Load();
        // As the app does before any window: the language decides every text (and a later switch rebuilds pages).
        Core.Localization.Loc.Instance.SetLanguage(settings.InterfaceLanguage());
        var theme = Stopwatch.StartNew();
        ThemeEngine.Apply(app, ThemePalette.Resolve(settings.Theme, settings.ThemeAccentHue, settings.ThemeBaseHue, settings.ThemeBaseTint));
        Console.WriteLine($"[start] theme \"{settings.Theme}\" applied in {theme.Elapsed.TotalMilliseconds:N0} ms");
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

    /// <summary>
    /// Waits for the card art a step asked for (downloads and background decodes) so it does not bleed into the next
    /// step, and reports what the burst cost the UI thread: the longest stall of a 16 ms timer, GCs and their pauses.
    /// </summary>
    private static void SettleArt(string what)
    {
        var pending = (System.Collections.ICollection)typeof(CardArt).GetField("Pending", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
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

    /// <summary>Content height of the scrolling page on screen: a list that builds fewer rows must keep it.</summary>
    private static string PageExtent(MainWindow main) =>
        new[] { "PageOverview", "PageBattlegrounds", "PageConstructed", "PageDeck", "PageArena", "PageMatch", "PageSettings" }
            .Select(main.FindName).OfType<System.Windows.Controls.ScrollViewer>().FirstOrDefault(p => p.IsVisible) is { } page
            ? page.ExtentHeight.ToString("N1", CultureInfo.InvariantCulture)
            : "-";

    /// <summary>Visuals in the window's tree (templates that were never applied add none).</summary>
    private static int Elements(System.Windows.Media.Visual root)
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
