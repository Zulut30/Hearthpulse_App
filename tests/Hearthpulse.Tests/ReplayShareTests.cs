using System.IO.Compression;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>The copy to share: through the import filter, without ranks, ratings and the time of play, under a neutral name.</summary>
public sealed class ReplayShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-share-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static string Read(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return reader.ReadToEnd();
    }

    [Fact]
    public void The_copy_has_no_names_ranks_rating_or_time_and_a_neutral_name()
    {
        // The app's own file of a game keyed by its BattleTag, with both ranks and the rating change in it.
        var text = ReplayImportTests.Constructed(header: "{\"kind\":\"constructed\",\"localRank\":\"Legend 1,204\",\"opponentRank\":\"Legend 88\","
            + "\"playerName\":\"Me#1234\",\"summary\":{\"mode\":\"Ranked\",\"format\":\"FT_STANDARD\",\"startedAt\":\"2026-10-28T18:41:07Z\",\"seconds\":420,"
            + "\"heroCardId\":\"HERO_08\",\"opponentName\":\"Rival#4321\",\"ratingBefore\":3100,\"ratingAfter\":3125,\"result\":\"Win\"},\"events\":17}");
        var gameFile = ReplayStore.FileNameFor("20261028184107|me#1234|GT_RANKED");

        var path = ReplayShare.Prepare(text, _dir, CardDb.Empty);

        Assert.NotNull(path);
        var name = Path.GetFileName(path!);
        Assert.Equal($"hearthpulse-replay-{new DateTime(2026, 10, 28, 18, 41, 7, DateTimeKind.Utc).ToLocalTime():yyyyMMdd}-ranked.hpr", name);
        Assert.DoesNotContain(gameFile[15..^4], name, StringComparison.Ordinal);
        var shared = Read(path!);
        foreach (var personal in new[] { "Me#1234", "Rival#4321", "Legend", "1,204", "3100", "3125", "18:41" })
        {
            Assert.DoesNotContain(personal, shared, StringComparison.Ordinal);
        }

        var header = ReplayFile.Read(shared)!.Value.Header;
        Assert.Equal(TimeSpan.Zero, header.Summary!.StartedAt.TimeOfDay);
        Assert.Equal(420, header.Summary.Seconds);
        // What a receiver imports is the same game.
        Assert.Equal(ReplayImportTests.ConstructedEvents.Length, header.Events);
        Assert.NotNull(ReplayImport.Sanitize(shared, CardDb.Empty, out _));
    }

    [Fact]
    public void Two_copies_of_a_day_keep_the_plain_name_in_folders_of_their_own()
    {
        var first = ReplayShare.Prepare(ReplayImportTests.Constructed(), _dir, CardDb.Empty)!;
        var second = ReplayShare.Prepare(ReplayImportTests.Constructed(), _dir, CardDb.Empty)!;

        Assert.Equal(Path.GetFileName(first), Path.GetFileName(second));
        Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
    }

    [Fact]
    public void A_replay_that_fails_the_filter_is_not_shared()
    {
        Assert.Null(ReplayShare.Prepare("HPR4\n{\"kind\":\"constructed\"}\nE 1 L PLAY 1 http://x MINION\n", _dir, CardDb.Empty));
        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Copies_older_than_a_day_are_deleted()
    {
        var old = Path.GetDirectoryName(ReplayShare.Prepare(ReplayImportTests.Constructed(), _dir, CardDb.Empty))!;
        Directory.SetCreationTimeUtc(old, DateTime.UtcNow.AddHours(-25));
        var fresh = Path.GetDirectoryName(ReplayShare.Prepare(ReplayImportTests.Constructed(), _dir, CardDb.Empty))!;

        Assert.Equal(0, ReplayShare.CleanUp(_dir, DateTime.UtcNow));
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void A_battlegrounds_copy_is_named_after_its_mode()
    {
        var text = ReplayFile.WriteBattlegrounds(
            new ReplayHeader { Summary = new ReplaySummary { Mode = "Duos", StartedAt = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc), RatingBefore = 7000, RatingAfter = 7040 } },
            new BattlegroundsReplayData(), ["T 1 30"]);

        var path = ReplayShare.Prepare(text, _dir, CardDb.Empty)!;

        Assert.Matches("^hearthpulse-replay-202610(30|31)-duos\\.hpr$", Path.GetFileName(path));
        Assert.DoesNotContain("7040", Read(path), StringComparison.Ordinal);
    }
}
