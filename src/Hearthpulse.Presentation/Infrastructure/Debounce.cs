#if HEARTHPULSE_WPF
using System.Windows.Threading;

namespace Hearthpulse.App.Infrastructure;
#else
using Avalonia.Threading;

namespace Hearthpulse.UI.Infrastructure;
#endif

/// <summary>
/// Waits for a quiet moment on the UI thread: a burst of changes (settings, history rows) runs the action once, a delay
/// after the last change. The hosts save settings and read the history again this way.
/// </summary>
public static class Debounce
{
    /// <summary>A stopped timer that runs <paramref name="action"/> once, <paramref name="delay"/> after the last <see cref="Kick"/>.</summary>
    public static DispatcherTimer Timer(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    /// <summary>Something changed: the wait starts again from now.</summary>
    public static void Kick(this DispatcherTimer timer)
    {
        timer.Stop();
        timer.Start();
    }
}
