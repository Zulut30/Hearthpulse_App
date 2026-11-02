using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// The hosts' live-game pipeline (<see cref="LiveFeed"/>, shared by both apps): snapshots from the log thread coalesce
/// to the newest, are shown at most once a frame, the main window before the overlay, and the deck record follows the
/// deck being played. The UI thread is a queue the test runs; the clock and the pacing wait are the test's too.
/// </summary>
public sealed class LiveFeedTests
{
    private readonly Queue<Action> _ui = new();
    private readonly List<string> _shown = [];
    private readonly List<TaskCompletionSource> _waits = [];
    private readonly List<int> _waitLengths = [];
    private readonly List<ConstructedView> _deckReads = [];
    private readonly TaskCompletionSource<(DeckAggregate?, MatchupAggregate?)> _deckAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _now = 1_000;

    private LiveFeed Feed() => new(
        post: action =>
        {
            lock (_ui)
            {
                _ui.Enqueue(action);
                Monitor.PulseAll(_ui);
            }
        },
        readDeck: view =>
        {
            lock (_deckReads)
            {
                _deckReads.Add(view);
            }

            return _deckAnswer.Task.GetAwaiter().GetResult();
        },
        showMain: s => _shown.Add("main " + s.Scene),
        showOverlay: () => _shown.Add("overlay"))
    {
        Clock = () => Interlocked.Read(ref _now),
        Delay = ms =>
        {
            var wait = new TaskCompletionSource();
            lock (_waits)
            {
                _waits.Add(wait);
                _waitLengths.Add(ms);
            }

            return wait.Task;
        },
    };

    private static LiveSnapshot Snap(string scene, ConstructedView? constructed = null) =>
        LiveSnapshot.Empty with { Scene = scene, Constructed = constructed };

    private static ConstructedView Deck(string name) =>
        new(5, name, true, "HERO_08", "MAGE", "HERO_01", "WARRIOR", null, [], 20, 4, [], [], 18, 5, 0, null, null);

    /// <summary>Runs what is queued for the UI thread now.</summary>
    private int RunUi()
    {
        var ran = 0;
        while (true)
        {
            Action next;
            lock (_ui)
            {
                if (_ui.Count == 0)
                {
                    return ran;
                }

                next = _ui.Dequeue();
            }

            next();
            ran++;
        }
    }

    /// <summary>Waits until something is queued for the UI thread by a worker (bounded: a hang fails, it never decides).</summary>
    private void WaitForUi()
    {
        lock (_ui)
        {
            while (_ui.Count == 0)
            {
                Assert.True(Monitor.Wait(_ui, TimeSpan.FromSeconds(10)), "nothing reached the UI thread");
            }
        }
    }

    [Fact]
    public void A_burst_of_snapshots_is_shown_once_with_the_newest_main_window_first()
    {
        var feed = Feed();

        feed.Publish(Snap("one"));
        feed.Publish(Snap("two"));
        feed.Publish(Snap("three"));

        Assert.Equal(1, RunUi());
        Assert.Equal(["main three", "overlay"], _shown);
        Assert.Equal("three", feed.Last.Scene);
        Assert.Equal(1, feed.TakeStats().Applied);
        Assert.Equal(0, RunUi());
    }

    [Fact]
    public void A_snapshot_within_a_frame_of_the_last_one_waits_out_the_rest_of_the_frame()
    {
        var feed = Feed();
        feed.Publish(Snap("one"));
        RunUi();

        _now += 10;
        feed.Publish(Snap("two"));
        Assert.Equal(0, RunUi());
        Assert.Equal([SnapshotPacer.MinGapMs - 10], _waitLengths);
        Assert.Equal(0, feed.Waiting); // scheduled at this very moment

        _now += 23;
        Assert.Equal(23, feed.Waiting);
        feed.Publish(Snap("three")); // joins the scheduled apply, no second wait
        Assert.Single(_waits);

        _waits[0].SetResult();
        WaitForUi();
        RunUi();
        Assert.Equal(["main one", "overlay", "main three", "overlay"], _shown);
        Assert.Equal(0, feed.Waiting);
        var stats = feed.TakeStats();
        Assert.Equal((2, 23L), (stats.Applied, stats.LongestWait));
        Assert.Equal((0, 0L, 0L), feed.TakeStats());
    }

    [Fact]
    public void The_deck_record_is_read_once_per_deck_and_shows_the_overlay_again_when_it_arrives()
    {
        var feed = Feed();
        feed.Publish(Snap("one", Deck("Big Mage")));
        RunUi();
        Assert.Null(feed.Deck);

        var record = new DeckAggregate("Big Mage", null, "MAGE", 10, 6, 4, DateTime.UnixEpoch);
        _deckAnswer.SetResult((record, null));
        WaitForUi();
        RunUi();
        Assert.Same(record, feed.Deck);
        Assert.Equal(["main one", "overlay", "overlay"], _shown);

        // The same deck: no new read; the history changed: read again.
        _now += 100;
        feed.Publish(Snap("two", Deck("Big Mage")));
        RunUi();
        Assert.Single(_deckReads);
        Assert.Same(record, feed.Deck);

        feed.ForgetDeck();
        _now += 100;
        feed.Publish(Snap("three", Deck("Big Mage")));
        RunUi();
        Assert.Null(feed.Deck);
        WaitForUi();
        RunUi();
        lock (_deckReads)
        {
            Assert.Equal(2, _deckReads.Count);
        }
    }

    [Fact]
    public void A_deck_record_that_arrives_after_the_deck_changed_is_dropped()
    {
        var feed = Feed();
        feed.Publish(Snap("one", Deck("Big Mage")));
        RunUi();

        _now += 100;
        feed.Publish(Snap("two")); // left the game: no deck
        RunUi();

        _deckAnswer.SetResult((new DeckAggregate("Big Mage", null, "MAGE", 10, 6, 4, DateTime.UnixEpoch), null));
        WaitForUi();
        RunUi();
        Assert.Null(feed.Deck);
        Assert.Equal(["main one", "overlay", "main two", "overlay"], _shown);
    }
}
