using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The live game on its way from the log thread to the screen, the same in both apps. <see cref="Publish"/> is called on
/// the log thread for every snapshot; the newest one is shown once per UI-thread pass, at most once a frame
/// (<see cref="SnapshotPacer"/>): the main window first, then the overlay. The record of the deck being played
/// (<see cref="LiveDeckRecord"/>) is read off the UI thread whenever the deck changes, and the overlay is shown again
/// when it arrives. Everything but <see cref="Publish"/> and <see cref="Waiting"/> runs on the UI thread.
/// </summary>
/// <param name="post">Runs an action on the UI thread at background priority (after input and rendering).</param>
/// <param name="readDeck">The deck record of a view: <see cref="LiveDeckRecord.Read"/> with the host's history and install folder.</param>
/// <param name="showMain">Shows a snapshot in the main window.</param>
/// <param name="showOverlay">Shows <see cref="Last"/> with <see cref="Deck"/> and <see cref="Matchup"/> on the overlay.</param>
public sealed class LiveFeed(
    Action<Action> post,
    Func<ConstructedView, (DeckAggregate? Deck, MatchupAggregate? Matchup)> readDeck,
    Action<LiveSnapshot> showMain,
    Action showOverlay)
{
    private readonly SnapshotPacer _pacer = new();
    private LiveSnapshot? _pending;
    private int _scheduled;
    private long _scheduledAt;
    private string? _deckKey;
    private int _applied;
    private long _slowestApply;
    private long _longestWait;

    /// <summary>Milliseconds now (<see cref="Environment.TickCount64"/>); tests set their own.</summary>
    internal Func<long> Clock { get; init; } = () => Environment.TickCount64;

    /// <summary>Waits before a paced apply; tests complete it themselves.</summary>
    internal Func<int, Task> Delay { get; init; } = ms => Task.Delay(ms);

    /// <summary>The snapshot shown last.</summary>
    public LiveSnapshot Last { get; private set; } = LiveSnapshot.Empty;

    /// <summary>The record of the deck in <see cref="Last"/>, once read; null while it is read or without a deck.</summary>
    public DeckAggregate? Deck { get; private set; }

    /// <summary>The deck's record against the current opponent's class, once read.</summary>
    public MatchupAggregate? Matchup { get; private set; }


    /// <summary>How long the newest snapshot has been waiting to be shown, in milliseconds; 0 when none waits. Any thread.</summary>
    public long Waiting
    {
        get
        {
            var scheduledAt = Volatile.Read(ref _scheduledAt);
            return Volatile.Read(ref _scheduled) == 1 ? Clock() - scheduledAt : 0;
        }
    }

    /// <summary>Called on the log thread: keeps the newest snapshot and schedules one apply if none is pending.</summary>
    public void Publish(LiveSnapshot snapshot)
    {
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
        {
            return;
        }

        var now = Clock();
        Volatile.Write(ref _scheduledAt, now);
        var delay = _pacer.Delay(now);
        if (delay == 0)
        {
            post(Apply);
        }
        else
        {
            _ = Delay(delay).ContinueWith(_ => post(Apply), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Since the last call: snapshots shown, the longest one took to show and the longest one waited for the UI thread
    /// (milliseconds). The counts start again from zero (the WPF watchdog logs them every few minutes).
    /// </summary>
    public (int Applied, long SlowestApply, long LongestWait) TakeStats()
    {
        var stats = (_applied, _slowestApply, _longestWait);
        (_applied, _slowestApply, _longestWait) = (0, 0, 0);
        return stats;
    }

    /// <summary>The history changed: the deck record is read again with the next snapshot.</summary>
    public void ForgetDeck() => _deckKey = null;

    private void Apply()
    {
        Interlocked.Exchange(ref _scheduled, 0);
        _pacer.Applied(Clock());
        var snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot is null)
        {
            return;
        }

        var waited = Clock() - Volatile.Read(ref _scheduledAt);
        _longestWait = Math.Max(_longestWait, waited);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        Last = snapshot;
        showMain(snapshot);
        FollowDeck(snapshot.Constructed);
        showOverlay();
        var took = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _applied++;
        _slowestApply = Math.Max(_slowestApply, took);
        if (took > 200 || waited > 1000)
        {
            Diag.Info($"Showing a snapshot took {took:N0} ms after waiting {waited:N0} ms for the UI thread (BG turn {snapshot.Battlegrounds?.Turn})");
        }
    }

    private void FollowDeck(ConstructedView? view)
    {
        var key = LiveDeckRecord.Key(view);
        if (key == _deckKey)
        {
            return;
        }

        _deckKey = key;
        Deck = null;
        Matchup = null;
        if (view is null)
        {
            return;
        }

        _ = Task.Run(() => readDeck(view)).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Diag.Error("Deck stats lookup failed", t.Exception);
            }

            if (t.IsCompletedSuccessfully)
            {
                post(() =>
                {
                    if (_deckKey == key)
                    {
                        (Deck, Matchup) = t.Result;
                        showOverlay();
                    }
                });
            }
        }, TaskScheduler.Default);
    }
}
