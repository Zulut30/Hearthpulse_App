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
            // Past the start-up: catching up with the last log and the session's trim 20 s after it.
            watcher.Start();
            Run(TimeSpan.FromSeconds(40));
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

        // The replayed Battlegrounds fights leave their full combat simulation running on the thread pool: wait it out,
        // or it lands in whichever window comes next.
        Quiet();
        var history = new HistoryStore(db);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var vm = new MainViewModel(history, settings);
        var main = WpfBench.OffScreen(new MainWindow(vm));
        main.Show();
        WpfBench.Pump();
        vm.SetCards(cards);
        main.ShowPage("History");
        WpfBench.Pump();
        Memory("History page");
        var games = history.Recent(400).Where(g => g.Replay is not null).ToList();
        foreach (var game in new[] { games.FirstOrDefault(g => g.IsBattlegrounds), games.FirstOrDefault(g => !g.IsBattlegrounds) })
        {
            if (game is null)
            {
                Console.WriteLine("[match] no such game with a replay in the kept logs");
                continue;
            }

            Play(vm, main, game);
        }

        WpfBench.Close(main, settings);
        GC.KeepAlive(app);
    }

    /// <summary>One game's match page: opened twice, every step by hand, then played at x4.</summary>
    private static void Play(MainViewModel vm, MainWindow main, GameRecord game)
    {
        var file = Path.Combine(AppPaths.Replays, game.Replay!);
        Console.WriteLine($"[match] {game.Mode} game of {game.StartedAt:dd.MM HH:mm}, replay {new FileInfo(file).Length / 1024} KB");
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
                Memory($"{game.Mode} match page open");
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

        page.Faster.Execute(null);
        page.Faster.Execute(null);
        // 15 s of playback first: the runtime compiles the hot paths again in the background (tiered JIT) for about as
        // long, and that is not what playing costs.
        page.Frame = 0;
        page.TogglePlay.Execute(null);
        Run(TimeSpan.FromSeconds(15));
        if (page.Playing)
        {
            page.TogglePlay.Execute(null);
        }

        page.Frame = 0;
        WpfBench.Pump();
        var from = page.Frame;
        page.TogglePlay.Execute(null);
        // HP_BENCH_MATCH_TYPES=1: what the playback allocates, by type (all threads).
        using (var types = Environment.GetEnvironmentVariable("HP_BENCH_MATCH_TYPES") == "1" ? new AllocationTypes() : null)
        {
            Window($"{game.Mode} playing x{page.Speed}");
            types?.Report($"{game.Mode} playing");
        }

        Console.WriteLine($"[match] played {page.Frame - from} steps, still playing {page.Playing}");
        if (page.Playing)
        {
            page.TogglePlay.Execute(null);
        }

        Memory($"{game.Mode} after playback");
        page.Back.Execute(null);
        WpfBench.Pump();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(int access, bool inherit, int threadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, out IntPtr info, int size, IntPtr returned);

    /// <summary>The module a thread started in (wpfgfx: WPF's render thread; coreclr: a runtime or pool thread).</summary>
    private static string StartModule(Process process, int threadId)
    {
        var handle = OpenThread(0x0040 /* THREAD_QUERY_INFORMATION */, false, threadId);
        if (handle == IntPtr.Zero)
        {
            return "?";
        }

        try
        {
            // ThreadQuerySetWin32StartAddress
            if (NtQueryInformationThread(handle, 9, out var start, IntPtr.Size, IntPtr.Zero) != 0)
            {
                return "?";
            }

            foreach (ProcessModule module in process.Modules)
            {
                var from = module.BaseAddress.ToInt64();
                if (start.ToInt64() >= from && start.ToInt64() < from + module.ModuleMemorySize)
                {
                    return module.ModuleName;
                }
            }

            return "?";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>The OS id of the finalizer thread, read once by a finalizer.</summary>
    private sealed class FinalizerThread
    {
        private static int _id;

        public static int Id
        {
            get
            {
                if (_id == 0)
                {
                    Drop();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                return _id;
            }
        }

        ~FinalizerThread() => _id = GetCurrentThreadId();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Drop() => GC.KeepAlive(new FinalizerThread());
    }

    /// <summary>Waits (at most 2 min) for a second in which the process used under 20 ms of CPU.</summary>
    private static void Quiet()
    {
        using var process = Process.GetCurrentProcess();
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < TimeSpan.FromMinutes(2))
        {
            process.Refresh();
            var cpu = process.TotalProcessorTime;
            Thread.Sleep(1000);
            process.Refresh();
            if ((process.TotalProcessorTime - cpu).TotalMilliseconds < 20)
            {
                break;
            }
        }

        Console.WriteLine($"[match] background work done after {waited.Elapsed.TotalSeconds:N0} s");
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
        var threads = FrameBench.Threads(process);
        var uiCpu = FrameBench.UiCpuMs();
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
            $"[rest] {what,-26} CPU {used / Seconds,6:N2} ms/s (UI thread {(FrameBench.UiCpuMs() - uiCpu) / Seconds,5:N2}), alloc {(GC.GetTotalAllocatedBytes() - allocated) / 1024.0 / Seconds,7:N1} KB/s, "
            + $"GCs gen0 {GC.CollectionCount(0) - gen0} gen1 {GC.CollectionCount(1) - gen1} gen2 {GC.CollectionCount(2) - gen2} in {Seconds} s, {operations / (double)Seconds:N1} dispatcher operations/s"));

        // The busiest threads over the window (the UI thread among them): where CPU beyond the UI thread goes.
        var busiest = FrameBench.Threads(process)
            .Select(t => (t.Key, Ms: t.Value - threads.GetValueOrDefault(t.Key)))
            .OrderByDescending(t => t.Ms)
            .Take(4);
        var ui = GetCurrentThreadId();
        var finalizer = FinalizerThread.Id;
        Console.WriteLine($"[rest]   finalizer #{finalizer}, UI #{ui}");
        Console.WriteLine($"[rest]   busiest threads: {string.Join(", ", busiest.Select(t => string.Create(CultureInfo.InvariantCulture,
            $"#{t.Key} ({(t.Key == ui ? "UI" : t.Key == finalizer ? "finalizer" : StartModule(process, t.Key))}) {t.Ms / Seconds:N1} ms/s")))}");
    }
}
