using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Bench;

/// <summary>
/// What the shown WPF overlay costs the machine, not just the UI thread: the window is transparent (layered), so every
/// frame is drawn and handed to Windows by WPF's render thread. The real <see cref="OverlayWindow"/> is shown off-screen
/// at 1920x1080 and 3840x2160 (the owner plays at 4K) and driven like a game: idle, the kept logs' snapshots at 10/s,
/// a small change 30 times a second (a hover, a timer), and the overlay being shown again when the game comes to the
/// front. Process CPU minus the UI thread's is the render side.
/// </summary>
internal static class FrameBench
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    internal static double UiCpuMs()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user);
        return (kernel + user) / 10_000.0;
    }

    public static void Run(Options options)
    {
        var (app, settings) = WpfBench.NewApplication();
        using var http = Measure.Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var (snapshot, all) = TickBench.MidGame(cards, options);
        if (snapshot is null)
        {
            Console.WriteLine("[frames] no mid-game Constructed snapshot in the kept logs");
            return;
        }

        // A stretch of the replay with the overlay in game: the snapshots a match would show at 10/s.
        var game = all.Where(s => s is { InGame: true }).ToList();
        var sizes = Environment.GetEnvironmentVariable("HP_BENCH_SIZES") is { Length: > 0 } list
            ? list.Split(',').Select(s => s.Split('x')).Select(p => (int.Parse(p[0], CultureInfo.InvariantCulture), int.Parse(p[1], CultureInfo.InvariantCulture))).ToArray()
            : [(1920, 1080), (3840, 2160)];
        foreach (var (width, height) in sizes)
        {
            for (var round = 0; round < Math.Max(1, options.Rounds); round++)
            {
                Scene(settings, cards, snapshot, game, width, height);
            }
        }

        GC.KeepAlive(app);
    }

    private static void Scene(Core.AppSettings settings, CardDb cards, LiveSnapshot snapshot, List<LiveSnapshot> game, int width, int height)
    {
        var vm = new OverlayViewModel();
        vm.Update(snapshot, cards, settings, null, null);
        var window = new OverlayWindow(vm, settings)
        {
            Offscreen = true,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000 - width,
            Top = -20000,
            Width = width,
            Height = height,
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
        type.GetField("_dpi", flags)!.SetValue(window, 1.0);
        scaleTransform.ScaleX = scaleTransform.ScaleY = scale;
        surface.Width = width / scale;
        surface.Height = height / scale;
        type.GetField("_layoutSize", flags)!.SetValue(window, new Size(surface.Width, surface.Height));
        type.GetMethod("ApplyLayout", flags)!.Invoke(window, null);
        var target = ((HwndSource)PresentationSource.FromVisual(window)!).CompositionTarget!;
        if (Environment.GetEnvironmentVariable("HP_BENCH_SOFTWARE") == "1")
        {
            target.RenderMode = RenderMode.SoftwareOnly;
        }

        if (Environment.GetEnvironmentVariable("HP_BENCH_ONSCREEN") == "1")
        {
            // On the primary screen so Windows really composes it, nearly invisible and click-through (WS_EX_TRANSPARENT).
            window.Opacity = 0.02;
            window.Left = 0;
            window.Top = 0;
        }

        Run(TimeSpan.FromSeconds(2), null);
        var label = $"{width}x{height} {target.RenderMode}{(window.Left == 0 ? " on screen" : string.Empty)}";

        Report(label, "idle", 3, null);

        var next = 0;
        Report(label, "snapshots 10/s", 6, new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            vm.Update(game[next++ % game.Count], cards, settings, null, null);
        }, Dispatcher.CurrentDispatcher));

        // One small element changes 30 times a second; the rest of the overlay is unchanged.
        var flip = false;
        var turnPanel = surface.Children.OfType<MovablePanel>().First(p => p.IsVisible);
        Report(label, "small change 30/s", 4, new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Background, (_, _) =>
        {
            flip = !flip;
            turnPanel.Opacity = flip ? 0.98 : 1;
        }, Dispatcher.CurrentDispatcher));
        turnPanel.Opacity = 1;

        // The game comes back to the front: the overlay root goes from hidden to shown (OverlayWindow.Tick).
        var shows = new List<double>();
        var showUi = new List<double>();
        using var process = Process.GetCurrentProcess();
        for (var i = 0; i < 6; i++)
        {
            root.Visibility = Visibility.Hidden;
            Run(TimeSpan.FromMilliseconds(300), null);
            process.Refresh();
            var cpu = process.TotalProcessorTime.TotalMilliseconds;
            var ui = UiCpuMs();
            root.Visibility = Visibility.Visible;
            Run(TimeSpan.FromMilliseconds(400), null);
            process.Refresh();
            if (i > 0)
            {
                shows.Add(process.TotalProcessorTime.TotalMilliseconds - cpu);
                showUi.Add(UiCpuMs() - ui);
            }
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[frames] {label,-28} show again         process CPU {shows.Average(),7:N1} ms (UI thread {showUi.Average():N1} ms) per show"));

        window.Close();
        Run(TimeSpan.FromMilliseconds(200), null);
    }

    /// <summary>
    /// Process and UI-thread CPU over <paramref name="seconds"/>, and the busiest other thread (WPF's render thread
    /// when the overlay draws; image decoding right after start).
    /// </summary>
    private static void Report(string label, string what, int seconds, DispatcherTimer? driver)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpu = process.TotalProcessorTime.TotalMilliseconds;
        var threads = Threads(process);
        var ui = UiCpuMs();
        Run(TimeSpan.FromSeconds(seconds), driver);
        var uiPerSecond = (UiCpuMs() - ui) / seconds;
        process.Refresh();
        var total = (process.TotalProcessorTime.TotalMilliseconds - cpu) / seconds;
        var busiest = Threads(process)
            .Select(t => t.Value - threads.GetValueOrDefault(t.Key))
            .OrderByDescending(v => v)
            .ToList();
        // The UI thread is among them; the largest that is not it, roughly.
        var other = busiest.FirstOrDefault(v => Math.Abs((v / seconds) - uiPerSecond) > 1) / seconds;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[frames] {label,-32} {what,-18} process CPU {total,7:N1} ms/s, UI thread {uiPerSecond,6:N1} ms/s, busiest other thread {other,6:N1} ms/s"));
    }

    private static Dictionary<int, double> Threads(Process process)
    {
        var times = new Dictionary<int, double>();
        foreach (ProcessThread thread in process.Threads)
        {
            try
            {
                times[thread.Id] = thread.TotalProcessorTime.TotalMilliseconds;
            }
            catch (InvalidOperationException)
            {
                // The thread ended meanwhile.
            }
        }

        return times;
    }

    /// <summary>Pumps the dispatcher for <paramref name="time"/> with an optional driver timer running.</summary>
    private static void Run(TimeSpan time, DispatcherTimer? driver)
    {
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(time, DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        driver?.Start();
        stop.Start();
        Dispatcher.PushFrame(frame);
        stop.Stop();
        driver?.Stop();
    }
}
