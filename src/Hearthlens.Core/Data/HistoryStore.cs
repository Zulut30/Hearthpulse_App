using System.Globalization;
using Hearthlens.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthlens.Core.Data;

public sealed record GameRecord
{
    public long Id { get; init; }

    public required string Key { get; init; }

    public DateTime StartedAt { get; init; }

    public DateTime EndedAt { get; init; }

    public string Mode { get; init; } = "Other";

    public string GameType { get; init; } = string.Empty;

    public string Format { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string OpponentName { get; init; } = string.Empty;

    public string HeroCardId { get; init; } = string.Empty;

    public string OpponentHeroCardId { get; init; } = string.Empty;

    public string PlayerClass { get; init; } = string.Empty;

    public string OpponentClass { get; init; } = string.Empty;

    public string Result { get; init; } = "Unknown";

    public int Placement { get; init; }

    public int? RatingBefore { get; init; }

    public int? RatingAfter { get; init; }

    public int Turns { get; init; }

    public string? DeckName { get; init; }

    public string? DeckCode { get; init; }

    public int TavernTier { get; init; }

    public string? FinalBoard { get; init; }

    public string? Races { get; init; }

    public string? Rank { get; init; }

    public string Source { get; init; } = "live";

    public int? RatingDelta => RatingBefore is { } b && RatingAfter is { } a ? a - b : null;

    public TimeSpan Duration => EndedAt - StartedAt;

    public bool IsBattlegrounds => Mode is "Battlegrounds" or "Duos";
}

public sealed record HeroAggregate(string HeroCardId, int Games, double AveragePlace, double Top4Rate, double WinRate, int? NetRating);

public sealed record ClassAggregate(string PlayerClass, string Mode, int Games, int Wins, int Losses)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

public sealed record DeckAggregate(string DeckName, string? DeckCode, string PlayerClass, int Games, int Wins, int Losses, DateTime LastPlayed)
{
    public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
}

public sealed record MatchupAggregate(string OpponentClass, int Games, int Wins, int Losses)
{
    public double WinRate => Wins + Losses == 0 ? 0 : (double)Wins / (Wins + Losses);
}

public sealed record RatingPoint(DateTime At, int Rating);

/// <summary>Local game history in SQLite.</summary>
public sealed class HistoryStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public HistoryStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Cache = SqliteCacheMode.Shared }.ToString();
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
    }

    public event Action? Changed;

    /// <summary>Inserts a game unless one with the same key exists. Returns true when inserted.</summary>
    public bool Add(GameRecord game)
    {
        int inserted;
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO games (game_key, started_at, ended_at, mode, game_type, format, player_name, opponent_name,
                    hero, opponent_hero, player_class, opponent_class, result, placement, rating_before, rating_after, turns,
                    deck_name, deck_code, tavern_tier, final_board, races, rank, source)
                VALUES ($key, $started, $ended, $mode, $type, $format, $player, $opponent, $hero, $ohero, $pclass, $oclass,
                    $result, $placement, $rb, $ra, $turns, $deck, $code, $tier, $board, $races, $rank, $source);
                """;
            command.Parameters.AddWithValue("$key", game.Key);
            command.Parameters.AddWithValue("$started", game.StartedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$ended", game.EndedAt.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$mode", game.Mode);
            command.Parameters.AddWithValue("$type", game.GameType);
            command.Parameters.AddWithValue("$format", game.Format);
            command.Parameters.AddWithValue("$player", game.PlayerName);
            command.Parameters.AddWithValue("$opponent", game.OpponentName);
            command.Parameters.AddWithValue("$hero", game.HeroCardId);
            command.Parameters.AddWithValue("$ohero", game.OpponentHeroCardId);
            command.Parameters.AddWithValue("$pclass", game.PlayerClass);
            command.Parameters.AddWithValue("$oclass", game.OpponentClass);
            command.Parameters.AddWithValue("$result", game.Result);
            command.Parameters.AddWithValue("$placement", game.Placement);
            command.Parameters.AddWithValue("$rb", (object?)game.RatingBefore ?? DBNull.Value);
            command.Parameters.AddWithValue("$ra", (object?)game.RatingAfter ?? DBNull.Value);
            command.Parameters.AddWithValue("$turns", game.Turns);
            command.Parameters.AddWithValue("$deck", (object?)game.DeckName ?? DBNull.Value);
            command.Parameters.AddWithValue("$code", (object?)game.DeckCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$tier", game.TavernTier);
            command.Parameters.AddWithValue("$board", (object?)game.FinalBoard ?? DBNull.Value);
            command.Parameters.AddWithValue("$races", (object?)game.Races ?? DBNull.Value);
            command.Parameters.AddWithValue("$rank", (object?)game.Rank ?? DBNull.Value);
            command.Parameters.AddWithValue("$source", game.Source);
            inserted = command.ExecuteNonQuery();
        }

        if (inserted > 0)
        {
            Changed?.Invoke();
        }

        return inserted > 0;
    }

    /// <summary>Fills in the post-game rating once the client reports it.</summary>
    public void UpdateRatingAfter(string key, int ratingAfter)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE games SET rating_after = $ra WHERE game_key = $key;";
            command.Parameters.AddWithValue("$ra", ratingAfter);
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }

        Changed?.Invoke();
    }

    /// <summary>Total number of recorded games (the dashboard's ALL TIME tile counts the whole history).</summary>
    public int GameCount()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM games;";
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
        return Query($"SELECT * FROM games {where} ORDER BY started_at DESC LIMIT $limit;", p =>
        {
            p.AddWithValue("$limit", limit);
            if (modeFilter is not (null or "Battlegrounds" or "Constructed"))
            {
                p.AddWithValue("$mode", modeFilter);
            }
        });
    }

    public IReadOnlyList<GameRecord> Since(DateTime since) =>
        Query("SELECT * FROM games WHERE started_at >= $since ORDER BY started_at;",
            p => p.AddWithValue("$since", since.ToString("O", CultureInfo.InvariantCulture)));

    public IReadOnlyList<HeroAggregate> HeroStats(Func<string, string> baseHero, string mode = "Battlegrounds")
    {
        var games = Query("SELECT * FROM games WHERE mode = $mode AND placement > 0;", p => p.AddWithValue("$mode", mode));
        var top = mode == "Duos" ? 2 : 4;
        return games
            .GroupBy(g => baseHero(g.HeroCardId))
            .Select(g => new HeroAggregate(
                g.Key,
                g.Count(),
                g.Average(x => x.Placement),
                g.Count(x => x.Placement <= top) / (double)g.Count(),
                g.Count(x => x.Placement == 1) / (double)g.Count(),
                g.Any(x => x.RatingDelta is not null) ? g.Sum(x => x.RatingDelta ?? 0) : null))
            .OrderByDescending(h => h.Games).ThenBy(h => h.AveragePlace)
            .ToList();
    }

    public IReadOnlyList<ClassAggregate> ClassStats() =>
        Query("SELECT * FROM games WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND player_class <> '';", null)
            .GroupBy(g => (g.PlayerClass, g.Mode))
            .Select(g => new ClassAggregate(g.Key.PlayerClass, g.Key.Mode, g.Count(),
                g.Count(x => x.Result == "Win"), g.Count(x => x.Result == "Loss")))
            .OrderByDescending(c => c.Games)
            .ToList();

    /// <summary>Constructed results grouped per deck (deck code when known, otherwise name and class).</summary>
    public IReadOnlyList<DeckAggregate> DeckStats() =>
        Query("SELECT * FROM games WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND deck_name IS NOT NULL;", null)
            .GroupBy(g => g.DeckCode ?? $"{g.DeckName}|{g.PlayerClass}")
            .Select(g =>
            {
                var latest = g.MaxBy(x => x.StartedAt)!;
                return new DeckAggregate(latest.DeckName!, latest.DeckCode, latest.PlayerClass, g.Count(),
                    g.Count(x => x.Result == "Win"), g.Count(x => x.Result == "Loss"), latest.StartedAt);
            })
            .OrderByDescending(d => d.LastPlayed)
            .ToList();

    /// <summary>Win rate against each opponent class, optionally for one deck.</summary>
    public IReadOnlyList<MatchupAggregate> MatchupStats(string? deckCode = null) =>
        Query("SELECT * FROM games WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND opponent_class <> ''"
              + (deckCode is null ? ";" : " AND deck_code = $code;"),
                p =>
                {
                    if (deckCode is not null) p.AddWithValue("$code", deckCode);
                })
            .GroupBy(g => g.OpponentClass)
            .Select(g => new MatchupAggregate(g.Key, g.Count(), g.Count(x => x.Result == "Win"), g.Count(x => x.Result == "Loss")))
            .OrderByDescending(m => m.Games)
            .ToList();

    public IReadOnlyList<RatingPoint> RatingHistory(string mode = "Battlegrounds", int limit = 300) =>
        Query("SELECT * FROM games WHERE mode = $mode AND rating_after IS NOT NULL ORDER BY started_at DESC LIMIT $limit;", p =>
            {
                p.AddWithValue("$mode", mode);
                p.AddWithValue("$limit", limit);
            })
            .OrderBy(g => g.StartedAt)
            .Select(g => new RatingPoint(g.EndedAt, g.RatingAfter!.Value))
            .ToList();

    public int[] PlacementDistribution(string mode = "Battlegrounds")
    {
        var distribution = new int[8];
        foreach (var game in Query("SELECT * FROM games WHERE mode = $mode AND placement BETWEEN 1 AND 8;", p => p.AddWithValue("$mode", mode)))
        {
            distribution[game.Placement - 1]++;
        }

        return distribution;
    }

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
                    Rank = NullableString(reader, "rank"),
                    Source = reader.GetString(reader.GetOrdinal("source")),
                });
            }

            return list;
        }
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
