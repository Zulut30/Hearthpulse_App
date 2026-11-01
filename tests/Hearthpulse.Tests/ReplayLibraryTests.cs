using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>The library's folders: the star copies into Saved, Saved outlives the replays trim, Imported keeps the last 500.</summary>
public sealed class ReplayLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-library-{Guid.NewGuid():N}");
    private readonly ReplayLibrary _library;

    public ReplayLibraryTests() =>
        _library = new ReplayLibrary(new ReplayStore(Path.Combine(_dir, "replays")) { MaxTotalFiles = 2 }, Path.Combine(_dir, "saved"), Path.Combine(_dir, "imported"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string SaveGame(int minute) =>
        _library.Recent.Save($"2026102812{minute:00}00|me#1|GT_RANKED", ReplayImportTests.Constructed())!;

    [Fact]
    public void The_library_root_is_inside_the_data_folder_with_HEARTHPULSE_DATA()
    {
        Assert.Equal(Path.Combine(Core.AppPaths.Root, "library"), Core.AppPaths.LibraryRoot);
        Assert.Equal(Path.Combine(Core.AppPaths.Root, "library", "saved"), Core.AppPaths.SavedReplays);
        Assert.Equal(Path.Combine(Core.AppPaths.Root, "library", "imported"), Core.AppPaths.ImportedReplays);
    }

    [Fact]
    public void A_star_copies_and_a_trimmed_game_opens_from_the_copy()
    {
        var name = SaveGame(1);

        Assert.True(_library.Star(ReplayShelf.Recent, name));
        Assert.Contains(name, _library.SavedNames());
        Assert.False(_library.IsLastCopy(name));

        // The replays trim (here 2 files) takes the oldest game; its starred copy stays and still opens.
        SaveGame(2);
        SaveGame(3);
        Assert.False(_library.Recent.Contains(name));
        Assert.True(_library.IsLastCopy(name));
        var text = _library.Load(ReplayShelf.Recent, name);
        Assert.NotNull(text);
        var page = MatchPage.Load(new Core.Data.GameRecord { Key = "20261028120100|me#1|GT_RANKED", Replay = name }, text, CardDb.Empty, null);
        Assert.Null(page.Missing);
    }

    [Fact]
    public void Unstarring_deletes_the_copy_and_keeps_the_game()
    {
        var name = SaveGame(1);
        _library.Star(ReplayShelf.Recent, name);

        Assert.True(_library.Unstar(name));

        Assert.DoesNotContain(name, _library.SavedNames());
        Assert.True(_library.Recent.Contains(name));
    }

    [Fact]
    public void An_imported_game_can_be_starred_and_deleted()
    {
        var path = Path.Combine(_dir, "in.hpr");
        Directory.CreateDirectory(_dir);
        ReplayImportTests.Write(path, ReplayImportTests.Constructed());
        var name = Assert.Single(_library.Import(path, CardDb.Empty).Added);

        Assert.True(_library.Star(ReplayShelf.Imported, name));
        Assert.True(_library.DeleteImported(name));

        Assert.False(_library.Imported.Contains(name));
        Assert.True(_library.IsLastCopy(name));
        Assert.NotNull(_library.Load(ReplayShelf.Imported, name));
    }

    [Fact]
    public void Imported_keeps_the_last_500_games()
    {
        var store = _library.Imported;
        for (var i = 0; i < ReplayLibrary.MaxImportedFiles + 2; i++)
        {
            var at = new DateTime(2026, 1, 1).AddMinutes(i);
            Assert.True(store.SaveAs($"{at:yyyyMMddHHmmss}-{i:x12}.hpr", "HPR4\n{\"kind\":\"constructed\"}\nT 1 L 0\n"));
        }

        var names = store.Names();
        Assert.Equal(ReplayLibrary.MaxImportedFiles, names.Count);
        Assert.DoesNotContain(names, n => n.StartsWith("20260101000000", StringComparison.Ordinal) || n.StartsWith("20260101000100", StringComparison.Ordinal));
        Assert.Equal(200L * 1024 * 1024, ReplayLibrary.MaxImportedBytes);
    }

    [Fact]
    public void An_imported_game_opens_with_you_and_opponent_for_names()
    {
        var path = Path.Combine(_dir, "in.hpr");
        Directory.CreateDirectory(_dir);
        ReplayImportTests.Write(path, ReplayImportTests.Constructed());
        var name = Assert.Single(_library.Import(path, CardDb.Empty).Added);

        var page = MatchPage.LoadFile(_library.Load(ReplayShelf.Imported, name), name, CardDb.Empty, null);

        Assert.Null(page.Missing);
        Assert.Equal(Core.Localization.Loc.T("Match.You"), page.Game.PlayerName);
        Assert.Equal(Core.Localization.Loc.T("Match.Opponent"), page.Game.OpponentName);
        Assert.StartsWith(MatchPage.FileKeyPrefix, page.Game.Key, StringComparison.Ordinal);
        Assert.Equal("Win", page.Game.Result);
        Assert.Equal(string.Empty, page.Header.Local.Rank);
        Assert.True(page.FrameCount > 0);
    }

    [Fact]
    public void Read_header_reads_only_the_first_two_lines()
    {
        var name = SaveGame(1);

        var header = _library.Recent.ReadHeader(name);

        Assert.NotNull(header);
        Assert.Equal("HERO_08", header!.Summary!.HeroCardId);
        Assert.Equal("Legend 1,204", header.LocalRank);
        Assert.Same(_library.Header(ReplayShelf.Recent, name), _library.Header(ReplayShelf.Recent, name));

        // A header line past the limit, a damaged file and a missing one read as nothing.
        var big = Path.Combine(_library.Recent.Directory, "20261028130000-aaaaaaaaaaaa.hpr");
        using (var file = File.Create(big))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.ASCII.GetBytes("HPR4\n{\"kind\":\"" + new string('x', 70_000) + "\"}\n"));
        }

        File.WriteAllText(Path.Combine(_library.Recent.Directory, "20261028140000-bbbbbbbbbbbb.hpr"), "not gzip");
        Assert.Null(_library.Recent.ReadHeader(Path.GetFileName(big)));
        Assert.Null(_library.Recent.ReadHeader("20261028140000-bbbbbbbbbbbb.hpr"));
        Assert.Null(_library.Recent.ReadHeader("20261028150000-cccccccccccc.hpr"));
        Assert.Null(_library.Recent.ReadHeader("..\\escape.hpr"));
    }
}
