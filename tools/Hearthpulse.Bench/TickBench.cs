using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>
/// The per-tick work of the WPF overlay while it is shown over the game: the click-through hit test along a cursor
/// path, the leaderboard hover and the game window lookup. The overlay is laid out off-screen at 1920x1080 with a
/// mid-game Constructed snapshot of a kept log; nothing follows or covers a real window. The private steps of
/// OverlayWindow.Tick are called through reflection with the same arguments the tick passes.
/// </summary>
internal static class TickBench
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Ticks = 600; // 20 s at 33 ms

    public static void Run(Options options)
    {
        var (app, settings) = WpfBench.NewApplication();
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var (snapshot, all) = MidGame(cards, options);
        if (snapshot is null)
        {
            Console.WriteLine("[tick] no mid-game Constructed snapshot in the kept logs");
            return;
        }

        var bg = WpfBench.BgPanels(settings);
        var vm = new OverlayViewModel();
        vm.Update(snapshot, cards, settings, null, null);
        var window = new OverlayWindow(vm, settings)
        {
            Offscreen = true,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            Width = Width,
            Height = Height,
        };
        window.Show();
        var type = typeof(OverlayWindow);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var root = (Grid)type.GetField("Root", flags)!.GetValue(window)!;
        var surface = (Grid)type.GetField("Surface", flags)!.GetValue(window)!;
        var scale = (ScaleTransform)type.GetField("SurfaceScale", flags)!.GetValue(window)!;
        // As RenderToFileAsync: a 1080-unit surface at scale 1.
        root.Visibility = Visibility.Visible;
        type.GetField("_scale", flags)!.SetValue(window, 1.0);
        type.GetField("_dpi", flags)!.SetValue(window, 1.0);
        scale.ScaleX = scale.ScaleY = 1;
        surface.Width = Width;
        surface.Height = Height;
        type.GetField("_layoutSize", flags)!.SetValue(window, new Size(Width, Height));
        type.GetMethod("ApplyLayout", flags)!.Invoke(window, null);
        Pump();
        window.UpdateLayout();

        // Every snapshot of the kept logs applied to the bound window and laid out (no frame is rendered in between):
        // what a snapshot costs the UI thread beyond Update - item containers, templates, measure and arrange.
        var changedNow = new HashSet<string>();
        var byProperty = new Dictionary<string, (int Count, double Ms)>();
        vm.PropertyChanged += (_, e) => changedNow.Add(e.PropertyName ?? "*");
        for (var round = 0; round < Math.Max(1, options.Rounds); round++)
        {
            var update = new List<double>(all.Count);
            var layout = new List<double>(all.Count);
            long bytes = 0;
            foreach (var s in all)
            {
                changedNow.Clear();
                var a = GC.GetAllocatedBytesForCurrentThread();
                var t = Stopwatch.GetTimestamp();
                vm.Update(s, cards, settings, null, null);
                if (bg)
                {
                    vm.ArrangeLeaderboard(Width, Height, settings);
                }

                var u = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                update.Add(u);
                t = Stopwatch.GetTimestamp();
                window.UpdateLayout();
                var l = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
                layout.Add(l);
                bytes += GC.GetAllocatedBytesForCurrentThread() - a;
                if (round == Math.Max(1, options.Rounds) - 1)
                {
                    // Each snapshot's Update + layout is shared out evenly between the properties it changed.
                    foreach (var name in changedNow)
                    {
                        var c = byProperty.GetValueOrDefault(name);
                        byProperty[name] = (c.Count + 1, c.Ms + ((u + l) / changedNow.Count));
                    }
                }
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[apply] {all.Count:N0} snapshots: Update {Spread(update)}; layout {Spread(layout)}; alloc {bytes / (double)all.Count / 1024:N1} KB/snapshot"));
        }

        foreach (var (name, c) in byProperty.OrderByDescending(x => x.Value.Ms).Take(12))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[apply] {name,-24} changed {c.Count,5:N0}x  share of Update+layout {c.Ms,8:N0} ms ({c.Ms / c.Count:N2} ms each)"));
        }

        vm.Update(snapshot, cards, settings, null, null);
        window.UpdateLayout();

        // What one real change of each list costs, laid out: the last item gone and back (the same items in a new list
        // are no change for Observable.Set). A list with a setter gets a new list (as before), a SyncList is synced in
        // place. The fullest snapshot is shown; no kept log knows the player's deck, so its panel is forced visible.
        typeof(OverlayViewModel).GetProperty(nameof(OverlayViewModel.ShowConstructed))!.SetValue(vm, true);
        window.UpdateLayout();
        foreach (var name in new[] { "Hand", "HandMarks", "Played", "MyCounters", "OpponentCounters", "Deck" })
        {
            var property = typeof(OverlayViewModel).GetProperty(name)!;
            if (property.GetValue(vm) is not System.Collections.IEnumerable items)
            {
                continue;
            }

            var copy = items.Cast<object>().ToList();
            var element = property.PropertyType.GetGenericArguments()[0];
            var rebuilds = new List<double>();
            for (var i = 0; i < 40; i++)
            {
                var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
                foreach (var item in i % 2 == 0 ? copy.Take(copy.Count - 1) : copy)
                {
                    list.Add(item);
                }

                var t = Stopwatch.GetTimestamp();
                if (property.CanWrite)
                {
                    property.SetValue(vm, list);
                }
                else
                {
                    items.GetType().GetMethod("Sync")!.Invoke(items, [list]);
                }

                window.UpdateLayout();
                rebuilds.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[rebuild] {name,-18} {copy.Count,3} items ({(property.CanWrite ? "new list" : "synced")}): {Spread(rebuilds.Skip(5).ToList())}"));
        }

        var native = type.Assembly.GetType("Hearthpulse.App.Infrastructure.Native", throwOnError: true)!;
        var pointType = native.GetNestedType("POINT")!;
        var rectType = native.GetNestedType("RECT")!;
        var rect = Activator.CreateInstance(rectType)!;
        rectType.GetField("Right")!.SetValue(rect, Width);
        rectType.GetField("Bottom")!.SetValue(rect, Height);
        var clickThrough = type.GetMethod("UpdateClickThrough", flags)!;
        var hover = type.GetMethod("UpdateLeaderboardHover", flags)!;

        Console.WriteLine($"[tick] snapshot turn {snapshot.Constructed!.Turn}, deck known {snapshot.Constructed.DeckKnown}, deck {snapshot.Constructed.Deck.Count} rows, opponent hand {snapshot.Constructed.OpponentHand.Count}, played {snapshot.Constructed.OpponentPlayed.Count}; {CountVisuals(root):N0} visuals");
        foreach (var (name, path) in Paths())
        {
            var points = path.Select(p =>
            {
                var point = Activator.CreateInstance(pointType)!;
                pointType.GetField("X")!.SetValue(point, p.X);
                pointType.GetField("Y")!.SetValue(point, p.Y);
                return point;
            }).ToList();

            // A warm-up pass, then the measured ones; the cursor path is the same each time.
            Drive(points, clickThrough, hover, window, rect);
            var times = new List<double>();
            long bytes = 0;
            for (var round = 0; round < Math.Max(3, options.Rounds); round++)
            {
                var a = GC.GetAllocatedBytesForCurrentThread();
                var t = Stopwatch.GetTimestamp();
                Drive(points, clickThrough, hover, window, rect);
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                bytes += GC.GetAllocatedBytesForCurrentThread() - a;
            }

            var perTick = times.Average() * 1000 / Ticks;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[tick] {name,-22} click-through + hover {perTick,7:N1} us/tick (min {times.Min() * 1000 / Ticks:N1}) = {perTick * 30 / 1000,5:N2} ms/s at 30 ticks/s; alloc {bytes / (double)times.Count / Ticks,6:N0} B/tick"));
        }

        // The card pool panel's 50 ms poll (pointer, buttons, chart hover), which runs while the panel is shown.
        if (FindVisual<CardPoolPanel>(root) is { } pool && typeof(CardPoolPanel).GetMethod("Poll", flags) is { } poll)
        {
            poll.Invoke(pool, null);
            var t = Stopwatch.GetTimestamp();
            var a = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 2000; i++)
            {
                poll.Invoke(pool, null);
            }

            var us = Stopwatch.GetElapsedTime(t).TotalMilliseconds * 1000 / 2000;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[tick] CardPoolPanel.Poll {us:N1} us each = {us * 20 / 1000:N2} ms/s at 20 polls/s; alloc {(GC.GetAllocatedBytesForCurrentThread() - a) / 2000.0:N0} B each (pointer off the panel)"));
        }

        // The bare hit test the click-through decision is made of.
        var hits = new List<double>();
        var random = new Random(7);
        for (var i = 0; i < 2000; i++)
        {
            var p = new Point(random.Next(Width), random.Next(Height));
            var t = Stopwatch.GetTimestamp();
            _ = root.InputHitTest(p);
            hits.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds * 1000);
        }

        hits.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[tick] Root.InputHitTest at random points: avg {hits.Average():N1} us, p50 {Percentile(hits, .5):N1}, p95 {Percentile(hits, .95):N1}, max {hits[^1]:N1} us"));

        // The game window lookup the tick runs every 1.5 s, and the session's "is it running" every 2 s. With the game
        // closed both are a process snapshot. With it running, this process stands in for Hearthstone (it has a main
        // window: the off-screen overlay): the old way snapshot + window enumeration, now the held process.
        var find = native.GetMethod("FindHearthstoneWindow", BindingFlags.Public | BindingFlags.Static)!;
        Console.WriteLine($"[lookup] {Process.GetProcesses().Length} processes, Hearthstone {(Process.GetProcessesByName("Hearthstone").Length > 0 ? "running" : "not running")}");
        Lookup("window, game closed (FindHearthstoneWindow)", () => find.Invoke(null, null));
        Lookup("running?, game closed (IsRunning)", () => Core.Hearthstone.HearthstoneInstall.IsRunning());
        var self = Process.GetCurrentProcess().ProcessName;
        // The overlay window has an owner (no taskbar button), which a main window may not: an off-screen stand-in.
        var stand = new Window { Width = 400, Height = 300, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, WindowStyle = WindowStyle.None };
        stand.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(stand).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x80 | 0x08000000); // tool window, no activate
        };
        stand.Show();
        Lookup("window, game running: snapshot + MainWindowHandle (as before)", () =>
        {
            var window = IntPtr.Zero;
            foreach (var process in Process.GetProcessesByName(self))
            {
                using (process)
                {
                    window = window == IntPtr.Zero ? process.MainWindowHandle : window;
                }
            }

            if (window == IntPtr.Zero)
            {
                throw new InvalidOperationException("the bench process has no main window");
            }
        });
        Lookup("running?, game running: snapshot (as before)", () =>
        {
            foreach (var process in Process.GetProcessesByName(self))
            {
                process.Dispose();
            }
        });
        // GameProcess exists from this branch on; the same bench also builds against the base commit.
        if (typeof(Core.Hearthstone.HearthstoneInstall).Assembly.GetType("Hearthpulse.Core.Hearthstone.GameProcess") is { } game)
        {
            var namesProperty = game.GetProperty("Names", BindingFlags.NonPublic | BindingFlags.Static)!;
            var names = namesProperty.GetValue(null);
            namesProperty.SetValue(null, new[] { self });
            // The stand-in has no Unity window: without this the closed-game lookups above hold off the next snapshot.
            var mayBeRunning = game.GetProperty("MayBeRunning", BindingFlags.NonPublic | BindingFlags.Static);
            var probe = mayBeRunning?.GetValue(null);
            mayBeRunning?.SetValue(null, new Func<bool>(() => true));
            var mainWindow = game.GetMethod("MainWindow")!;
            var isRunning = game.GetMethod("IsRunning")!;
            Lookup("window, game running: GameProcess.MainWindow", () =>
            {
                if ((IntPtr)mainWindow.Invoke(null, null)! == IntPtr.Zero)
                {
                    throw new InvalidOperationException("the bench process has no main window");
                }
            });
            Lookup("running?, game running: GameProcess.IsRunning", () => isRunning.Invoke(null, null));
            namesProperty.SetValue(null, names);
            mayBeRunning?.SetValue(null, probe);
        }

        stand.Close();

        window.Close();
        GC.KeepAlive(app);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    private static void Lookup(string what, Action lookup)
    {
        lookup();
        var times = new List<double>();
        long bytes = 0;
        for (var i = 0; i < 20; i++)
        {
            var a = GC.GetAllocatedBytesForCurrentThread();
            var t = Stopwatch.GetTimestamp();
            lookup();
            times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            bytes += GC.GetAllocatedBytesForCurrentThread() - a;
        }

        times.Sort();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[lookup] {what,-62} avg {times.Average():N3} ms, p50 {Percentile(times, .5):N3}, max {times[^1]:N3} ms, alloc {bytes / 20.0 / 1024:N1} KB"));
    }

    private static void Drive(List<object> points, MethodInfo clickThrough, MethodInfo hover, OverlayWindow window, object rect)
    {
        var args = new object?[2];
        args[1] = rect;
        foreach (var point in points)
        {
            args[0] = point;
            clickThrough.Invoke(window, args);
            hover.Invoke(window, args);
        }
    }

    /// <summary>
    /// Cursor paths over 600 ticks: resting (the mouse rarely moves while the player thinks), always moving, and mixed
    /// (rests of 0.2-2 s between moves of 0.2-1 s across the screen, panels included).
    /// </summary>
    private static IEnumerable<(string Name, List<(int X, int Y)> Path)> Paths()
    {
        yield return ("resting on the board", Enumerable.Repeat((960, 600), Ticks).ToList());
        var random = new Random(3);
        yield return ("always moving", Enumerable.Range(0, Ticks).Select(_ => (random.Next(Width), random.Next(Height))).ToList());
        var mixed = new List<(int X, int Y)>(Ticks);
        var at = (X: 960, Y: 600);
        while (mixed.Count < Ticks)
        {
            for (var i = random.Next(6, 60); i > 0; i--)
            {
                mixed.Add(at);
            }

            var to = (X: random.Next(Width), Y: random.Next(Height));
            var steps = random.Next(6, 30);
            for (var i = 1; i <= steps; i++)
            {
                mixed.Add((at.X + ((to.X - at.X) * i / steps), at.Y + ((to.Y - at.Y) * i / steps)));
            }

            at = to;
        }

        yield return ("mixed rests and moves", mixed.Take(Ticks).ToList());
    }

    /// <summary>
    /// Every in-game snapshot of the kept logs, and the fullest Constructed one: most deck rows, opponent hand and
    /// played cards.
    /// </summary>
    internal static (LiveSnapshot? Best, List<LiveSnapshot> All) MidGame(CardDb cards, Options options)
    {
        LiveSnapshot? best = null;
        var all = new List<LiveSnapshot>();
        var score = 0;
        foreach (var (dir, path, start) in LogReplay.Logs(options.Logs))
        {
            var (session, setClock, db) = LogReplay.NewSession(cards);
            session.SnapshotChanged += s =>
            {
                all.Add(s);
                if (s is { InGame: true, Constructed: { } c } && c.Deck.Count + c.OpponentHand.Count + c.OpponentPlayed.Count + (c.DeckKnown ? 100 : 0) > score)
                {
                    score = c.Deck.Count + c.OpponentHand.Count + c.OpponentPlayed.Count + (c.DeckKnown ? 100 : 0);
                    best = s;
                }
            };
            LogReplay.Run(session, setClock, dir, path, start);
            LogReplay.Delete(db);
        }

        return (best, all);
    }

    private static T? FindVisual<T>(DependencyObject node)
        where T : DependencyObject
    {
        if (node is T found)
        {
            return found;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (FindVisual<T>(VisualTreeHelper.GetChild(node, i)) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static int CountVisuals(DependencyObject node)
    {
        var count = 1;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            count += CountVisuals(VisualTreeHelper.GetChild(node, i));
        }

        return count;
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => frame.Continue = false), DispatcherPriority.ApplicationIdle);
        Dispatcher.PushFrame(frame);
    }
}
