using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

[CollectionDefinition(nameof(ReplayImportRealLogs), DisableParallelization = true)]
public sealed class ReplayImportRealLogs;

/// <summary>
/// The import filter over every game of the kept logs: heavy (it records them all), so it runs alone, not alongside
/// the timing tests of the parser.
/// </summary>
[Collection(nameof(ReplayImportRealLogs))]
public sealed class ReplayImportRealLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-import-real-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    /// <summary>Every replay the app records from the kept logs passes the filter with its event lines unchanged.</summary>
    [RealLogsFact]
    public void Kept_logs_replays_pass_the_filter_unchanged()
    {
        var store = new ReplayStore(Path.Combine(_dir, "real")) { AutoPrune = false };
        var games = 0;
        foreach (var dir in Directory.EnumerateDirectories(RealLogTests.LogRoot, "Hearthstone_*"))
        {
            if (Core.Hearthstone.HearthstoneInstall.SessionStart(dir) is not { } start)
            {
                continue;
            }

            foreach (var file in new[] { "Power_old.log", "Power.log" }.Select(n => Path.Combine(dir, n)).Where(File.Exists))
            {
                foreach (var record in Core.Data.LogBackfill.Parse(file, start, CardDb.Empty, replays: store))
                {
                    var text = store.Load(record.Replay)!;
                    var clean = ReplayImport.Sanitize(text, CardDb.Empty, out var error);
                    Assert.True(clean is not null, $"{record.StartedAt:g} {record.Mode}: {error}");
                    var (header, body) = ReplayFile.Read(text)!.Value;
                    var (cleanHeader, cleanBody) = ReplayFile.Read(clean!)!.Value;
                    Assert.Equal(body.Take(header.Events), cleanBody.Take(cleanHeader.Events));
                    games++;
                }
            }
        }

        Assert.True(games > 0);
    }
}
