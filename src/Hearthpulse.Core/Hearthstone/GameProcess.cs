using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hearthpulse.Core.Hearthstone;

/// <summary>
/// The running Hearthstone process, held once found. While it runs, asking for it costs a check of its handle instead
/// of a snapshot of every process on the machine: 5-6 ms with about 360 processes, which the overlay took on its UI
/// thread every 1.5 s and the session on the log thread every 2 s for the whole game. Thread-safe.
/// </summary>
public static class GameProcess
{
    private static readonly object Gate = new();
    private static Process? _held;

    /// <summary>
    /// With the game closed a snapshot found nothing, at 11 ms of kernel time with about 470 processes, and the overlay
    /// and the session asked for one more than once a second all day. Now a snapshot runs at most this often, or at once
    /// when a Unity window appears (the game's window comes up seconds after its process).
    /// </summary>
    private const long SnapshotGapMs = 10_000;

    private static long _nextSnapshot;

    /// <summary>
    /// False only when the game cannot be running with a window: on Windows, no top-level Unity window exists. A few
    /// microseconds instead of a snapshot; elsewhere always true. Tests and the bench replace it.
    /// </summary>
    internal static Func<bool> MayBeRunning { get; set; } = () => !OperatingSystem.IsWindows() || FindWindowW("UnityWndClass", null) != IntPtr.Zero;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    /// <summary>
    /// Process names that count as the game (the bench points it at itself). Windows drops ".exe" from process names, so
    /// there a second name would only cost a second snapshot of every process.
    /// </summary>
    internal static string[] Names { get; set; } = OperatingSystem.IsWindows() ? ["Hearthstone"] : ["Hearthstone", "Hearthstone.exe"];

    /// <summary>True while Hearthstone runs. A failed process snapshot throws <see cref="OutOfMemoryException"/>.</summary>
    public static bool IsRunning()
    {
        lock (Gate)
        {
            return Held() is not null;
        }
    }

    /// <summary>The game's main window, or zero when it is not running or has no window yet.</summary>
    public static IntPtr MainWindow()
    {
        lock (Gate)
        {
            try
            {
                if (Held() is not { } process)
                {
                    return IntPtr.Zero;
                }

                // Process keeps the first main window it found; the game may have replaced it since.
                process.Refresh();
                if (process.MainWindowHandle is var window && window != IntPtr.Zero)
                {
                    return window;
                }

                // No window (yet): as before it was held, the first game process that has one, which is then kept.
                foreach (var other in Process.GetProcessesByName(Names[0]))
                {
                    if (window == IntPtr.Zero && other.Id != process.Id && other.MainWindowHandle != IntPtr.Zero)
                    {
                        window = other.MainWindowHandle;
                        process.Dispose();
                        _held = other;
                        continue;
                    }

                    other.Dispose();
                }

                return window;
            }
            catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException or Win32Exception)
            {
                // A failed snapshot (the system is short of memory) or the game exited just now: try on the next lookup.
                return IntPtr.Zero;
            }
        }
    }

    /// <summary>Drops the held process and the snapshot gap (tests).</summary>
    internal static void Forget()
    {
        lock (Gate)
        {
            _held?.Dispose();
            _held = null;
            _nextSnapshot = 0;
        }
    }

    /// <summary>When the running game started, or null.</summary>
    public static DateTime? StartTime()
    {
        lock (Gate)
        {
            try
            {
                return Held()?.StartTime;
            }
            catch (Exception ex) when (ex is OutOfMemoryException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The held process while it runs, else a snapshot by name (the first match is kept), at most every
    /// <see cref="SnapshotGapMs"/> while no Unity window exists. Under <see cref="Gate"/>.
    /// </summary>
    private static Process? Held()
    {
        if (_held is { } held)
        {
            try
            {
                if (!held.HasExited)
                {
                    return held;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // No access to its handle: look it up by name each time, as before it was held.
            }

            held.Dispose();
            _held = null;
            // The game just closed (or cannot be checked by handle): look again at once.
            _nextSnapshot = 0;
        }

        var now = Environment.TickCount64;
        if (now < _nextSnapshot && !MayBeRunning())
        {
            return null;
        }

        _nextSnapshot = now + SnapshotGapMs;
        foreach (var name in Names)
        {
            Process? found = null;
            foreach (var process in Process.GetProcessesByName(name))
            {
                // Every Process holds a handle until disposed; only the kept one stays open.
                if (found is null)
                {
                    found = process;
                }
                else
                {
                    process.Dispose();
                }
            }

            if (found is not null)
            {
                _held = found;
                return found;
            }
        }

        return null;
    }
}
