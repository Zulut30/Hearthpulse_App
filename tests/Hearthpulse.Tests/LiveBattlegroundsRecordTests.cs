using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// A Battlegrounds game is saved live as soon as it is over for us, before the game itself ends. The client writes our
/// PLAYSTATE and our final place a few milliseconds apart (28.10: lines 312869 and 312879, 17 ms), so a log poll can fall
/// between them; the row must still get the final place.
/// </summary>
public sealed class LiveBattlegroundsRecordTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-live-bg-{Guid.NewGuid():N}");
    private readonly HearthstoneMemory _memory = new();
    private readonly HistoryStore _history;
    private readonly GameSession _session;
    private DateTime _now = new(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc);

    public LiveBattlegroundsRecordTests()
    {
        Directory.CreateDirectory(_dir);
        _history = new HistoryStore(Path.Combine(_dir, "history.db"));
        _session = new GameSession(_history, _memory)
        {
            Clock = () => _now,
            IsHearthstoneRunning = () => true,
            ReadsMemory = false,
            Replays = new ReplayStore(Path.Combine(_dir, "replays")),
        };
        _session.OnSessionStarted("Hearthstone_2026_10_23_12_00_00", new DateTime(2026, 10, 23, 12, 0, 0), attachedMidSession: false);
    }

    public void Dispose()
    {
        _memory.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_final_place_written_in_the_next_poll_reaches_the_row()
    {
        var lines = BattlegroundsReplayTests.Game5th().Select(b => "D 12:30:00.0000000 GameState." + b).ToList();
        var lost = lines.FindIndex(l => l.Contains("tag=PLAYSTATE value=LOST", StringComparison.Ordinal));

        // One poll ends right after our PLAYSTATE; the place comes with the next one, 100 ms later.
        Poll(lines.Take(lost + 1));
        Poll(lines.Skip(lost + 1));

        var game = Assert.Single(_history.Recent());
        Assert.Equal(5, game.Placement);
    }

    [Fact]
    public void A_game_whose_place_never_moves_is_saved_once_the_wait_runs_out()
    {
        var lines = BattlegroundsReplayTests.Game5th().Select(b => "D 12:30:00.0000000 GameState." + b).ToList();
        var lost = lines.FindIndex(l => l.Contains("tag=PLAYSTATE value=LOST", StringComparison.Ordinal));

        // Our place was already final before the end and the log goes quiet: no line says anything more.
        Poll(lines.Take(lost + 1));
        Poll([]);
        Assert.Empty(_history.Recent());

        _now = _now.AddSeconds(2);
        _session.OnTick();
        Assert.Single(_history.Recent());
    }

    private void Poll(IEnumerable<string> lines)
    {
        _session.OnLines("Power.log", lines.ToList(), catchingUp: false);
        _now = _now.AddMilliseconds(100);
        _session.OnTick();
    }
}
