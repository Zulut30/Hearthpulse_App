using Hearthpulse.Core;

namespace Hearthpulse.Tests;

/// <summary>Errors that reach the apps' global handlers are logged; the player is told once per kind.</summary>
public sealed class CrashLogTests
{
    private sealed class RepeatingTestException(string message) : Exception(message);

    private static Exception Thrown(string message)
    {
        try
        {
            throw new RepeatingTestException(message);
        }
        catch (RepeatingTestException ex)
        {
            return ex;
        }
    }

    [Fact]
    public void The_first_error_of_a_kind_is_shown_and_a_repeating_one_does_not_flood_the_log()
    {
        var marker = Guid.NewGuid().ToString("N");
        Assert.True(CrashLog.Record("UI error " + marker, Thrown("first")));
        for (var i = 0; i < 149; i++)
        {
            Assert.False(CrashLog.Record("UI error " + marker, Thrown("again")));
        }

        var lines = TestDataFolder.ReadLog().Split('\n').Where(l => l.Contains(marker, StringComparison.Ordinal)).ToList();
        // The first three with their stack, then the hundredth.
        Assert.Equal(4, lines.Count);
        Assert.Contains("seen 100 times", lines[^1]);
        Assert.Contains("RepeatingTestException: first", lines[0]);
    }

    [Fact]
    public void The_window_line_names_the_error()
    {
        Assert.Equal("InvalidOperationException: the cache is gone", CrashLog.Describe(new InvalidOperationException("the cache\nis gone".Replace("\n", " "))));
        Assert.Equal("IOException: disk full", CrashLog.Describe(new AggregateException(new IOException("disk full"))));
    }

    [Fact]
    public async Task A_forgotten_task_that_fails_is_logged_right_away()
    {
        var marker = Guid.NewGuid().ToString("N");
        var failing = Task.Run(() => throw new InvalidOperationException(marker));
        failing.Forget("Test task");
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        // The continuation runs synchronously with the task's completion; give the log write a moment.
        for (var i = 0; i < 50 && !TestDataFolder.ReadLog().Contains(marker, StringComparison.Ordinal); i++)
        {
            await Task.Delay(20);
        }

        Assert.Contains($"Test task failed: System.InvalidOperationException: {marker}", TestDataFolder.ReadLog());
    }
}
