using System.Globalization;
using Hearthpulse.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

/// <summary>Local game history in SQLite.</summary>
public sealed partial class HistoryStore
{
    private readonly string _connectionString;
    private readonly Lock _gate = new();

    public HistoryStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();
        try
        {
            CreateSchema();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase && File.Exists(path))
        {
            // A file that is not a database (a broken disk, a truncated copy) stopped the app at every start: it is kept
            // next to the new one, and the history starts again.
            SqliteConnection.ClearAllPools();
            var aside = string.Create(CultureInfo.InvariantCulture, $"{path}.broken-{DateTime.UtcNow:yyyyMMddHHmmss}");
            File.Move(path, aside);
            foreach (var journal in new[] { "-wal", "-shm", "-journal" }.Where(j => File.Exists(path + j)))
            {
                File.Move(path + journal, aside + journal);
            }

            Diag.Error($"The history could not be read and was put aside as {Path.GetFileName(aside)}; a new one is started", ex);
            CreateSchema();
        }
    }

    /// <summary>
    /// The open connections that keep test histories alive (<see cref="InMemory"/>): a shared in-memory database lives
    /// while one connection to it is open, and pooled ones may be cleared at any time.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentBag<SqliteConnection> MemoryKeepers = [];

    private HistoryStore(SqliteConnectionStringBuilder builder)
    {
        _connectionString = builder.ToString();
        var keeper = new SqliteConnection(_connectionString);
        keeper.Open();
        MemoryKeepers.Add(keeper);
        CreateSchema();
    }

    /// <summary>
    /// A history in memory, for tests that need a <see cref="Tracking.GameSession"/> but no file: no disk, so no file
    /// locks or "disk I/O error" from a scanner on the temp folder. It lives as long as the test process.
    /// </summary>
    internal static HistoryStore InMemory() => new(new SqliteConnectionStringBuilder
    {
        DataSource = "hp-test-" + Guid.NewGuid().ToString("N"),
        Mode = SqliteOpenMode.Memory,
        Cache = SqliteCacheMode.Shared,
    });

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private void CreateSchema()
    {
        using var connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS games (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                game_key TEXT NOT NULL UNIQUE,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                mode TEXT NOT NULL,
                game_type TEXT NOT NULL,
                format TEXT NOT NULL,
                player_name TEXT NOT NULL,
                opponent_name TEXT NOT NULL,
                hero TEXT NOT NULL,
                opponent_hero TEXT NOT NULL,
                player_class TEXT NOT NULL,
                opponent_class TEXT NOT NULL,
                result TEXT NOT NULL,
                placement INTEGER NOT NULL,
                rating_before INTEGER NULL,
                rating_after INTEGER NULL,
                turns INTEGER NOT NULL,
                deck_name TEXT NULL,
                deck_code TEXT NULL,
                tavern_tier INTEGER NOT NULL,
                final_board TEXT NULL,
                races TEXT NULL,
                rank TEXT NULL,
                source TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_games_started ON games(started_at);
            CREATE INDEX IF NOT EXISTS ix_games_mode ON games(mode);
            """);
        AddMissingColumns(connection);
        CreateFamilyTables(connection);
        CreateSavedDeckTables(connection);
        CreateArenaRewardTables(connection);
        CreateBookmarkTables(connection);
    }

    /// <summary>Columns added after the first schema, with their SQL type. Older databases get them on open.</summary>
    private static readonly (string Name, string Type)[] AddedColumns =
    [
        ("combat_results", "TEXT NULL"),
        ("offered_heroes", "TEXT NULL"),
        ("anomaly", "TEXT NULL"),
        ("rank_star_level", "INTEGER NULL"),
        ("rank_legend", "INTEGER NULL"),
        ("went_first", "INTEGER NULL"),
        ("deck_id", "INTEGER NULL"),
        ("mulligan", "TEXT NULL"),
        ("cards_drawn", "TEXT NULL"),
        ("cards_played", "TEXT NULL"),
        ("opponent_cards", "TEXT NULL"),
        ("opponent_archetype", "TEXT NULL"),
        ("replay", "TEXT NULL"),
        ("combat_odds", "TEXT NULL"),
        ("scenario_id", "INTEGER NULL"),
    ];

    /// <summary>The deck page's per-game card columns: large JSON, read only for the games of an open deck (<see cref="WithCards"/>).</summary>
    private static readonly HashSet<string> CardColumns = ["mulligan", "cards_drawn", "cards_played", "opponent_cards", "opponent_archetype"];

    private static readonly string[] FirstColumns =
    [
        "id", "game_key", "started_at", "ended_at", "mode", "game_type", "format", "player_name", "opponent_name", "hero", "opponent_hero",
        "player_class", "opponent_class", "result", "placement", "rating_before", "rating_after", "turns", "deck_name", "deck_code",
        "tavern_tier", "final_board", "races", "rank", "source",
    ];

    /// <summary>Every column but the card columns (read back as NULL): what lists, stats and the overlay need.</summary>
    private static readonly string ListSelect = "SELECT "
        + string.Join(", ", FirstColumns.Concat(AddedColumns.Select(c => c.Name)).Select(c => CardColumns.Contains(c) ? $"NULL AS {c}" : c))
        + " FROM games";

    /// <summary>
    /// The list columns plus <c>opponent_archetype</c> (the other card columns stay NULL): what the profile upload sends.
    /// </summary>
    private static readonly string UploadSelect = "SELECT "
        + string.Join(", ", FirstColumns.Concat(AddedColumns.Select(c => c.Name))
            .Select(c => CardColumns.Contains(c) && c != "opponent_archetype" ? $"NULL AS {c}" : c))
        + " FROM games";

    /// <summary>
    /// Up to <paramref name="limit"/> games saved after row <paramref name="afterId"/>, in row order, with the columns the
    /// profile upload needs: a cursor over the whole history that never holds it all in memory.
    /// </summary>
    public IReadOnlyList<GameRecord> PageForUpload(long afterId, int limit) =>
        // Games brought over from another tracker stay on this PC (docs/migration.md).
        Query($"{UploadSelect} WHERE id > $after AND source NOT IN ('{Migration.HdtImport.Source}', '{Migration.FirestoneImport.Source}') ORDER BY id LIMIT $limit;", p =>
        {
            p.AddWithValue("$after", afterId);
            p.AddWithValue("$limit", limit);
        });

    /// <summary>
    /// The same games read again with their card columns (mulligan, cards drawn and played, opponent cards and
    /// archetype), in the order given; for the deck page, which needs them for one deck's games only.
    /// </summary>
    public IReadOnlyList<GameRecord> WithCards(IReadOnlyList<GameRecord> games)
    {
        if (games.Count == 0)
        {
            return games;
        }

        var full = Query("SELECT * FROM games WHERE game_key IN (SELECT value FROM json_each($keys));",
                p => p.AddWithValue("$keys", System.Text.Json.JsonSerializer.Serialize(games.Select(g => g.Key))))
            .ToDictionary(g => g.Key, StringComparer.Ordinal);
        return games.Select(g => full.GetValueOrDefault(g.Key) ?? g).ToList();
    }

    /// <summary>One game with every column, or null when no game has the key.</summary>
    public GameRecord? Game(string key) =>
        Query("SELECT * FROM games WHERE game_key = $key;", p => p.AddWithValue("$key", key)).FirstOrDefault();

    private static void AddMissingColumns(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(games);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach (var (name, type) in AddedColumns)
        {
            if (!existing.Contains(name))
            {
                Execute(connection, $"ALTER TABLE games ADD COLUMN {name} {type};");
            }
        }
    }

    public event Action? Changed;

    /// <summary>Recorded games with an outcome (the dashboard's ALL TIME tile); abandoned games are not counted.</summary>
    public int GameCount()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM games WHERE result <> 'Abandoned';";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public IReadOnlyList<GameRecord> Recent(int limit = 200, string? modeFilter = null)
    {
        var where = modeFilter switch
        {
            null => string.Empty,
            "Battlegrounds" => "WHERE mode IN ('Battlegrounds','Duos')",
            "Constructed" => "WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries')",
            _ => "WHERE mode = $mode",
        };
        return Query($"{ListSelect} {where} ORDER BY started_at DESC LIMIT $limit;", p =>
        {
            p.AddWithValue("$limit", limit);
            if (modeFilter is not (null or "Battlegrounds" or "Constructed"))
            {
                p.AddWithValue("$mode", modeFilter);
            }
        });
    }

    public IReadOnlyList<GameRecord> Since(DateTime since) =>
        Query($"{ListSelect} WHERE started_at >= $since ORDER BY started_at;",
            p => p.AddWithValue("$since", since.ToString("O", CultureInfo.InvariantCulture)));

    private IReadOnlyList<GameRecord> Query(string sql, Action<SqliteParameterCollection>? bind)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            bind?.Invoke(command.Parameters);
            using var reader = command.ExecuteReader();
            var list = new List<GameRecord>();
            while (reader.Read())
            {
                list.Add(new GameRecord
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Key = reader.GetString(reader.GetOrdinal("game_key")),
                    StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    EndedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("ended_at")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    Mode = reader.GetString(reader.GetOrdinal("mode")),
                    GameType = reader.GetString(reader.GetOrdinal("game_type")),
                    Format = reader.GetString(reader.GetOrdinal("format")),
                    PlayerName = reader.GetString(reader.GetOrdinal("player_name")),
                    OpponentName = reader.GetString(reader.GetOrdinal("opponent_name")),
                    HeroCardId = reader.GetString(reader.GetOrdinal("hero")),
                    OpponentHeroCardId = reader.GetString(reader.GetOrdinal("opponent_hero")),
                    PlayerClass = reader.GetString(reader.GetOrdinal("player_class")),
                    OpponentClass = reader.GetString(reader.GetOrdinal("opponent_class")),
                    Result = reader.GetString(reader.GetOrdinal("result")),
                    Placement = reader.GetInt32(reader.GetOrdinal("placement")),
                    RatingBefore = NullableInt(reader, "rating_before"),
                    RatingAfter = NullableInt(reader, "rating_after"),
                    Turns = reader.GetInt32(reader.GetOrdinal("turns")),
                    DeckName = NullableString(reader, "deck_name"),
                    DeckCode = NullableString(reader, "deck_code"),
                    TavernTier = reader.GetInt32(reader.GetOrdinal("tavern_tier")),
                    FinalBoard = NullableString(reader, "final_board"),
                    Races = NullableString(reader, "races"),
                    Rank = RankInfo.Normalize(NullableString(reader, "rank")),
                    Source = reader.GetString(reader.GetOrdinal("source")),
                    CombatResults = NullableString(reader, "combat_results"),
                    CombatOdds = NullableString(reader, "combat_odds"),
                    OfferedHeroes = NullableString(reader, "offered_heroes"),
                    AnomalyCardId = NullableString(reader, "anomaly"),
                    RankStarLevel = NullableInt(reader, "rank_star_level"),
                    RankLegend = NullableInt(reader, "rank_legend"),
                    WentFirst = NullableInt(reader, "went_first") is { } first ? first != 0 : null,
                    ScenarioId = NullableInt(reader, "scenario_id"),
                    DeckId = reader.IsDBNull(reader.GetOrdinal("deck_id")) ? null : reader.GetInt64(reader.GetOrdinal("deck_id")),
                    Mulligan = NullableString(reader, "mulligan"),
                    CardsDrawn = NullableString(reader, "cards_drawn"),
                    CardsPlayed = NullableString(reader, "cards_played"),
                    OpponentCards = NullableString(reader, "opponent_cards"),
                    OpponentArchetype = NullableString(reader, "opponent_archetype"),
                    Replay = NullableString(reader, "replay"),
                });
            }

            return list;
        }
    }

    private static void BindCardColumns(SqliteParameterCollection p, GameRecord game)
    {
        p.AddWithValue("$mulligan", (object?)game.Mulligan ?? DBNull.Value);
        p.AddWithValue("$drawn", (object?)game.CardsDrawn ?? DBNull.Value);
        p.AddWithValue("$played", (object?)game.CardsPlayed ?? DBNull.Value);
        p.AddWithValue("$ocards", (object?)game.OpponentCards ?? DBNull.Value);
        p.AddWithValue("$oarch", (object?)game.OpponentArchetype ?? DBNull.Value);
        p.AddWithValue("$replay", (object?)game.Replay ?? DBNull.Value);
    }

    private static int? NullableInt(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private static string? NullableString(SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
