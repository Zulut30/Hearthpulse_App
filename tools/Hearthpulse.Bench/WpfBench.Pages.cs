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

internal static partial class WpfBench
{
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
        // The tier tabs' lists, from the data folder's cache while it is fresh (the site's service otherwise).
        using var siteHttp = Hearthpulse.Core.Battlegrounds.Site.BgSiteClient.CreateHttp();
        var bgSite = new Hearthpulse.Core.Battlegrounds.Site.BgSiteClient(siteHttp).LoadAsync().GetAwaiter().GetResult();
        Console.WriteLine($"[pages] site lists: heroes {bgSite?.Heroes?.Entries.Count ?? 0}, duos {bgSite?.HeroesDuos?.Entries.Count ?? 0}, minions {bgSite?.Minions?.Entries.Count ?? 0}, trinkets {bgSite?.Trinkets?.Entries.Count ?? 0}, comps {bgSite?.Comps.Count ?? 0}");
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
        var vm = new MainViewModel(history, settings, Hearthpulse.Core.Hosting.AppData.FromAppPaths());
        var main = OffScreen(new MainWindow(vm));
        main.Show();
        Pump();
        vm.SetCards(cards);
        vm.SetMeta(bgMeta);
        vm.SetBgSiteStats(bgSite);
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
            if (Environment.GetEnvironmentVariable("HP_BENCH_VISUALS") is { Length: > 0 } dump && what.Contains(dump, StringComparison.Ordinal))
            {
                VisualsByBlock(main);
            }

            SettleArt(what);
        }

        foreach (var round in new[] { "first", "again" })
        {
            foreach (var page in new[] { "Battlegrounds", "Constructed", "Arena", "Brawl", "History", "Settings", "Overview" })
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
                if (Part(main, tab) is ToggleButton { IsVisible: true } toggle)
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
            else if (Part(main, tab) is ToggleButton toggle)
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

            var scroll = Scroller(main, scroller) ?? throw new InvalidOperationException($"No scrolling page {scroller}");
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
        HistoryWheel(main);
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
    /// <summary>HP_BENCH_VISUALS=step: the shown visuals counted by the nearest named element above them, and by type.</summary>
    internal static void VisualsByBlock(System.Windows.Media.Visual root)
    {
        var blocks = new Dictionary<string, int>(StringComparer.Ordinal);
        var types = new Dictionary<string, int>(StringComparer.Ordinal);
        void Walk(DependencyObject node, string block)
        {
            if (node is UIElement { IsVisible: false })
            {
                return;
            }

            if (node is FrameworkElement { Name.Length: > 0 } named && named.TemplatedParent is null)
            {
                block = named.Name;
            }

            blocks[block] = blocks.GetValueOrDefault(block) + 1;
            var type = node.GetType().Name;
            types[type] = types.GetValueOrDefault(type) + 1;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i), block);
            }
        }

        Walk(root, "(window)");
        foreach (var (name, count) in blocks.OrderByDescending(b => b.Value).Take(25))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[visuals] block {name,-40} {count,6:N0}"));
        }

        foreach (var (name, count) in types.OrderByDescending(b => b.Value).Take(20))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[visuals] type  {name,-40} {count,6:N0}"));
        }
    }
}
