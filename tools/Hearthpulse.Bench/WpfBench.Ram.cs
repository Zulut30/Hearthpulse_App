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
        using var siteHttp = Hearthpulse.Core.Battlegrounds.Site.BgSiteClient.CreateHttp();
        var bgSite = new Hearthpulse.Core.Battlegrounds.Site.BgSiteClient(siteHttp).LoadAsync().GetAwaiter().GetResult();
        Memory("+ Battlegrounds site lists");
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
        var vm = new MainViewModel(history, settings, Hearthpulse.Core.Hosting.AppData.FromAppPaths());
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
        vm.SetBgSiteStats(bgSite);
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
                if (Part(main, tab) is ToggleButton { IsVisible: true } toggle)
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

        // The tray (stage 9): before, the hidden window kept every page; now the app lets it go after a minute hidden,
        // asks MemoryTrimmer for a collection, and builds a new window on the same place when it is opened again. The
        // trim comes right after the dispatcher settles, with no measuring collection in between: that one would do the
        // first half of the trim's work (the closed window goes only after its finalizers ran).
        main.Hide();
        Pump();
        Memory("hidden to tray (held)");
        var place = main.Place;
        Close(main, settings);
        main = null!;
        settle.Restart();
        while (settle.ElapsedMilliseconds < 2000)
        {
            Pump(DispatcherPriority.Background);
        }

        var trimmer = new MemoryTrimmer();
        var clock = DateTime.UtcNow;
        trimmer.NoteActivityWhenWindowAway(clock, windowLetGo: true);
        trimmer.Tick(clock + MemoryTrimmer.MinInterval, busy: false);
        Memory("released + trim");

        var reopen = Stopwatch.StartNew();
        var again = new MainWindow(vm);
        again.MoveTo(place);
        again.ShowActivated = false;
        var rendered = false;
        again.ContentRendered += (_, _) => rendered = true;
        again.Show();
        while (!rendered && reopen.ElapsedMilliseconds < 10_000)
        {
            Pump(DispatcherPriority.Background);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[ram] reopened on {place.Page}/{place.BattlegroundsTab}/{place.ConstructedTab}: first frame in {reopen.Elapsed.TotalMilliseconds:N0} ms"));
        Pump();
        Memory("reopened");
        Close(again, settings);
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
}
