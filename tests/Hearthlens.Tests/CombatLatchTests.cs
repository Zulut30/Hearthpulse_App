using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;
using Xunit;

namespace Hearthlens.Tests;

/// <summary>
/// Regression tests for the overlay getting stuck on "Combat" while the player is in the tavern: some client
/// builds never log the closing BOARD_VISUAL_STATE line, so the combat latch must expire.
/// </summary>
public sealed class CombatLatchTests
{
    [Fact]
    public void Stale_combat_screen_latch_is_released()
    {
        // The clock is passed in on purpose: CI runners boot seconds before this test runs, so
        // Environment.TickCount64 - 200_000 would be negative there and the latch timestamp would look unset.
        const long now = 900_000;
        var game = new GameState
        {
            CombatOnScreen = true,
            CombatOnScreenSince = now - 200_000,
        };

        Assert.True(BattlegroundsTracker.CombatLatchStale(game, now));
        Assert.False(game.CombatOnScreen);
        Assert.Equal(0, game.CombatOnScreenSince);
    }

    [Fact]
    public void Fresh_combat_screen_latch_is_kept()
    {
        const long now = 900_000;
        var game = new GameState
        {
            CombatOnScreen = true,
            CombatOnScreenSince = now,
        };

        Assert.False(BattlegroundsTracker.CombatLatchStale(game, now));
        Assert.True(game.CombatOnScreen);
    }

    [Fact]
    public void Latch_without_timestamp_is_kept()
    {
        var game = new GameState { CombatOnScreen = true };

        Assert.False(BattlegroundsTracker.CombatLatchStale(game, 900_000));
        Assert.True(game.CombatOnScreen);
    }
}
