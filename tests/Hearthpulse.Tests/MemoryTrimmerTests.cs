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
