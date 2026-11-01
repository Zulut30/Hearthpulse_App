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

        var page = MatchPage.LoadFile(_library.Load(ReplayShelf.Imported, name), name, CardDb.Empty, _ => null);

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

    [Fact]
    public void A_file_put_in_a_library_folder_by_hand_opens_through_the_filter()
    {
        // Ranks in the header and a card id in the opponent's hand: neither shows on the page.
        string[] events = ["T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30", "E 5 O HAND 1 CS2_182 MINION ATK=4", "T 2 O 5"];
        var name = "20261028120000-handmade0001.hpr";
        Directory.CreateDirectory(_library.Imported.Directory);
        ReplayImportTests.Write(Path.Combine(_library.Imported.Directory, name), ReplayImportTests.Constructed(events));

        var page = MatchPage.LoadFile(_library.Load(ReplayShelf.Imported, name), name, CardDb.Empty, _ => null);

        Assert.Null(page.Missing);
        Assert.Equal(string.Empty, page.Header.Local.Rank);
        Assert.Equal(string.Empty, page.Header.Opponent.Rank);
        Assert.Null(page.Game.RatingBefore);
        var hand = page.Step(page.FrameCount - 1, ReplayPlayer.Local)!.Top.Hand;
        var card = Assert.Single(hand);
        Assert.True(card.CardBack);
        Assert.NotEqual("CS2_182", card.CardId);

        // A file the filter refuses opens as unreadable, and nothing of it shows.
        var bad = "20261028130000-handmade0002.hpr";
        ReplayImportTests.Write(Path.Combine(_library.Imported.Directory, bad), "HPR4\n{\"kind\":\"constructed\",\"localRank\":\"Legend 7\"}\nE 1 L PLAY 1 http://evil MINION\n");
        var refused = MatchPage.LoadFile(_library.Load(ReplayShelf.Imported, bad), bad, CardDb.Empty, _ => null);
        Assert.Equal(Core.Localization.Loc.T("Match.Missing.Unreadable"), refused.Missing);
        Assert.Equal(string.Empty, refused.Header.Local.Rank);
    }

    [Fact]
    public void A_gzip_bomb_in_a_library_folder_is_not_read()
    {
        var name = "20261028120000-bomb00000001.hpr";
        Directory.CreateDirectory(_library.Saved.Directory);
        using (var file = File.Create(Path.Combine(_library.Saved.Directory, name)))
        using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize))
        {
            gzip.Write(Encoding.ASCII.GetBytes("HPR4\n{\"kind\":\"constructed\"}\n"));
            var chunk = Encoding.ASCII.GetBytes(new string('B', 1024 * 1024));
            for (var i = 0; i < 33; i++)
            {
                gzip.Write(chunk);
            }
        }

        Assert.Null(_library.Load(ReplayShelf.Saved, name));
    }

    [Fact]
    public void A_header_is_read_again_once_its_file_is_gone_or_written_again()
    {
        var name = SaveGame(1);
        Assert.NotNull(_library.Header(ReplayShelf.Recent, name));

        _library.Recent.Save("20261028120100|me#1|GT_RANKED", ReplayImportTests.Constructed(summary: ReplayImportTests.ConstructedSummary with { HeroCardId = "HERO_09" }), overwrite: true);
        File.SetLastWriteTimeUtc(Path.Combine(_library.Recent.Directory, name), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("HERO_09", _library.Header(ReplayShelf.Recent, name)!.Summary!.HeroCardId);

        _library.Recent.Delete(name);
        Assert.Null(_library.Header(ReplayShelf.Recent, name));
    }

    [Fact]
    public void A_star_replaces_a_copy_older_than_its_source()
    {
        // The part saved when the client left, starred; then the whole game after the reconnect.
        var name = _library.Recent.Save("20261028120100|me#1|GT_RANKED", ReplayImportTests.Constructed(["T 1 L 0"]))!;
        _library.Star(ReplayShelf.Recent, name);
        File.SetLastWriteTimeUtc(Path.Combine(_library.Saved.Directory, name), DateTime.UtcNow.AddMinutes(-5));
        _library.Recent.Save("20261028120100|me#1|GT_RANKED", ReplayImportTests.Constructed(), overwrite: true);

        Assert.True(_library.Star(ReplayShelf.Recent, name));

        Assert.Equal(_library.Recent.Load(name), _library.Saved.Load(name));
    }

    [Fact]
    public void Imported_trims_by_when_files_were_written_not_by_the_games_date()
    {
        var store = new ReplayStore(Path.Combine(_dir, "trim")) { MaxTotalFiles = 2, PruneByWriteTime = true };
        const string text = "HPR4\n{\"kind\":\"constructed\"}\nT 1 L 0\n";
        store.SaveAs("20261001000000-aaaaaaaaaaaa.hpr", text);
        File.SetLastWriteTimeUtc(Path.Combine(store.Directory, "20261001000000-aaaaaaaaaaaa.hpr"), DateTime.UtcNow.AddMinutes(-10));
        store.SaveAs("20261002000000-bbbbbbbbbbbb.hpr", text);
        File.SetLastWriteTimeUtc(Path.Combine(store.Directory, "20261002000000-bbbbbbbbbbbb.hpr"), DateTime.UtcNow.AddMinutes(-5));

        // An old log's game, imported last: it stays, the file written first goes.
        store.SaveAs("20200101000000-cccccccccccc.hpr", text);

        Assert.Equal(["20261002000000-bbbbbbbbbbbb.hpr", "20200101000000-cccccccccccc.hpr"], store.Names());
        Assert.True(_library.Imported.PruneByWriteTime);
    }
}
