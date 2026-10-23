using System.Globalization;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

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

    /// <summary>
    /// <c>Result</c> of a game the client left before it ended (closed or crashed mid-game): its outcome is not known, so
    /// it is kept in History but left out of win rates, placements and the other stats.
    /// </summary>
    public const string AbandonedResult = "Abandoned";

    /// <summary>Win / Loss / Tie / Unknown, or <see cref="AbandonedResult"/>.</summary>
    public string Result { get; init; } = "Unknown";

    /// <summary>The client left the game before it ended (see <see cref="AbandonedResult"/>).</summary>
    public bool IsAbandoned => Result == AbandonedResult;

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

    // ---- My Stats fields (added by migration; null in rows saved before them) ----

    /// <summary>
    /// Battlegrounds: every combat of the game as JSON, oldest first:
    /// <c>[{"turn":3,"result":"Win","opponent":"TB_BaconShop_HERO_08","taken":0,"dealt":5}]</c>.
    /// <c>result</c> is Win/Loss/Tie from the player's side; <c>taken</c>/<c>dealt</c> is hero damage after the fight.
    /// </summary>
    public string? CombatResults { get; init; }

    /// <summary>Battlegrounds: hero card ids offered at the hero pick, comma-separated, in offer order.</summary>
    public string? OfferedHeroes { get; init; }

    /// <summary>Battlegrounds: card id of the lobby's anomaly, if the game had one.</summary>
    public string? AnomalyCardId { get; init; }

    /// <summary>Constructed: ranked star level 1-50 (Bronze 10 = 1 ... Diamond 1 = 50, 51+ = Legend) at game start.</summary>
    public int? RankStarLevel { get; init; }

    /// <summary>Constructed: Legend rank number at game start (null below Legend or when unknown).</summary>
    public int? RankLegend { get; init; }

    /// <summary>Constructed and Arena: true when the player went first, false when on the coin.</summary>
    public bool? WentFirst { get; init; }

    /// <summary>Deck id from Decks.log (Constructed decks and Arena runs: one Arena run has one deck id).</summary>
    public long? DeckId { get; init; }

    // ---- Deck page fields (Constructed; null in rows saved before them) ----

    /// <summary>Local mulligan as JSON (<see cref="GameCards.MulliganJson"/>): offered, replaced and received card ids.</summary>
    public string? Mulligan { get; init; }

    /// <summary>Deck cards the player drew, JSON <c>[{"id":..,"turn":..}]</c>; turn 0 is the opening hand after the mulligan.</summary>
    public string? CardsDrawn { get; init; }

    /// <summary>Deck cards the player played from hand, JSON <c>[{"id":..,"turn":..}]</c> (the player's own turn number).</summary>
    public string? CardsPlayed { get; init; }

    /// <summary>The opponent's starting-deck cards it played face up, JSON <c>[{"id":..,"turn":..}]</c>. Nothing hidden.</summary>
    public string? OpponentCards { get; init; }

    /// <summary>The opponent's archetype when its played cards placed it clearly (<c>ArchetypeGuess</c>), else null.</summary>
    public string? OpponentArchetype { get; init; }

    /// <summary>File name of the game's match replay in the replays folder (<c>Replay/ReplayStore</c>); null when none was recorded.</summary>
    public string? Replay { get; init; }

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
public sealed partial class HistoryStore
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
        AddMissingColumns(connection);
        CreateFamilyTables(connection);
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

    /// <summary>
    /// Inserts a game unless one with the same key exists. A saved <see cref="GameRecord.AbandonedResult"/> row is replaced
    /// when the same game turns out to have ended after all (the client came back, a backfill found the end). Returns true
    /// when inserted or replaced.
    /// </summary>
    public bool Add(GameRecord game)
    {
        int inserted;
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            // A reconnect replays the game from a new CREATE_GAME: another start time, so another key. The part played
            // before the client went away must not stay in History as a separate abandoned game.
            if (game.IsAbandoned && CountSameGame(connection, game) > 0)
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO games (game_key, started_at, ended_at, mode, game_type, format, player_name, opponent_name,
                    hero, opponent_hero, player_class, opponent_class, result, placement, rating_before, rating_after, turns,
                    deck_name, deck_code, tavern_tier, final_board, races, rank, source,
                    combat_results, offered_heroes, anomaly, rank_star_level, rank_legend, went_first, deck_id,
                    mulligan, cards_drawn, cards_played, opponent_cards, opponent_archetype, replay)
                VALUES ($key, $started, $ended, $mode, $type, $format, $player, $opponent, $hero, $ohero, $pclass, $oclass,
                    $result, $placement, $rb, $ra, $turns, $deck, $code, $tier, $board, $races, $rank, $source,
                    $combats, $offered, $anomaly, $stars, $legend, $first, $deckid,
                    $mulligan, $drawn, $played, $ocards, $oarch, $replay)
                ON CONFLICT (game_key) DO UPDATE SET
                    ended_at = excluded.ended_at, mode = excluded.mode, game_type = excluded.game_type, format = excluded.format,
                    player_name = excluded.player_name, opponent_name = excluded.opponent_name, hero = excluded.hero,
                    opponent_hero = excluded.opponent_hero, player_class = excluded.player_class,
                    opponent_class = excluded.opponent_class, result = excluded.result, placement = excluded.placement,
                    rating_before = COALESCE(games.rating_before, excluded.rating_before), rating_after = excluded.rating_after,
                    turns = excluded.turns, deck_name = excluded.deck_name, deck_code = excluded.deck_code,
                    tavern_tier = excluded.tavern_tier, final_board = excluded.final_board,
                    races = COALESCE(excluded.races, games.races), rank = COALESCE(games.rank, excluded.rank),
                    source = excluded.source, combat_results = excluded.combat_results,
                    offered_heroes = COALESCE(excluded.offered_heroes, games.offered_heroes),
                    anomaly = COALESCE(excluded.anomaly, games.anomaly),
                    rank_star_level = COALESCE(games.rank_star_level, excluded.rank_star_level),
                    rank_legend = COALESCE(games.rank_legend, excluded.rank_legend),
                    went_first = COALESCE(excluded.went_first, games.went_first), deck_id = COALESCE(excluded.deck_id, games.deck_id),
                    mulligan = COALESCE(excluded.mulligan, games.mulligan),
                    cards_drawn = COALESCE(excluded.cards_drawn, games.cards_drawn),
                    cards_played = COALESCE(excluded.cards_played, games.cards_played),
                    opponent_cards = COALESCE(excluded.opponent_cards, games.opponent_cards),
                    opponent_archetype = COALESCE(excluded.opponent_archetype, games.opponent_archetype),
                    replay = COALESCE(excluded.replay, games.replay)
                WHERE games.result = 'Abandoned' AND excluded.result <> 'Abandoned';
                """;
            command.Parameters.AddWithValue("$combats", (object?)game.CombatResults ?? DBNull.Value);
            command.Parameters.AddWithValue("$offered", (object?)game.OfferedHeroes ?? DBNull.Value);
            command.Parameters.AddWithValue("$anomaly", (object?)game.AnomalyCardId ?? DBNull.Value);
            command.Parameters.AddWithValue("$stars", (object?)game.RankStarLevel ?? DBNull.Value);
            command.Parameters.AddWithValue("$legend", (object?)game.RankLegend ?? DBNull.Value);
            command.Parameters.AddWithValue("$first", game.WentFirst is { } first ? first ? 1 : 0 : DBNull.Value);
            command.Parameters.AddWithValue("$deckid", (object?)game.DeckId ?? DBNull.Value);
            BindCardColumns(command.Parameters, game);
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
            if (inserted > 0 && !game.IsAbandoned)
            {
                DeleteSameGame(connection, game);
            }

            transaction.Commit();
        }

        if (inserted > 0)
        {
            Changed?.Invoke();
        }

        return inserted > 0;
    }

    /// <summary>The longest a game the client left can still be resumed by a reconnect.</summary>
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromHours(1);

    /// <summary>The longest game: an abandoned part started at most this long before the reconnected game.</summary>
    private static readonly TimeSpan LongestGame = TimeSpan.FromHours(2);

    /// <summary>
    /// The same game seen twice: the part before the client went away (abandoned) and the reconnect that went on with it.
    /// Matched on what both parts carry - player, game type, the player's and the opponent's hero, the opponent's name
    /// when both know it - and on time: the reconnect starts after the abandoned part and within an hour of its end.
    /// </summary>
    private const string SameGame = """
        player_name = $player AND game_type = $type AND hero = $hero AND opponent_hero = $ohero
            AND (opponent_name = $opponent OR opponent_name = '' OR $opponent = '' OR opponent_name LIKE 'UNKNOWN%' OR $opponent LIKE 'UNKNOWN%')
        """;

    private static long CountSameGame(SqliteConnection connection, GameRecord abandoned)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COUNT(*) FROM games WHERE result <> 'Abandoned' AND game_key <> $key AND {SameGame}
                AND started_at >= $started AND started_at <= $latest;
            """;
        AddIdentity(command, abandoned);
        command.Parameters.AddWithValue("$latest", Iso(abandoned.EndedAt + ReconnectWindow));
        return (long)command.ExecuteScalar()!;
    }

    private static void DeleteSameGame(SqliteConnection connection, GameRecord finished)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM games WHERE result = 'Abandoned' AND game_key <> $key AND {SameGame}
                AND started_at <= $started AND started_at >= $earliest AND ended_at >= $endedAfter;
            """;
        AddIdentity(command, finished);
        command.Parameters.AddWithValue("$earliest", Iso(finished.StartedAt - LongestGame));
        command.Parameters.AddWithValue("$endedAfter", Iso(finished.StartedAt - ReconnectWindow));
        command.ExecuteNonQuery();
    }

    private static void AddIdentity(SqliteCommand command, GameRecord game)
    {
        command.Parameters.AddWithValue("$key", game.Key);
        command.Parameters.AddWithValue("$player", game.PlayerName);
        command.Parameters.AddWithValue("$type", game.GameType);
        command.Parameters.AddWithValue("$hero", game.HeroCardId);
        command.Parameters.AddWithValue("$ohero", game.OpponentHeroCardId);
        command.Parameters.AddWithValue("$opponent", game.OpponentName);
        command.Parameters.AddWithValue("$started", Iso(game.StartedAt));
    }

    private static string Iso(DateTime at) => at.ToString("O", CultureInfo.InvariantCulture);

    /// <summary>
    /// Fills the My Stats fields (<see cref="AddedColumns"/>) a saved game lacks from a re-read of the same game, e.g. a
    /// backfill over logs whose games were saved before those fields existed. Values already saved are kept. Returns
    /// true when the row changed.
    /// </summary>
    public bool FillMissingFields(GameRecord game)
    {
        int changed;
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE games SET
                    combat_results = COALESCE(combat_results, $combats),
                    offered_heroes = COALESCE(offered_heroes, $offered),
                    anomaly = COALESCE(anomaly, $anomaly),
                    rank_star_level = COALESCE(rank_star_level, $stars),
                    rank_legend = COALESCE(rank_legend, $legend),
                    went_first = COALESCE(went_first, $first),
                    deck_id = COALESCE(deck_id, $deckid),
                    mulligan = COALESCE(mulligan, $mulligan),
                    cards_drawn = COALESCE(cards_drawn, $drawn),
                    cards_played = COALESCE(cards_played, $played),
                    opponent_cards = COALESCE(opponent_cards, $ocards),
                    opponent_archetype = COALESCE(opponent_archetype, $oarch),
                    replay = COALESCE(replay, $replay)
                WHERE game_key = $key
                    AND ((combat_results IS NULL AND $combats IS NOT NULL) OR (offered_heroes IS NULL AND $offered IS NOT NULL)
                        OR (anomaly IS NULL AND $anomaly IS NOT NULL) OR (rank_star_level IS NULL AND $stars IS NOT NULL)
                        OR (rank_legend IS NULL AND $legend IS NOT NULL) OR (went_first IS NULL AND $first IS NOT NULL)
                        OR (deck_id IS NULL AND $deckid IS NOT NULL) OR (mulligan IS NULL AND $mulligan IS NOT NULL)
                        OR (cards_drawn IS NULL AND $drawn IS NOT NULL) OR (cards_played IS NULL AND $played IS NOT NULL)
                        OR (opponent_cards IS NULL AND $ocards IS NOT NULL) OR (opponent_archetype IS NULL AND $oarch IS NOT NULL)
                        OR (replay IS NULL AND $replay IS NOT NULL));
                """;
            command.Parameters.AddWithValue("$key", game.Key);
            command.Parameters.AddWithValue("$combats", (object?)game.CombatResults ?? DBNull.Value);
            command.Parameters.AddWithValue("$offered", (object?)game.OfferedHeroes ?? DBNull.Value);
            command.Parameters.AddWithValue("$anomaly", (object?)game.AnomalyCardId ?? DBNull.Value);
            command.Parameters.AddWithValue("$stars", (object?)game.RankStarLevel ?? DBNull.Value);
            command.Parameters.AddWithValue("$legend", (object?)game.RankLegend ?? DBNull.Value);
            command.Parameters.AddWithValue("$first", game.WentFirst is { } first ? first ? 1 : 0 : DBNull.Value);
            command.Parameters.AddWithValue("$deckid", (object?)game.DeckId ?? DBNull.Value);
            BindCardColumns(command.Parameters, game);
            changed = command.ExecuteNonQuery();
        }

        if (changed > 0)
        {
            Changed?.Invoke();
        }

        return changed > 0;
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

    /// <summary>
    /// Gives games saved without a deck code the deck Decks.log named for them; a deck name already saved is kept.
    /// Returns how many games changed.
    /// </summary>
    public int FillDecks(IReadOnlyCollection<DeckFill> fills)
    {
        var changed = 0;
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            foreach (var fill in fills)
            {
                // The game's deck version was "name|CLASS"; with a code it becomes the card list. A family or a
                // "not the same deck" choice made on the old key must follow it (DeckFamilies).
                var oldKey = NameKeyOf(connection, transaction, fill.GameKey);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE games SET deck_code = $code, deck_name = COALESCE(deck_name, $name)
                    WHERE game_key = $key AND (deck_code IS NULL OR deck_code = '');
                    """;
                command.Parameters.AddWithValue("$code", fill.DeckCode);
                command.Parameters.AddWithValue("$name", fill.DeckName);
                command.Parameters.AddWithValue("$key", fill.GameKey);
                var updated = command.ExecuteNonQuery();
                changed += updated;
                if (updated > 0 && oldKey is not null)
                {
                    CarryDeckKey(connection, transaction, oldKey, DeckRecords.CardListKey(fill.DeckCode));
                }
            }

            transaction.Commit();
        }

        if (changed > 0)
        {
            Changed?.Invoke();
        }

        return changed;
    }

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

    public IReadOnlyList<HeroAggregate> HeroStats(Func<string, string> baseHero, string mode = "Battlegrounds")
    {
        var games = Query($"{ListSelect} WHERE mode = $mode AND placement > 0;", p => p.AddWithValue("$mode", mode));
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
        Query($"{ListSelect} WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND player_class <> '' AND result <> 'Abandoned';", null)
            .GroupBy(g => (g.PlayerClass, g.Mode))
            .Select(g => new ClassAggregate(g.Key.PlayerClass, g.Key.Mode, g.Count(),
                g.Count(x => x.Result == "Win"), g.Count(x => x.Result == "Loss")))
            .OrderByDescending(c => c.Games)
            .ToList();

    /// <summary>Constructed results grouped per deck (deck code when known, otherwise name and class).</summary>
    public IReadOnlyList<DeckAggregate> DeckStats() =>
        Query($"{ListSelect} WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND deck_name IS NOT NULL AND result <> 'Abandoned';", null)
            .GroupBy(DeckRecords.DeckKey)
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
        Query($"{ListSelect} WHERE mode NOT IN ('Battlegrounds','Duos','Mercenaries') AND opponent_class <> '' AND result <> 'Abandoned'"
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
        Query($"{ListSelect} WHERE mode = $mode AND rating_after IS NOT NULL ORDER BY started_at DESC LIMIT $limit;", p =>
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
        foreach (var game in Query($"{ListSelect} WHERE mode = $mode AND placement BETWEEN 1 AND 8;", p => p.AddWithValue("$mode", mode)))
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
                    Rank = RankInfo.Normalize(NullableString(reader, "rank")),
                    Source = reader.GetString(reader.GetOrdinal("source")),
                    CombatResults = NullableString(reader, "combat_results"),
                    OfferedHeroes = NullableString(reader, "offered_heroes"),
                    AnomalyCardId = NullableString(reader, "anomaly"),
                    RankStarLevel = NullableInt(reader, "rank_star_level"),
                    RankLegend = NullableInt(reader, "rank_legend"),
                    WentFirst = NullableInt(reader, "went_first") is { } first ? first != 0 : null,
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
