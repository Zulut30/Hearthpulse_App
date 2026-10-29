using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// The garbage of a catch-up or a game is collected once the app is quiet, never while a game is being played, and
/// the log watcher slows down while the game is closed.
/// </summary>
public sealed class MemoryTrimmerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-trim-{Guid.NewGuid():N}.db");
    private readonly HearthstoneMemory _memory = new();
    private readonly HistoryStore _history;
    private readonly GameSession _session;
    private DateTime _now = T0;
    private bool _running;
    private int _collections;

    public MemoryTrimmerTests()
    {
        _history = new HistoryStore(_db);
        _session = new GameSession(_history, _memory)
        {
            Clock = () => _now,
            IsHearthstoneRunning = () => _running,
            ReadsMemory = false,
            Trimmer = new MemoryTrimmer(() => _collections++),
        };
        _session.OnSessionStarted("Hearthstone_2026_10_23_02_01_43", new DateTime(2026, 10, 23, 2, 1, 43), attachedMidSession: false);
    }

    public void Dispose()
    {
        _memory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private void TickAt(TimeSpan sinceT0)
    {
        _now = T0 + sinceT0;
        _session.OnTick();
    }

    [Fact]
    public void Nothing_is_collected_without_activity_and_one_request_collects_once_after_the_settle_time()
    {
        var count = 0;
        var trimmer = new MemoryTrimmer(() => count++);

        Assert.False(trimmer.Tick(T0 + TimeSpan.FromHours(1), busy: false));

        trimmer.NoteActivity(T0);
        Assert.False(trimmer.Tick(T0 + MemoryTrimmer.Settle - TimeSpan.FromSeconds(1), busy: false));
        Assert.True(trimmer.Tick(T0 + MemoryTrimmer.Settle, busy: false));
        Assert.False(trimmer.Tick(T0 + TimeSpan.FromHours(2), busy: false));
        Assert.Equal(1, count);
    }

    [Fact]
    public void New_activity_and_a_game_put_the_collection_off_and_two_are_at_least_the_minimum_interval_apart()
    {
        var count = 0;
        var trimmer = new MemoryTrimmer(() => count++);

        trimmer.NoteActivity(T0);
        trimmer.NoteActivity(T0 + TimeSpan.FromSeconds(15));
        Assert.False(trimmer.Tick(T0 + MemoryTrimmer.Settle, busy: false));
        Assert.False(trimmer.Tick(T0 + TimeSpan.FromMinutes(10), busy: true));
        Assert.True(trimmer.Tick(T0 + TimeSpan.FromMinutes(10), busy: false));

        var last = T0 + TimeSpan.FromMinutes(10);
        trimmer.NoteActivity(last);
        Assert.False(trimmer.Tick(last + MemoryTrimmer.MinInterval - TimeSpan.FromSeconds(1), busy: false));
        Assert.True(trimmer.Tick(last + MemoryTrimmer.MinInterval, busy: false));
        Assert.Equal(2, count);
    }

    /// <summary>
    /// A meta load the player asked for (a page, a filter) does not pause the window they are looking at: its collection
    /// waits while the window is on screen or waiting in the tray, and runs once it is minimized or let go. Start-up and
    /// game garbage is collected as before, window or not, and that collection takes the waiting garbage too.
    /// </summary>
    [Fact]
    public void A_meta_load_collects_only_once_the_window_is_away()
    {
        var count = 0;
        var trimmer = new MemoryTrimmer(() => count++) { WindowInUse = true };

        trimmer.NoteActivityWhenWindowAway(T0);
        Assert.False(trimmer.Tick(T0 + TimeSpan.FromMinutes(10), busy: false));
        trimmer.WindowInUse = false;
        Assert.True(trimmer.Tick(T0 + TimeSpan.FromMinutes(10), busy: false));
        Assert.Equal(1, count);

        var later = T0 + TimeSpan.FromMinutes(20);
        trimmer.WindowInUse = true;
        trimmer.NoteActivityWhenWindowAway(later);
        trimmer.NoteActivity(later + TimeSpan.FromSeconds(5));
        Assert.True(trimmer.Tick(later + TimeSpan.FromSeconds(5) + MemoryTrimmer.Settle, busy: false));
        trimmer.WindowInUse = false;
        Assert.False(trimmer.Tick(later + TimeSpan.FromMinutes(30), busy: false));
        Assert.Equal(2, count);
    }

    /// <summary>
    /// Minimizing, closing and letting go of the window ask too, while the window is away. Shown again before the
    /// collection (within the settle time, or while the minimum interval holds it), the window puts it off: the pause
    /// never lands on a window the player just brought back.
    /// </summary>
    [Fact]
    public void A_window_brought_back_before_its_collection_puts_it_off()
    {
        var count = 0;
        var trimmer = new MemoryTrimmer(() => count++);

        trimmer.NoteActivityWhenWindowAway(T0);
        trimmer.WindowInUse = true;
        Assert.False(trimmer.Tick(T0 + MemoryTrimmer.Settle, busy: false));
        Assert.False(trimmer.Tick(T0 + TimeSpan.FromMinutes(5), busy: false));
        trimmer.WindowInUse = false;
        Assert.True(trimmer.Tick(T0 + TimeSpan.FromMinutes(5), busy: false));
        Assert.Equal(1, count);
    }

    /// <summary>
    /// Only a collection after a window was let go or closed runs the finalizers too (the window is garbage only after
    /// them); start-up, game and meta collections stay single, as their pause was 31–62 ms against 87–102 with it.
    /// </summary>
    [Fact]
    public void Only_a_collection_after_a_window_was_let_go_runs_the_finalizers()
    {
        var kinds = new List<bool>();
        var trimmer = new MemoryTrimmer(kinds.Add);
        var at = T0;
        void CollectAfter(Action ask)
        {
            ask();
            at += MemoryTrimmer.MinInterval;
            Assert.True(trimmer.Tick(at, busy: false));
        }

        CollectAfter(() => trimmer.NoteActivity(at));
        CollectAfter(() => trimmer.NoteActivityWhenWindowAway(at));
        CollectAfter(() => trimmer.NoteActivityWhenWindowAway(at, windowLetGo: true));
        // Let go, then a meta load before the collection: still one collection, with the finalizers.
        CollectAfter(() =>
        {
            trimmer.NoteActivityWhenWindowAway(at, windowLetGo: true);
            trimmer.NoteActivityWhenWindowAway(at);
        });
        CollectAfter(() => trimmer.NoteActivity(at));
        Assert.Equal([false, false, true, true, false], kinds);
    }

    /// <summary>
    /// The hosts ask after every meta load and when the window hides: a burst (meta, language, tray) collects once,
    /// and a request soon after a collection is kept for the next allowed one, not dropped.
    /// </summary>
    [Fact]
    public void A_burst_of_requests_collects_once_and_a_request_too_soon_waits_for_the_minimum_interval()
    {
        var count = 0;
        var trimmer = new MemoryTrimmer(() => count++);
        var at = new List<TimeSpan>();
        void TickEverySecond(TimeSpan from, TimeSpan to)
        {
            for (var t = from; t <= to; t += TimeSpan.FromSeconds(1))
            {
                if (trimmer.Tick(T0 + t, busy: false))
                {
                    at.Add(t);
                }
            }
        }

        trimmer.NoteActivity(T0);
        trimmer.NoteActivity(T0 + TimeSpan.FromSeconds(2));
        trimmer.NoteActivity(T0 + TimeSpan.FromSeconds(5));
        TickEverySecond(TimeSpan.Zero, TimeSpan.FromSeconds(60));
        Assert.Equal([TimeSpan.FromSeconds(5) + MemoryTrimmer.Settle], at);

        var first = at[0];
        trimmer.NoteActivity(T0 + first + TimeSpan.FromSeconds(30));
        TickEverySecond(TimeSpan.FromSeconds(61), first + MemoryTrimmer.MinInterval + TimeSpan.FromMinutes(5));
        Assert.Equal([first, first + MemoryTrimmer.MinInterval], at);
        Assert.Equal(2, count);
    }

    [Fact]
    public void A_game_in_progress_is_never_interrupted_and_its_garbage_goes_once_it_is_over()
    {
        // The Battlegrounds fixture ends with the client closed in the third round: live until saved as abandoned.
        _running = true;
        _session.OnLines("Power.log", BattlegroundsFixtureTests.Lines().ToList(), catchingUp: false);
        TickAt(TimeSpan.Zero);
        TickAt(TimeSpan.FromMinutes(1));
        _session.RequestMemoryTrim();
        TickAt(TimeSpan.FromMinutes(3));
        Assert.Equal(0, _collections);

        _running = false;
        TickAt(TimeSpan.FromMinutes(4));
        TickAt(TimeSpan.FromMinutes(4) + GameSession.AbandonAfter);
        Assert.Single(_history.Recent());
        TickAt(TimeSpan.FromMinutes(5) + GameSession.AbandonAfter);
        Assert.Equal(1, _collections);
    }

    [Fact]
    public void The_watcher_slows_down_only_while_the_game_is_closed_and_its_log_is_quiet()
    {
        TickAt(TimeSpan.Zero);
        Assert.True(_session.Quiet);

        _session.OnLines("Power.log", BattlegroundsFixtureTests.Lines().Take(50).ToList(), catchingUp: false);
        Assert.False(_session.Quiet);
        TickAt(TimeSpan.FromSeconds(11));
        Assert.True(_session.Quiet);

        _running = true;
        TickAt(TimeSpan.FromSeconds(30));
        Assert.False(_session.Quiet);
    }
}
