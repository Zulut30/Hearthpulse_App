using System.IO.Compression;
using System.Text;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// Replay files and history rows stay in step: the left part of a reconnected game gives its file to the reconnect or
/// takes it away; files of the first format and crash leftovers are deleted; old games go first.
/// </summary>
public sealed class ReplayHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-replay-history-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly ReplayStore _store;
    private readonly HistoryStore _history;

    public ReplayHistoryTests()
    {
        Directory.CreateDirectory(_dir);
        _db = Path.Combine(_dir, "history.db");
        _store = new ReplayStore(Path.Combine(_dir, "replays"));
        _history = new HistoryStore(_db) { Replays = _store };
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly DateTime At = new(2026, 10, 23, 20, 0, 0);

    private GameRecord Game(string key, DateTime start, string result, bool withReplay)
    {
        var game = new GameRecord
        {
            Key = key, StartedAt = start, EndedAt = start.AddMinutes(4), Mode = "Ranked", GameType = "GT_RANKED", PlayerName = "Me#1",
            OpponentName = "You#2", HeroCardId = "HERO_08", OpponentHeroCardId = "HERO_02", Result = result,
        };
        return withReplay ? game with { Replay = _store.Save(key, ReplayFile.Write(new ReplayHeader(), [$"T 1 L 0 {key}"])) } : game;
    }

    private bool Exists(string? name) => name is not null && File.Exists(Path.Combine(_store.Directory, name));

    [Fact]
    public void The_reconnect_takes_over_the_left_parts_replay_when_it_has_none()
    {
        var left = Game("20261023200000|Me#1|GT_RANKED", At, GameRecord.AbandonedResult, withReplay: true);
        Assert.True(_history.Add(left));

        Assert.True(_history.Add(Game("20261023200900|Me#1|GT_RANKED", At.AddMinutes(9), "Win", withReplay: false)));

        var game = Assert.Single(_history.Recent());
        Assert.Equal(left.Replay, game.Replay);
        Assert.True(Exists(left.Replay));
    }

    [Fact]
    public void The_left_parts_replay_is_deleted_when_the_reconnect_has_its_own()
    {
        var left = Game("20261023200000|Me#1|GT_RANKED", At, GameRecord.AbandonedResult, withReplay: true);
        Assert.True(_history.Add(left));
        var reconnect = Game("20261023200900|Me#1|GT_RANKED", At.AddMinutes(9), "Win", withReplay: true);

        Assert.True(_history.Add(reconnect));

        Assert.Equal(reconnect.Replay, Assert.Single(_history.Recent()).Replay);
        Assert.False(Exists(left.Replay));
        Assert.True(Exists(reconnect.Replay));
    }

    [Fact]
    public void A_left_part_saved_after_its_reconnect_takes_its_file_away()
    {
        Assert.True(_history.Add(Game("20261023200900|Me#1|GT_RANKED", At.AddMinutes(9), "Win", withReplay: true)));
        var late = Game("20261023200000|Me#1|GT_RANKED", At, GameRecord.AbandonedResult, withReplay: true);

        Assert.False(_history.Add(late));

        Assert.False(Exists(late.Replay));
        Assert.Single(_history.Recent());
    }

    private void WriteRaw(string name, string text)
    {
        Directory.CreateDirectory(_store.Directory);
        using var file = File.Create(Path.Combine(_store.Directory, name));
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public void A_first_format_file_is_deleted_when_read_and_replaced_when_saved_again()
    {
        var name = ReplayStore.FileNameFor("20261023120000|a|GT_RANKED");
        WriteRaw(name, "HPR1\n{}\nE 1 O DECK 0 - -\n");

        Assert.Null(_store.Load(name));
        Assert.False(Exists(name));

        WriteRaw(name, "HPR1\n{}\nE 1 O DECK 0 - -\n");
        Assert.Equal(name, _store.Save("20261023120000|a|GT_RANKED", ReplayFile.Write(new ReplayHeader(), ["D O 30"])));
        Assert.StartsWith(ReplayFile.Magic, _store.Load(name));
    }

    [Fact]
    public void The_first_trim_deletes_first_format_files_and_crash_leftovers()
    {
        WriteRaw("20261023120000-aaaaaaaaaaaa.hpr", "HPR1\n{}\n");
        WriteRaw("20261023130000-bbbbbbbbbbbb.hpr", ReplayFile.Write(new ReplayHeader(), ["D O 30"]));
        var stale = Path.Combine(_store.Directory, "20261023140000-cccccccccccc.hpr.0123.tmp");
        File.WriteAllText(stale, "partial");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        _store.Prune();

        Assert.False(Exists("20261023120000-aaaaaaaaaaaa.hpr"));
        Assert.True(Exists("20261023130000-bbbbbbbbbbbb.hpr"));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void A_replay_named_without_a_start_time_is_trimmed_by_its_age()
    {
        var store = new ReplayStore(_store.Directory) { MaxTotalFiles = 2 };
        var odd = store.Save("not-a-time|x", ReplayFile.Write(new ReplayHeader(), ["T 1 L 0"]))!;
        Assert.StartsWith("game-", odd);
        File.SetLastWriteTimeUtc(Path.Combine(store.Directory, odd), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        store.Save("20261023120000|a|GT_RANKED", ReplayFile.Write(new ReplayHeader(), ["T 1 L 0"]));
        store.Save("20261023130000|a|GT_RANKED", ReplayFile.Write(new ReplayHeader(), ["T 1 L 0"]));

        Assert.False(Exists(odd));
    }

    [Fact]
    public void A_save_does_not_trip_over_a_temp_file_of_another_writer()
    {
        var key = "20261023120000|a|GT_RANKED";
        Directory.CreateDirectory(_store.Directory);
        File.WriteAllText(Path.Combine(_store.Directory, ReplayStore.FileNameFor(key) + ".tmp"), "held by another writer");

        Assert.NotNull(_store.Save(key, ReplayFile.Write(new ReplayHeader(), ["T 1 L 0"])));
    }
}
