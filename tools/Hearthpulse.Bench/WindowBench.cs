using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
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
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>The WPF main window over time: at rest (shown, in the tray, let go) and the match page playing a replay.</summary>
internal static class WindowBench
{
    private const int Seconds = 30;

    /// <summary>
    /// The app at rest with Hearthstone closed: the main window on Overview, the overlay window and the log watcher running
    /// as in the app. Then hidden to the tray, then let go and trimmed (stage 9). Each state: <see cref="Seconds"/> s of
    /// process and UI-thread CPU, allocations and collections, then memory (after the count: it collects).
    /// </summary>
    public static void Rest(Options options)
    {
        var n = options.Games > 0 ? options.Games : 3000;
        var (app, settings) = WpfBench.NewApplication();
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
        var vm = new MainViewModel(history, settings);
        var main = WpfBench.OffScreen(new MainWindow(vm));
        main.Show();
        WpfBench.Pump();
        vm.SetCards(cards);
        vm.SetMeta(bgMeta);
        vm.SetConstructedMeta(ctMeta);
        var refresh = vm.RefreshAsync();
        while (!refresh.IsCompleted)
        {
            WpfBench.Pump(DispatcherPriority.Background);
        }

        var overlayVm = new OverlayViewModel();
        var overlay = new OverlayWindow(overlayVm, settings);
        overlay.Show();

        // The log watcher on the installed game's logs, snapshots applied on the UI thread as the app does.
        var install = HearthstoneInstall.FindInstallDirectory(settings.HearthstonePath);
        var db = Path.Combine(Path.GetTempPath(), $"hp-bench-{Guid.NewGuid():N}.db");
        var session = new GameSession(new HistoryStore(db), new Core.Memory.HearthstoneMemory()) { ReadsMemory = false, Cards = cards };
        var dispatcher = Dispatcher.CurrentDispatcher;
        session.SnapshotChanged += s => dispatcher.BeginInvoke(() => overlayVm.Update(s, cards, settings, null, null), DispatcherPriority.Background);
        using (var watcher = new Core.Logs.LogWatcher(session, () => install))
        {
            watcher.Start();
            Run(TimeSpan.FromSeconds(10));
            Window("shown on Overview");
            Memory("shown on Overview");

            main.Hide();
            WpfBench.Pump();
            Run(TimeSpan.FromSeconds(2));
            Window("hidden to the tray");
            Memory("hidden to the tray");

            WpfBench.Close(main, settings);
            main = null!;
            Run(TimeSpan.FromSeconds(2));
            var trimmer = new MemoryTrimmer();
            var clock = DateTime.UtcNow;
            trimmer.NoteActivityWhenWindowAway(clock, windowLetGo: true);
            trimmer.Tick(clock + MemoryTrimmer.MinInterval, busy: false);
            Window("let go and trimmed");
            Memory("let go and trimmed");
        }

        overlay.Close();
        LogReplay.Delete(db);
        GC.KeepAlive(app);
        GC.KeepAlive(vm);
    }

