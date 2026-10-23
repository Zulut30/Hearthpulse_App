using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// The log watcher hands Power.log over in whatever chunks Hearthstone has written, so a batch can end in the middle of
/// a FULL_ENTITY's tag lines. The game state must not depend on where the batches end.
/// </summary>
public sealed class PowerBatchTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(40)]
    public void Small_batches_give_the_same_game_state_as_one_batch(int batch)
    {
        var lines = BattlegroundsFixtureTests.Lines().ToList();
        var whole = Replay(lines, lines.Count);
        var split = Replay(lines, batch);
        Assert.Equal(whole.Count, split.Count);
        Assert.Empty(whole.Except(split));
    }

    [Fact]
    public void A_block_cut_by_a_batch_continues_in_the_next_one_and_is_completed_once_the_log_is_quiet()
    {
        var lines = BattlegroundsFixtureTests.Lines().ToList();
        // Cut right after the first FULL_ENTITY header and two of its tag lines; ZONE comes later in the block.
        var header = lines.FindIndex(l => l.Contains("FULL_ENTITY - Creating", StringComparison.Ordinal));
        var end = lines.FindIndex(header + 1, l => !l.Contains(" tag=", StringComparison.Ordinal) || l.Contains("TAG_CHANGE", StringComparison.Ordinal));
        using var run = new Run();
        run.Session.OnLines("Power.log", lines.Take(header + 3).ToList(), catchingUp: false);
        run.Tick(TimeSpan.FromMilliseconds(100));
        Assert.True(run.Session.ParserHasPending);

        run.Session.OnLines("Power.log", lines.Skip(header + 3).Take(end - header - 3).ToList(), catchingUp: false);
        run.Tick(TimeSpan.FromMilliseconds(100));
        var entity = run.Session.Game!.Entities.Values.MaxBy(e => e.Id)!;
        Assert.Contains(entity.Tags, t => t.Key == "ZONE");
        Assert.True(run.Session.ParserHasPending);

        run.Tick(TimeSpan.FromMilliseconds(100));
        Assert.False(run.Session.ParserHasPending);
    }

    [Fact]
    public void Lines_the_parser_cannot_read_are_counted_and_the_first_of_each_kind_is_logged_with_its_stack()
    {
        var lines = BattlegroundsFixtureTests.Lines().ToList();
        using var run = new Run();
        run.Session.OnLines("Power.log", lines.Take(300).ToList(), catchingUp: false);
        var bad = "D 02:05:15.4431341 GameState.DebugPrintPower() - FULL_ENTITY - Creating ID=99999999999 CardID=BG_TEST";
        run.Session.OnLines("Power.log", [bad, bad, .. lines.Skip(300)], catchingUp: false);
        run.Tick(TimeSpan.FromSeconds(1));

        var errors = run.Session.Current.ParseErrors;
        Assert.NotNull(errors);
        Assert.Equal(2, errors.Total);
        Assert.Equal("OverflowException ×2", errors.ByType);
        // The rest of the batch was still read.
        Assert.Equal(7, run.Session.Current.Battlegrounds!.Opponents.Count);
        var log = File.ReadAllText(Hearthpulse.Core.AppPaths.LogFile);
        Assert.Contains("(first OverflowException): " + bad[..60], log);
        Assert.Contains("at Hearthpulse.Core.Power.PowerParser", log);
    }

    private static HashSet<string> Replay(List<string> lines, int batch)
    {
        using var run = new Run();
        for (var i = 0; i < lines.Count; i += batch)
        {
            run.Session.OnLines("Power.log", lines.GetRange(i, Math.Min(batch, lines.Count - i)), catchingUp: false);
            run.Tick(TimeSpan.FromMilliseconds(100));
        }

        run.Tick(TimeSpan.FromSeconds(1));
        return run.Session.Game!.Entities.Values.SelectMany(e => e.Tags.Select(t => $"{e.Id}:{e.CardId}:{t.Key}={t.Value}")).ToHashSet();
    }

    private sealed class Run : IDisposable
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-batch-{Guid.NewGuid():N}.db");
        private readonly HearthstoneMemory _memory = new();
        private DateTime _now = new(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);

        public Run()
        {
            Session = new GameSession(new HistoryStore(_db), _memory) { Clock = () => _now, IsHearthstoneRunning = () => true };
            Session.OnSessionStarted("Hearthstone_2026_10_23_02_01_43", new DateTime(2026, 10, 23, 2, 1, 43), attachedMidSession: false);
        }

        public GameSession Session { get; }

        public void Tick(TimeSpan by)
        {
            _now += by;
            Session.OnTick();
        }

        public void Dispose()
        {
            _memory.Dispose();
            SqliteConnection.ClearAllPools();
            File.Delete(_db);
        }
    }
}
