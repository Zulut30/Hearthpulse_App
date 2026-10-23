using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// The My Stats fields (combats, offered heroes, anomaly, rank stars, first player, deck id) are columns added to
/// existing history files on open; rows saved before them read back with the new fields empty.
/// </summary>
public sealed class HistoryMigrationTests(ITestOutputHelper output)
{
    // The games table exactly as the first schema created it.
    private const string FirstSchema = """
        CREATE TABLE games (
            id INTEGER PRIMARY KEY AUTOINCREMENT, game_key TEXT NOT NULL UNIQUE, started_at TEXT NOT NULL,
            ended_at TEXT NOT NULL, mode TEXT NOT NULL, game_type TEXT NOT NULL, format TEXT NOT NULL,
            player_name TEXT NOT NULL, opponent_name TEXT NOT NULL, hero TEXT NOT NULL, opponent_hero TEXT NOT NULL,
            player_class TEXT NOT NULL, opponent_class TEXT NOT NULL, result TEXT NOT NULL, placement INTEGER NOT NULL,
            rating_before INTEGER NULL, rating_after INTEGER NULL, turns INTEGER NOT NULL, deck_name TEXT NULL,
            deck_code TEXT NULL, tavern_tier INTEGER NOT NULL, final_board TEXT NULL, races TEXT NULL, rank TEXT NULL,
            source TEXT NOT NULL);
        INSERT INTO games (game_key, started_at, ended_at, mode, game_type, format, player_name, opponent_name, hero,
            opponent_hero, player_class, opponent_class, result, placement, rating_before, rating_after, turns, deck_name,
            deck_code, tavern_tier, final_board, races, rank, source)
        VALUES ('old', '2026-10-01T10:00:00.0000000', '2026-10-01T10:12:00.0000000', 'Ranked', 'GT_RANKED', 'FT_STANDARD',
            'Me#1', 'You#2', 'HERO_08', 'HERO_02', 'MAGE', 'SHAMAN', 'Win', 0, NULL, NULL, 9, 'Mage', NULL, 0, NULL, NULL,
            'Gold 4', 'live');
        """;

    private static string TempDb() => Path.Combine(Path.GetTempPath(), $"hp-migrate-{Guid.NewGuid():N}.db");

    [Fact]
    public void An_old_history_file_gets_the_new_columns_and_keeps_its_games()
    {
        var path = TempDb();
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = FirstSchema;
            command.ExecuteNonQuery();
        }

        var store = new HistoryStore(path);
        var old = Assert.Single(store.Recent());
        Assert.Equal("Gold 4", old.Rank);
        Assert.Null(old.CombatResults);
        Assert.Null(old.OfferedHeroes);
        Assert.Null(old.AnomalyCardId);
        Assert.Null(old.RankStarLevel);
        Assert.Null(old.RankLegend);
        Assert.Null(old.WentFirst);
        Assert.Null(old.DeckId);

        // Opening again is a no-op (the columns are there now).
        store = new HistoryStore(path);
        Assert.Single(store.Recent());
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public void New_fields_round_trip()
    {
        var path = TempDb();
        var store = new HistoryStore(path);
        var at = new DateTime(2026, 10, 20, 21, 0, 0);
        Assert.True(store.Add(new GameRecord
        {
            Key = "bg",
            StartedAt = at,
            EndedAt = at.AddMinutes(19),
            Mode = "Battlegrounds",
            HeroCardId = "TB_BaconShop_HERO_72",
            Placement = 2,
            RatingBefore = 6400,
            RatingAfter = 6461,
            CombatResults = """[{"turn":1,"result":"Win","opponent":"BG22_HERO_305","taken":0,"dealt":3}]""",
            OfferedHeroes = "TB_BaconShop_HERO_72,BG22_HERO_305",
            AnomalyCardId = "BG27_Anomaly_100",
        }));
        Assert.True(store.Add(new GameRecord
        {
            Key = "ranked",
            StartedAt = at.AddHours(1),
            EndedAt = at.AddHours(1).AddMinutes(8),
            Mode = "Ranked",
            Result = "Loss",
            RankStarLevel = 34,
            WentFirst = false,
            DeckId = 2774348185,
        }));

        var games = store.Recent().ToDictionary(g => g.Key);
        Assert.Equal("TB_BaconShop_HERO_72,BG22_HERO_305", games["bg"].OfferedHeroes);
        Assert.Equal("BG27_Anomaly_100", games["bg"].AnomalyCardId);
        Assert.Contains("\"result\":\"Win\"", games["bg"].CombatResults);
        Assert.Null(games["bg"].WentFirst);
        Assert.Equal(34, games["ranked"].RankStarLevel);
        Assert.Null(games["ranked"].RankLegend);
        Assert.False(games["ranked"].WentFirst);
        Assert.Equal(2774348185, games["ranked"].DeckId);
        SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    /// <summary>
    /// Every kept Constructed game says who went first (FIRST_PLAYER on a player entity; checked by hand against the
    /// opening coin's controller in the October 2026 logs). No-op without the client's logs.
    /// </summary>
    [Fact]
    public void Kept_constructed_logs_say_who_went_first()
    {
        const string logRoot = @"D:\Hearthstone\Logs";
        if (!Directory.Exists(logRoot))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(logRoot, "Hearthstone_*"))
        {
            var start = Core.Hearthstone.HearthstoneInstall.SessionStart(dir);
            foreach (var file in new[] { "Power_old.log", "Power.log" }.Select(n => Path.Combine(dir, n)).Where(File.Exists))
            {
                if (start is null)
                {
                    continue;
                }

                foreach (var record in LogBackfill.Parse(file, DateOnly.FromDateTime(start.Value), CardDb.Empty).Where(r => !r.IsBattlegrounds))
                {
                    output.WriteLine($"{record.StartedAt:g} {record.Mode} {record.Result} first={record.WentFirst}");
                    Assert.NotNull(record.WentFirst);
                }
            }
        }
    }
}