    /// <summary>
    /// The match page of a Battlegrounds game with its step-by-step replay (format 4). The games of the kept logs are
    /// recorded into a scratch history (their replay files land in the data copy), the newest Battlegrounds game is
    /// opened (time and allocations), every step is drawn once by hand, then the replay plays at x4 for
    /// <see cref="Seconds"/> s: process and UI-thread CPU, allocations, collections.
    /// </summary>
    public static void Match(Options options)
    {
        var (app, settings) = WpfBench.NewApplication();
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var db = Path.Combine(Path.GetTempPath(), $"hp-bench-match-{Guid.NewGuid():N}.db");
        foreach (var (dir, path, start) in LogReplay.Logs(options.Logs))
        {
            var (session, setClock, _) = LogReplay.NewSession(cards, db);
            LogReplay.Run(session, setClock, dir, path, start);
        }

        var history = new HistoryStore(db);
        if (history.Recent(400).FirstOrDefault(g => g.IsBattlegrounds && g.Replay is not null) is not { } game)
        {
            Console.WriteLine("[match] no Battlegrounds game with a replay in the kept logs");
            return;
        }

        var file = Path.Combine(AppPaths.Replays, game.Replay!);
        Console.WriteLine($"[match] {game.Mode} game of {game.StartedAt:dd.MM HH:mm}, replay {new FileInfo(file).Length / 1024} KB");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var vm = new MainViewModel(history, settings);
        var main = WpfBench.OffScreen(new MainWindow(vm));
        main.Show();
        WpfBench.Pump();
        vm.SetCards(cards);
        main.ShowPage("History");
        WpfBench.Pump();
        Memory("History page");

        for (var round = 0; round < 2; round++)
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var gen0 = GC.CollectionCount(0);
            var gen2 = GC.CollectionCount(2);
            var elements = WpfBench.Elements(main);
            var sw = Stopwatch.StartNew();
            var open = vm.OpenMatchPageAsync(game.Key);
            while (!open.IsCompleted)
            {
                WpfBench.Pump(DispatcherPriority.Background);
            }

            WpfBench.Pump();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[match] open r{round}: {sw.Elapsed.TotalMilliseconds:N0} ms, alloc {Mb(GC.GetTotalAllocatedBytes(precise: true) - allocated)}, gen0 {GC.CollectionCount(0) - gen0} gen2 {GC.CollectionCount(2) - gen2}, {vm.MatchPage.FrameMax + 1} steps, visuals {WpfBench.Elements(main) - elements:+#,0;-#,0;0}"));
            if (round == 0)
            {
                Memory("match page open");
                vm.MatchPage.Back.Execute(null);
                WpfBench.Pump();
            }
        }

        // Every step once, laid out: what a step costs the UI thread whatever the speed.
        var page = vm.MatchPage;
        var steps = new List<double>();
        var stepAlloc = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame <= page.FrameMax; frame++)
        {
            var t = Stopwatch.GetTimestamp();
            page.Frame = frame;
            WpfBench.Pump(DispatcherPriority.Input);
            steps.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[match] {steps.Count} steps by hand: {Spread(steps)}; alloc {(GC.GetAllocatedBytesForCurrentThread() - stepAlloc) / 1024.0 / Math.Max(1, steps.Count):N1} KB/step"));

        page.Frame = 0;
        WpfBench.Pump();
        page.Faster.Execute(null);
        page.Faster.Execute(null);
        var from = page.Frame;
        page.TogglePlay.Execute(null);
        Window($"playing x{page.Speed}");
        Console.WriteLine($"[match] played {page.Frame - from} steps, still playing {page.Playing}");
        if (page.Playing)
        {
            page.TogglePlay.Execute(null);
        }

        Memory("after playback");
        WpfBench.Close(main, settings);
        GC.KeepAlive(app);
    }

    /// <summary>Runs the dispatcher (timers, rendering) for <paramref name="time"/>.</summary>
    private static void Run(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(time, DispatcherPriority.Normal, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        stop.Start();
        Dispatcher.PushFrame(frame);
        stop.Stop();
    }

    /// <summary><see cref="Seconds"/> s of the dispatcher: process and UI-thread CPU, allocations, collections, operations.</summary>
    private static void Window(string what)
    {
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var ui = FrameBench.UiCpuMs();
        var allocated = GC.GetTotalAllocatedBytes();
        var (gen0, gen1, gen2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var operations = 0;
        void Count(object? sender, DispatcherHookEventArgs e) => operations++;
        Dispatcher.CurrentDispatcher.Hooks.OperationCompleted += Count;
        Run(TimeSpan.FromSeconds(Seconds));
        Dispatcher.CurrentDispatcher.Hooks.OperationCompleted -= Count;
        process.Refresh();
        var used = (process.TotalProcessorTime - cpu).TotalMilliseconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[rest] {what,-26} CPU {used / Seconds,6:N2} ms/s (UI thread {(FrameBench.UiCpuMs() - ui) / Seconds,5:N2}), alloc {(GC.GetTotalAllocatedBytes() - allocated) / 1024.0 / Seconds,7:N1} KB/s, "
            + $"GCs gen0 {GC.CollectionCount(0) - gen0} gen1 {GC.CollectionCount(1) - gen1} gen2 {GC.CollectionCount(2) - gen2} in {Seconds} s, {operations / (double)Seconds:N1} dispatcher operations/s"));
    }
}
