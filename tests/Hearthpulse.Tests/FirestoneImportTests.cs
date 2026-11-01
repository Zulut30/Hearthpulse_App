using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Migration;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// Moving over from Firestone: its desktop build's SQLite history and the Overwolf build's older JSON file, both
/// synthetic; an Overwolf-only install keeps its history on the server and says so.
/// </summary>
public sealed class FirestoneImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-firestone-{Guid.NewGuid():N}");
    private readonly HistoryStore _history;

    public FirestoneImportTests()
    {
        Directory.CreateDirectory(_dir);
        _history = new HistoryStore(Path.Combine(_dir, "history.db"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly string[] Records =
    [
        """{"reviewId":"r1","creationTimestamp":1761667800000,"gameMode":"ranked","gameFormat":"standard","result":"won","coinPlay":"coin","playerName":"Player#1000","opponentName":"Rival#2000","playerClass":"mage","opponentClass":"warrior","playerCardId":"HERO_08","opponentCardId":"HERO_01","playerRank":"legend-812","playerDecklist":"AAECAf0EAAA=","playerDeckName":"Spell Mage","gameDurationSeconds":540,"gameDurationTurns":15}""",
        """{"reviewId":"r2","creationTimestamp":1761671400000,"gameMode":"battlegrounds","playerName":"Player#1000","playerCardId":"TB_BaconShop_HERO_50","playerRank":"6100","newPlayerRank":"6132","additionalResult":"3","gameDurationSeconds":1200,"gameDurationTurns":12}""",
        """{"reviewId":"r3","creationTimestamp":1761675000000,"gameMode":"mercenaries-pve","result":"won"}""",
        """{"reviewId":"r4","creationTimestamp":1761678600000,"gameMode":"casual","result":"unknown","playerClass":"mage"}""",
        """{"reviewId":"r5","creationTimestamp":"x","gameMode":"ranked","result":"won"}""",
        """{"reviewId":"r6","creationTimestamp":null,"gameMode":"ranked","result":"won"}""",
        """{"reviewId":"r7","creationTimestamp":99999999999999999999,"gameMode":"ranked","result":"won"}""",
    ];

    private string Json()
    {
        var path = Path.Combine(_dir, "user-match-history.json");
        File.WriteAllText(path, "[" + string.Join(',', Records) + "]");
        return path;
    }

    private string Database()
    {
        var path = Path.Combine(_dir, "FirestoneDB.sqlite");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE matchHistory (reviewId TEXT, data TEXT);";
            create.ExecuteNonQuery();
            foreach (var record in Records)
            {
                using var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO matchHistory (data) VALUES ($d);";
                insert.Parameters.AddWithValue("$d", record);
                insert.ExecuteNonQuery();
            }
        }

        return path;
    }

    [Fact]
    public void The_json_history_imports_constructed_and_battlegrounds_games()
    {
        var result = FirestoneImport.Import(Json(), _history, CardDb.Empty, Path.Combine(_dir, "work"));

        Assert.Equal((2, 0, 5), (result.Added, result.Already, result.Skipped));
        var ranked = Assert.Single(_history.Recent(10), g => g.Mode == "Ranked");
        Assert.Equal("firestone", ranked.Source);
        Assert.Equal(("MAGE", "WARRIOR", "Win", 8, false), (ranked.PlayerClass, ranked.OpponentClass, ranked.Result, ranked.Turns, ranked.WentFirst));
        Assert.Equal("FT_STANDARD", ranked.Format);
        Assert.Equal(812, ranked.RankLegend);
        Assert.Equal("Legend 812", ranked.Rank);
        Assert.Equal("Spell Mage", ranked.DeckName);
        Assert.Equal(TimeSpan.FromSeconds(540), ranked.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1761667800000).LocalDateTime.AddSeconds(-540), ranked.StartedAt);
        var bg = Assert.Single(_history.Recent(10), g => g.IsBattlegrounds);
        Assert.Equal((3, "Win", 6100, 6132), (bg.Placement, bg.Result, bg.RatingBefore ?? 0, bg.RatingAfter ?? 0));
    }

    [Fact]
    public void The_desktop_database_imports_from_a_copy_and_again_adds_nothing()
    {
        var path = Database();
        var before = File.GetLastWriteTimeUtc(path);

        var first = FirestoneImport.Import(path, _history, CardDb.Empty, Path.Combine(_dir, "work"));
        var again = FirestoneImport.Import(path, _history, CardDb.Empty, Path.Combine(_dir, "work"));

        Assert.Equal(2, first.Added);
        Assert.Equal((0, 2), (again.Added, again.Already));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "work")));
    }

    [Fact]
    public void Scan_finds_the_history_or_an_install_whose_history_is_on_the_server()
    {
        var found = FirestoneImport.Scan(database: Path.Combine(_dir, "none.sqlite"), json: Json(), extension: Path.Combine(_dir, "none"))!;
        Assert.Equal((MigrationSource.Firestone, 2, false), (found.Source, found.Games, found.ServerOnly));

        // The database is found, not copied or counted: the import counts it.
        var database = FirestoneImport.Scan(database: Database(), json: Path.Combine(_dir, "none.json"), extension: Path.Combine(_dir, "none"))!;
        Assert.False(database.Counted);
        Assert.True(database.HasWork);
        Assert.False(Directory.Exists(Path.Combine(_dir, "work")));

        var extension = Directory.CreateDirectory(Path.Combine(_dir, "Extensions", "firestone")).FullName;
        var serverOnly = FirestoneImport.Scan(Path.Combine(_dir, "none.sqlite"), Path.Combine(_dir, "none.json"), extension)!;
        Assert.True(serverOnly.ServerOnly);
        Assert.Equal(MigrationErrors.Missing, MigrationScan.Import(serverOnly, _history, CardDb.Empty).Error);

        Assert.Null(FirestoneImport.Scan(Path.Combine(_dir, "none.sqlite"), Path.Combine(_dir, "none.json"), Path.Combine(_dir, "none")));
    }

    [Fact]
    public void A_broken_file_stops_the_import()
    {
        var path = Path.Combine(_dir, "user-match-history.json");
        File.WriteAllText(path, "{\"not\":\"a list\"}");

        Assert.Equal(MigrationErrors.Damaged, FirestoneImport.Import(path, _history, CardDb.Empty, Path.Combine(_dir, "work")).Error);
        File.WriteAllText(path, "[{]");
        Assert.Equal(MigrationErrors.Damaged, FirestoneImport.Import(path, _history, CardDb.Empty, Path.Combine(_dir, "work")).Error);
        Assert.Equal(0, _history.GameCount());
    }
}
