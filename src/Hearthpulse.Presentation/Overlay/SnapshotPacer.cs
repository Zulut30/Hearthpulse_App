#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Spaces the hosts' snapshot updates at least a frame apart. A burst of log lines (a combat, the log catching up)
/// publishes many snapshots in a row; each one applied laid out the overlay again and drew the whole transparent
/// window once more, at 4K several milliseconds of the render thread each. The host keeps coalescing to the newest
/// snapshot; this only says how long to wait before applying it. Thread-safe: asked on the log thread, told on the UI.
/// </summary>
public sealed class SnapshotPacer
{
    /// <summary>Shortest time between two applied snapshots: one frame at 30 fps.</summary>
    public const int MinGapMs = 33;

    private long _lastApplied = long.MinValue / 2;

    /// <summary>Milliseconds to wait at <paramref name="now"/> (Environment.TickCount64) before the next apply; 0 = at once.</summary>
    public int Delay(long now) => (int)Math.Clamp(MinGapMs - (now - Volatile.Read(ref _lastApplied)), 0, MinGapMs);

    /// <summary>A snapshot was applied at <paramref name="now"/>.</summary>
    public void Applied(long now) => Volatile.Write(ref _lastApplied, now);
}
