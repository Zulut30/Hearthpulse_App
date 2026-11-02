using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>The rewards the player enters for Arena runs (<c>docs/arena-rewards.md</c>): kept in the history by run key.</summary>
public sealed class ArenaRewardsTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-arena-rewards-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    [Fact]
    public void Rewards_are_saved_changed_and_cleared_by_run()
    {
        var history = new HistoryStore(_db);
        Assert.Empty(history.ArenaRewards());

        history.SetArenaRewards("deck:1", new ArenaRewards(150, 40, 1, 0, 0));
        history.SetArenaRewards("deck:2", new ArenaRewards(0, 0, 1, 1, 2));
        Assert.Equal(new ArenaRewards(150, 40, 1, 0, 0), new HistoryStore(_db).ArenaRewards()["deck:1"]);

        history.SetArenaRewards("deck:1", new ArenaRewards(200, 40, 1, 0, 0));
        Assert.Equal(200, history.ArenaRewards()["deck:1"].Gold);

        // All zero or none clears the run's rewards.
        history.SetArenaRewards("deck:1", ArenaRewards.None);
        history.SetArenaRewards("deck:2", null);
        Assert.Empty(history.ArenaRewards());
    }

    /// <summary>
    /// The in-memory history tests use for a GameSession (no temp file, no "disk I/O error"): its data stays while pools
    /// are cleared by other tests, and two of them are separate.
    /// </summary>
    [Fact]
    public void A_test_history_in_memory_keeps_its_data_and_is_its_own()
    {
        var first = HistoryStore.InMemory();
        var second = HistoryStore.InMemory();
        first.SetArenaRewards("deck:1", new ArenaRewards(150, 0, 0, 0, 0));
        SqliteConnection.ClearAllPools();

        Assert.Equal(150, first.ArenaRewards()["deck:1"].Gold);
        Assert.Empty(second.ArenaRewards());
    }

    [Fact]
    public void Out_of_range_rewards_are_refused()
    {
        var history = new HistoryStore(_db);
        Assert.Throws<ArgumentException>(() => history.SetArenaRewards("deck:1", new ArenaRewards(-1, 0, 0, 0, 0)));
        Assert.Throws<ArgumentException>(() => history.SetArenaRewards("deck:1", new ArenaRewards(0, ArenaRewards.MaxValue + 1, 0, 0, 0)));
        Assert.Empty(history.ArenaRewards());
    }

    [Fact]
    public void A_run_is_keyed_by_its_deck_id_else_by_class_and_start()
    {
        var start = new DateTime(2026, 10, 28, 0, 33, 42, DateTimeKind.Utc);
        Assert.Equal("deck:2776251657", new ArenaRun("HUNTER", 2776251657, start, start, 0, 0, false, []).Key);
        Assert.Equal("HUNTER:2026-10-28T00:33:42.0000000Z", new ArenaRun("HUNTER", null, start, start, 0, 0, false, []).Key);
        Assert.Equal(new ArenaRewards(150, 40, 2, 1, 2), ArenaRewards.Sum([new(150, 40, 1, 0, 0), new(0, 0, 1, 1, 2)]));
    }
}
