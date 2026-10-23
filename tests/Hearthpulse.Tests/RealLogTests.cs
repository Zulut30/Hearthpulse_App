using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// Runs the parser over Power logs kept by the local Hearthstone client. Tests are no-ops on
/// machines without those logs.
/// </summary>
public sealed class RealLogTests(ITestOutputHelper output)
{
    internal const string LogRoot = @"D:\Hearthstone\Logs";

    private static CardDb Cards { get; } = LoadCards();

    private static CardDb LoadCards()
    {
        var path = Path.Combine(Path.GetTempPath(), "hl", "cards.json");
        if (!File.Exists(path))
        {
            return CardDb.Empty;
        }

        using var stream = File.OpenRead(path);
        return CardDb.Parse("itIT", stream);
    }

    private static IEnumerable<(string Path, DateOnly Date)> PowerLogs()
    {
        if (!Directory.Exists(LogRoot))
        {
            yield break;
        }

        foreach (var dir in Directory.EnumerateDirectories(LogRoot, "Hearthstone_*"))
        {
            var start = Core.Hearthstone.HearthstoneInstall.SessionStart(dir);
            foreach (var name in new[] { "Power_old.log", "Power.log" })
            {
                var file = Path.Combine(dir, name);
                if (start is not null && File.Exists(file))
                {
                    yield return (file, DateOnly.FromDateTime(start.Value));
                }
            }
        }
    }

    [RealLogsFact]
    public void Backfill_parses_every_kept_log_into_sane_records()
    {
        var total = 0;
        foreach (var (path, date) in PowerLogs())
        {
            var records = LogBackfill.Parse(path, date, Cards);
            foreach (var r in records)
            {
                output.WriteLine($"{r.StartedAt:g} {r.Mode,-13} {r.PlayerName,-14} hero={Cards.NameOf(r.HeroCardId),-22} place={r.Placement} result={r.Result} turns={r.Turns} tier={r.TavernTier} vs={r.OpponentClass}");
                Assert.False(string.IsNullOrEmpty(r.PlayerName));
                Assert.False(string.IsNullOrEmpty(r.HeroCardId));
                if (r.IsBattlegrounds)
                {
                    Assert.InRange(r.Placement, 1, 8);
                    Assert.InRange(r.TavernTier, 1, 7);
                    Assert.True(r.Turns >= 1);
                }
            }

            total += records.Count;
        }

        output.WriteLine($"{total} games");
    }

    [RealLogsFact]
    public void Kept_battlegrounds_log_tracks_lobby_boards_and_combats()
    {
        // Machine-dependent smoke run over the newest kept Battlegrounds log; the deterministic check is
        // BattlegroundsFixtureTests, which runs the same code on a fragment kept in the repository.
        var log = PowerLogs().LastOrDefault(l => ReadShared(l.Path).Take(20_000).Any(x => x.Contains("GameType=GT_BATTLEGROUNDS")));
        if (log.Path is null)
        {
            output.WriteLine("No Battlegrounds game in the kept logs.");
            return;
        }

        var replay = BattlegroundsReplay.Run(ReadShared(log.Path), log.Date, Cards, every: 10);
        Assert.NotNull(replay.HeroPick);
        Assert.InRange(replay.HeroPick!.HeroOptions.Count, 2, 4);
        Assert.NotNull(replay.Best);
        replay.Print(output, Cards);
        Assert.Equal(7, replay.Best!.Opponents.Count);
    }

    /// <summary>The newest Power.log may be the one Hearthstone is writing right now: read it without locking it.</summary>
    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

/// <summary>A fact that is skipped on machines without the Hearthstone client's kept logs.</summary>
public sealed class RealLogsFactAttribute : FactAttribute
{
    public RealLogsFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(RealLogTests.LogRoot))
        {
            Skip = $"No kept Hearthstone logs ({RealLogTests.LogRoot}) on this machine.";
        }
    }
}
