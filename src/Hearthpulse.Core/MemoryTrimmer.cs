using System.Diagnostics;
using System.Runtime;

namespace Hearthpulse.Core;

/// <summary>
/// Gives memory back to Windows once the app goes quiet. Catching up on a log, a game and a meta load leave tens of
/// megabytes of garbage in the old generation, and an idle app allocates too little for the GC to ever collect it: in
/// the owner's log the app sat at 444–450 MB (managed 141–148 MB) for 20 minutes after a Battlegrounds game. One
/// compacting collection after things settle returns it. Never while a game is being played.
/// </summary>
public sealed class MemoryTrimmer
{
    /// <summary>How long nothing may have happened (no new game lines, no request) before the collection.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>The shortest gap between two collections.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long the hosts keep the main window hidden in the tray before they let it go with its pages: a quick hide and
    /// show stays instant, a window put away for a game is given back within a minute.
    /// </summary>
    public static readonly TimeSpan HiddenWindowRelease = TimeSpan.FromSeconds(60);

    private readonly Action _collect;
    private readonly object _gate = new();
    private DateTime _activityAt = DateTime.MinValue;
    private DateTime _lastTrim = DateTime.MinValue;
    private bool _pending;
    private bool _pendingAway;
    private bool _windowInUse;

    /// <param name="collect">The collection; tests pass a counter, the app the real one.</param>
    public MemoryTrimmer(Action? collect = null)
    {
        _collect = collect ?? Collect;
    }

    /// <summary>
    /// The main window is on screen, or hidden in the tray and not let go yet (its release asks for the collection):
    /// a collection asked for with <see cref="NoteActivityWhenWindowAway"/> waits, as its pause would stall the window.
    /// </summary>
    public bool WindowInUse
    {
        get
        {
            lock (_gate)
            {
                return _windowInUse;
            }
        }
        set
        {
            lock (_gate)
            {
                _windowInUse = value;
            }
        }
    }

    /// <summary>Something just left garbage behind (game lines, the start-up): collect once the app has been quiet.</summary>
    public void NoteActivity(DateTime now)
    {
        lock (_gate)
        {
            _pending = true;
            Note(now);
        }
    }

    /// <summary>
    /// Garbage the player's own action left (a meta load for a page or a filter): collect once the main window is out of
    /// the way (minimized or let go) and the app has been quiet, not while the player is looking at it.
    /// </summary>
    public void NoteActivityWhenWindowAway(DateTime now)
    {
        lock (_gate)
        {
            _pendingAway = true;
            Note(now);
        }
    }

    private void Note(DateTime now)
    {
        if (now > _activityAt)
        {
            _activityAt = now;
        }
    }

    /// <summary>Runs the collection when one is due. While <paramref name="busy"/> (a game in progress) it waits.</summary>
    /// <returns>True when it collected.</returns>
    public bool Tick(DateTime now, bool busy)
    {
        lock (_gate)
        {
            var due = _pending || (_pendingAway && !_windowInUse);
            if (!due || busy || now - _activityAt < Settle || now - _lastTrim < MinInterval)
            {
                return false;
            }

            // One collection takes all the garbage, whoever asked.
            _pending = false;
            _pendingAway = false;
            _lastTrim = now;
        }

        _collect();
        return true;
    }

    /// <summary>
    /// A full blocking collection that compacts the large object heap too and decommits what it frees. A closed window
    /// is garbage only once its finalizable parts have run: a first collection queues them, the finalizers run, and the
    /// compacting one takes the window (one collection left a main window let go in the tray in 2 bench runs of 3).
    /// Runs on the log watcher's thread, not the UI's.
    /// </summary>
    private static void Collect()
    {
        const double mb = 1024 * 1024;
        using var process = Process.GetCurrentProcess();
        var managedBefore = GC.GetTotalMemory(false);
        var privateBefore = process.PrivateMemorySize64;
        var started = Stopwatch.GetTimestamp();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var took = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        Diag.Info($"Memory trim: managed {managedBefore / mb:N0} -> {GC.GetTotalMemory(false) / mb:N0} MB, "
                  + $"private {privateBefore / mb:N0} -> {process.PrivateMemorySize64 / mb:N0} MB in {took:N0} ms");
    }
}
