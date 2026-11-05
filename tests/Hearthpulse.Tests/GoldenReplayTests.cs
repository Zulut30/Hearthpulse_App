using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using static Hearthpulse.Tests.BattlegroundsReplayTests;

namespace Hearthpulse.Tests;

/// <summary>
/// Golden replays (plan R5): each kept Power.log in <c>Fixtures/Replay/Golden</c> is recorded the way the app does it
/// (<see cref="LogBackfill.Parse"/> with a replay store) and must give its <c>.hpr</c> byte for byte; the import filter
/// must give its <c>.import.hpr</c>. A change to what is written fails here first: if it is meant, run the tests once
/// with <c>HEARTHPULSE_GOLDEN_UPDATE=1</c> (the files are written into the source folder) and review the diff.
/// The files are kept with LF endings whatever the checkout does (<c>.gitattributes</c>). The one value that depends on
/// the machine, the game's start (the log's local time written in UTC), is kept as <c>@STARTED@</c>.
/// </summary>
public sealed class GoldenReplayTests : IDisposable
{
    private static readonly DateTime Day = new(2026, 11, 5);

    private readonly string _dir = TestDataFolder.Scratch("hp-golden");

    public void Dispose() => TestDataFolder.TryDelete(_dir);

    private static bool Update => Environment.GetEnvironmentVariable("HEARTHPULSE_GOLDEN_UPDATE") == "1";

    /// <summary>The kept games: a short Constructed win (HPR4), a Battlegrounds game out in 5th (HPR4), a Duos win with the partner's table (HPR5).</summary>
    public static TheoryData<string, string> Games => new()
    {
        { "constructed-win", ReplayFile.Magic },
        { "battlegrounds-5th", ReplayFile.Magic },
        { "duos-partner", ReplayFile.Magic5 },
    };

    [Theory]
    [MemberData(nameof(Games))]
    public void A_kept_log_records_its_golden_replay_byte_for_byte(string name, string magic)
    {
        var folder = Update ? SourceFolder() : Path.Combine(AppContext.BaseDirectory, "Fixtures", "Replay", "Golden");
        var log = Path.Combine(folder, name + ".log");
        if (Update && !File.Exists(log))
        {
            File.WriteAllText(log, string.Join('\n', Bodies(name).Select(b => "D 12:30:00.0000000 GameState." + b)) + "\n");
        }

        var store = new ReplayStore(Path.Combine(_dir, "replays")) { AutoPrune = false };
        var record = Assert.Single(LogBackfill.Parse(log, Day, CardDb.Empty, replays: store), r => r.Replay is not null);
        var text = store.Load(record.Replay)!;
        Assert.StartsWith(magic + "\n", text, StringComparison.Ordinal);
        var clean = ReplayImport.Sanitize(text, CardDb.Empty, out var error);
        Assert.True(clean is not null, error.ToString());

        Same(folder, name + ".hpr", text);
        Same(folder, name + ".import.hpr", clean!);
        // The filter keeps a replay it rebuilt as it is: an imported file imported again is the same file.
        Assert.Equal(clean, ReplayImport.Sanitize(clean!, CardDb.Empty, out _));
    }

    private static void Same(string folder, string file, string written)
    {
        var path = Path.Combine(folder, file);
        var started = new DateTime(Day.Year, Day.Month, Day.Day, 12, 30, 0, DateTimeKind.Local).ToUniversalTime()
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains($"\"startedAt\":\"{started}\"", written, StringComparison.Ordinal);
        var text = written.Replace(started, "@STARTED@", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (Update)
        {
            File.WriteAllBytes(path, bytes);
        }

        var golden = File.ReadAllBytes(path);
        if (golden.AsSpan().SequenceEqual(bytes))
        {
            return;
        }

        // The first line that differs, for the failure message.
        var want = Encoding.UTF8.GetString(golden).Split('\n');
        var got = text.Split('\n');
        var line = Enumerable.Range(0, Math.Max(want.Length, got.Length)).First(i => i >= want.Length || i >= got.Length || want[i] != got[i]);
        Assert.Fail($"{file} differs at line {line + 1}: golden «{(line < want.Length ? want[line] : "<end>")}», written «{(line < got.Length ? got[line] : "<end>")}»");
    }

    private static string[] Bodies(string name) => name switch
    {
        "constructed-win" =>
        [
            // The test setup's real-looking name replaced by the suite's placeholder one (review m11).
            .. ReplayRecorderTests.Setup.Select(line => line.Replace("velox#21276", "Me#1234", StringComparison.Ordinal)),
            "DebugPrintPower() - TAG_CHANGE Entity=Me#1234 tag=PLAYSTATE value=WON ",
            "DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE ",
        ],
        "battlegrounds-5th" => Game5th(),
        "duos-partner" =>
        [
            .. DuosReplayTests.DuosBodies(),
            Player("PLAYSTATE", "WON"),
            Tag(10, "PLAYER_LEADERBOARD_PLACE", 1),
            Game("STATE", "COMPLETE"),
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>The fixtures' folder in the repository (the update writes there, not into the build output).</summary>
    private static string SourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "Hearthpulse.Tests", "Fixtures", "Replay", "Golden");
            if (Directory.Exists(Path.Combine(dir.FullName, "tests", "Hearthpulse.Tests")))
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The repository's tests folder");
    }
}
