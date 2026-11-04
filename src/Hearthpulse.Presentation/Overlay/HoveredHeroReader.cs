using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Asks the game which hero is under the mouse on the Battlegrounds leaderboard (the memory reader), off the UI thread
/// and at most four times a second, and keeps the last answer for the overlay tick. When the game has no answer the
/// overlay falls back to the leaderboard calibration (<see cref="LeaderboardGeometry"/>).
/// </summary>
public sealed class HoveredHeroReader
{
    /// <summary>The shortest time between two reads.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private int _busy;
    private DateTime _next;
    private volatile string? _hero;

    /// <summary>The hero card id the last read found under the mouse; null when none or not asked.</summary>
    public string? Hero => _hero;

    /// <summary>The mouse left the leaderboard, or the game cannot be asked: no hero under it.</summary>
    public void Clear() => _hero = null;

    /// <summary>
    /// Starts a background read with <paramref name="read"/> unless one ran less than <see cref="Interval"/> ago or is
    /// still running. A read that throws is logged and leaves no hero.
    /// </summary>
    public void Poll(Func<string?> read, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (now < _next || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        _next = now + Interval;
        _ = Task.Run(() =>
        {
            try
            {
                _hero = read();
            }
            catch (Exception ex)
            {
                _hero = null;
                Diag.Error("leaderboard hover read failed", ex);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
    }
}
