using System.Diagnostics;
using Hearthpulse.Core.Hearthstone;

namespace Hearthpulse.Tests;

/// <summary>Points the process lookup at this test process, so it runs alone.</summary>
[CollectionDefinition(nameof(GlobalGameProcess), DisableParallelization = true)]
public sealed class GlobalGameProcess;

/// <summary>
/// With the game closed the lookup must not snapshot every process on each call (11 ms of kernel time, more than once
/// a second all day): it waits for a Unity window or the next gap.
/// </summary>
[Collection(nameof(GlobalGameProcess))]
public sealed class GameProcessTests
{
    [Fact]
    public void A_closed_game_is_looked_for_again_when_a_window_may_be_its_own_not_on_every_call()
    {
        var names = GameProcess.Names;
        var probe = GameProcess.MayBeRunning;
        try
        {
            GameProcess.Forget();
            var window = false;
            GameProcess.MayBeRunning = () => window;
            GameProcess.Names = ["Hearthpulse-no-such-process"];
            Assert.False(GameProcess.IsRunning());

            // The "game" starts (this process stands in for it): no snapshot until a Unity window may be there.
            GameProcess.Names = [Process.GetCurrentProcess().ProcessName];
            Assert.False(GameProcess.IsRunning());
            window = true;
            Assert.True(GameProcess.IsRunning());

            // Held: no snapshot needed while it runs, whatever the windows.
            window = false;
            Assert.True(GameProcess.IsRunning());
        }
        finally
        {
            GameProcess.Names = names;
            GameProcess.MayBeRunning = probe;
            GameProcess.Forget();
        }
    }
}
