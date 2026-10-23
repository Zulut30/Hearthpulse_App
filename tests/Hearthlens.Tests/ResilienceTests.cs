using Hearthlens.Core.Logs;
using Hearthlens.Core.Power;

namespace Hearthlens.Tests;

/// <summary>
/// Covers the log plumbing that keeps a long session from freezing the overlay: resuming at the last game
/// without loading the whole file, reporting unread bytes, and replaying the current game on demand.
/// </summary>
public sealed class ResilienceTests
{
    private const string CreateGame = "D 12:00:00.0000000 GameState.DebugPrintPower() - CREATE_GAME";

    [Fact]
    public void LastGame_starts_at_the_last_create_game()
    {
        using var dir = new TempDir();
        var power = Path.Combine(dir.Path, "Power.log");
        File.WriteAllText(power, string.Join('\n', "GAME_ONE", CreateGame, "first game line", CreateGame, "second game line") + "\n");

        var tail = new LogTail("Power.log");
        tail.Reset(dir.Path, TailStart.LastGame);
        var lines = tail.ReadNewLines();

        Assert.Equal(new[] { "D 12:00:00.0000000 GameState.DebugPrintPower() - CREATE_GAME", "second game line" }, lines);
    }

    [Fact]
    public void LastGame_finds_a_marker_split_across_read_blocks()
    {
        using var dir = new TempDir();
        var power = Path.Combine(dir.Path, "Power.log");

        // The tail scans backwards in 1 MB blocks. The suffix size puts the CREATE_GAME marker exactly across
        // the boundary between the last block and the one before it.
        const int megabyte = 1024 * 1024;
        var marker = CreateGame + "\n";
        var prefix = "GAME_ONE\n";
        var suffix = new string('y', megabyte - 10 - 1) + "\n";
        File.WriteAllText(power, prefix + marker + suffix);

        var tail = new LogTail("Power.log");
        tail.Reset(dir.Path, TailStart.LastGame);
        var lines = tail.ReadNewLines();

        Assert.Equal(CreateGame, lines[0]);
        Assert.DoesNotContain("GAME_ONE", lines);
    }

    [Fact]
    public void End_only_reads_what_is_appended_after_reset()
    {
        using var dir = new TempDir();
        var power = Path.Combine(dir.Path, "Power.log");
        File.WriteAllText(power, "old line\n");

        var tail = new LogTail("Power.log");
        tail.Reset(dir.Path, TailStart.End);
        Assert.Empty(tail.ReadNewLines());
        Assert.Equal(0, tail.PendingBytes);

        File.AppendAllText(power, "new line\n");
        Assert.Equal(9, tail.PendingBytes);
        Assert.Equal(new[] { "new line" }, tail.ReadNewLines());
        Assert.Equal(0, tail.PendingBytes);
    }

    [Fact]
    public void PendingBytes_counts_data_that_is_not_read_yet()
    {
        using var dir = new TempDir();
        var power = Path.Combine(dir.Path, "Power.log");
        var tail = new LogTail("Power.log");
        tail.Reset(dir.Path, TailStart.Beginning);

        File.AppendAllText(power, "one\ntwo\n");
        Assert.Equal(8, tail.PendingBytes);
        tail.ReadNewLines();
        Assert.Equal(0, tail.PendingBytes);
    }

    [Fact]
    public void Watching_a_running_session_starts_at_the_last_game_and_replays_it_on_resync()
    {
        using var dir = new TempDir();
        var install = Path.Combine(dir.Path, "Hearthstone");
        var session = Path.Combine(install, "Logs", "Hearthstone_2026_01_01_00_00_00");
        Directory.CreateDirectory(session);
        File.WriteAllText(Path.Combine(session, "Power.log"),
            string.Join('\n', "GAME_ONE", CreateGame, "first game line", CreateGame, "second game line") + "\n");

        var consumer = new RecordingConsumer();
        using var watcher = new LogWatcher(consumer, () => install);
        watcher.Start();

        Assert.True(consumer.WaitForLines(2, TimeSpan.FromSeconds(5)), "the first pass should deliver the last game");
        Assert.Equal(1, consumer.Sessions);
        Assert.DoesNotContain("GAME_ONE", consumer.Snapshot());

        consumer.Clear();
        watcher.RequestResync(replayCurrentGame: true);

        Assert.True(consumer.WaitForLines(2, TimeSpan.FromSeconds(5)), "the resync should replay the last game");
        Assert.Equal(1, consumer.Resyncs);
        Assert.Equal(CreateGame, consumer.Snapshot()[0]);
        Assert.DoesNotContain("GAME_ONE", consumer.Snapshot());
    }

    [Fact]
    public void Resetting_the_parser_drops_the_game_without_ending_it()
    {
        var parser = new PowerParser(new DateOnly(2026, 1, 1));
        parser.Feed(CreateGame);
        Assert.NotNull(parser.Game);

        parser.ResetGame();
        Assert.Null(parser.Game);

        parser.Feed(CreateGame);
        Assert.NotNull(parser.Game);
        Assert.False(parser.Game!.IsComplete);
    }

    private sealed class RecordingConsumer : ILogConsumer
    {
        private readonly List<string> _lines = [];
        private readonly object _gate = new();

        public int Sessions;
        public int Resyncs;

        public void OnSessionStarted(string sessionDir, DateTime sessionStart, bool attachedMidSession)
        {
            lock (_gate)
            {
                Sessions++;
            }
        }

        public void OnLines(string fileName, IReadOnlyList<string> lines, bool catchingUp)
        {
            lock (_gate)
            {
                _lines.AddRange(lines);
            }
        }

        public void OnTick()
        {
        }

        public void OnResync()
        {
            lock (_gate)
            {
                Resyncs++;
            }
        }

        public bool WaitForLines(int count, TimeSpan timeout) =>
            SpinWait.SpinUntil(() =>
            {
                lock (_gate)
                {
                    return _lines.Count >= count;
                }
            }, timeout);

        public List<string> Snapshot()
        {
            lock (_gate)
            {
                return [.. _lines];
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _lines.Clear();
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hearthlens-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
