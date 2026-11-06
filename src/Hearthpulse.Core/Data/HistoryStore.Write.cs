using System.Globalization;
using Hearthpulse.Core.Memory;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

public sealed partial class HistoryStore
{
    /// <summary>
    /// Inserts a game unless one with the same key exists. A saved <see cref="GameRecord.AbandonedResult"/> row is replaced
    /// when the same game turns out to have ended after all (the client came back, a backfill found the end). Returns true
    /// when inserted or replaced.
    /// </summary>
    public bool Add(GameRecord game)
    {
        int inserted;
        var orphans = new List<string>();
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            // A reconnect replays the game from a new CREATE_GAME: another start time, so another key. The part played
            // before the client went away must not stay in History as a separate abandoned game - nor its replay file.
            if (game.IsAbandoned && CountSameGame(connection, game) > 0)
            {
                Replays?.Delete(game.Replay);
                return false;
            }

            // The app's own record of a game replaces the one brought over from another tracker (docs/migration.md).
            if (!game.FromOtherTracker && !game.IsAbandoned)
            {
                orphans.AddRange(DeleteOtherTrackersSameGame(connection, game));
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO games (game_key, started_at, ended_at, mode, game_type, format, player_name, opponent_name,
                    hero, opponent_hero, player_class, opponent_class, result, placement, rating_before, rating_after, turns,
                    deck_name, deck_code, tavern_tier, final_board, races, rank, source,
                    combat_results, offered_heroes, anomaly, rank_star_level, rank_legend, went_first, deck_id,
                    mulligan, cards_drawn, cards_played, opponent_cards, opponent_archetype, replay, combat_odds, scenario_id)
                VALUES ($key, $started, $ended, $mode, $type, $format, $player, $opponent, $hero, $ohero, $pclass, $oclass,
                    $result, $placement, $rb, $ra, $turns, $deck, $code, $tier, $board, $races, $rank, $source,
                    $combats, $offered, $anomaly, $stars, $legend, $first, $deckid,
                    $mulligan, $drawn, $played, $ocards, $oarch, $replay, $codds, $scenario)
                ON CONFLICT (game_key) DO UPDATE SET
                    ended_at = excluded.ended_at, mode = excluded.mode, game_type = excluded.game_type, format = excluded.format,
                    player_name = excluded.player_name, opponent_name = excluded.opponent_name, hero = excluded.hero,
                    opponent_hero = excluded.opponent_hero, player_class = excluded.player_class,
                    opponent_class = excluded.opponent_class, result = excluded.result, placement = excluded.placement,
                    rating_before = COALESCE(games.rating_before, excluded.rating_before), rating_after = excluded.rating_after,
                    turns = excluded.turns, deck_name = excluded.deck_name, deck_code = excluded.deck_code,
                    tavern_tier = excluded.tavern_tier, final_board = excluded.final_board,
                    races = COALESCE(excluded.races, games.races), rank = COALESCE(games.rank, excluded.rank),
                    source = excluded.source, combat_results = excluded.combat_results, combat_odds = excluded.combat_odds,
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
                    replay = COALESCE(excluded.replay, games.replay),
                    scenario_id = COALESCE(excluded.scenario_id, games.scenario_id)
                WHERE games.result = 'Abandoned' AND excluded.result <> 'Abandoned';
                """;
            command.Parameters.AddWithValue("$combats", (object?)game.CombatResults ?? DBNull.Value);
            command.Parameters.AddWithValue("$codds", (object?)game.CombatOdds ?? DBNull.Value);
            command.Parameters.AddWithValue("$scenario", (object?)game.ScenarioId ?? DBNull.Value);
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
                orphans.AddRange(DeleteSameGame(connection, game));
            }

            transaction.Commit();
        }

        foreach (var file in orphans)
        {
            Replays?.Delete(file);
        }

        if (inserted > 0)
        {
            Changed?.Invoke();
        }

        return inserted > 0;
    }

    /// <summary>
    /// Where the games' replay files are, so that a row dropped as the left part of a reconnected game takes its file
    /// with it (or hands it to the reconnect, when that one has none). Null: files are not touched.
    /// </summary>
    public Replay.ReplayStore? Replays { get; init; }

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

    /// <summary>
    /// Deletes the abandoned parts of a game that ended after a reconnect. Returns the replay files nothing refers to
    /// any more: a part's file goes to the finished game when that one has no replay (the reconnect started recording
    /// too late), else it is no longer needed.
    /// </summary>
    private static List<string> DeleteSameGame(SqliteConnection connection, GameRecord finished)
    {
        const string Parts = $"""
            FROM games WHERE result = 'Abandoned' AND game_key <> $key AND {SameGame}
                AND started_at <= $started AND started_at >= $earliest AND ended_at >= $endedAfter
            """;
        void Bind(SqliteCommand command)
        {
            AddIdentity(command, finished);
            command.Parameters.AddWithValue("$earliest", Iso(finished.StartedAt - LongestGame));
            command.Parameters.AddWithValue("$endedAfter", Iso(finished.StartedAt - ReconnectWindow));
        }

        var files = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT replay {Parts} AND replay IS NOT NULL ORDER BY started_at;";
            Bind(select);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                files.Add(reader.GetString(0));
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = $"DELETE {Parts};";
            Bind(delete);
            delete.ExecuteNonQuery();
        }

        if (finished.Replay is null && files.Count > 0)
        {
            using var carry = connection.CreateCommand();
            carry.CommandText = "UPDATE games SET replay = $replay WHERE game_key = $key AND replay IS NULL;";
            carry.Parameters.AddWithValue("$replay", files[^1]);
            carry.Parameters.AddWithValue("$key", finished.Key);
            if (carry.ExecuteNonQuery() > 0)
            {
                files.RemoveAt(files.Count - 1);
            }
        }

        return files;
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
                    replay = COALESCE(replay, $replay),
                    combat_odds = COALESCE(combat_odds, $codds),
                    scenario_id = COALESCE(scenario_id, $scenario)
                WHERE game_key = $key
                    AND ((combat_results IS NULL AND $combats IS NOT NULL) OR (offered_heroes IS NULL AND $offered IS NOT NULL)
                        OR (anomaly IS NULL AND $anomaly IS NOT NULL) OR (rank_star_level IS NULL AND $stars IS NOT NULL)
                        OR (rank_legend IS NULL AND $legend IS NOT NULL) OR (went_first IS NULL AND $first IS NOT NULL)
                        OR (deck_id IS NULL AND $deckid IS NOT NULL) OR (mulligan IS NULL AND $mulligan IS NOT NULL)
                        OR (cards_drawn IS NULL AND $drawn IS NOT NULL) OR (cards_played IS NULL AND $played IS NOT NULL)
                        OR (opponent_cards IS NULL AND $ocards IS NOT NULL) OR (opponent_archetype IS NULL AND $oarch IS NOT NULL)
                        OR (replay IS NULL AND $replay IS NOT NULL) OR (combat_odds IS NULL AND $codds IS NOT NULL)
                        OR (scenario_id IS NULL AND $scenario IS NOT NULL));
                """;
            command.Parameters.AddWithValue("$key", game.Key);
            command.Parameters.AddWithValue("$combats", (object?)game.CombatResults ?? DBNull.Value);
            command.Parameters.AddWithValue("$codds", (object?)game.CombatOdds ?? DBNull.Value);
            command.Parameters.AddWithValue("$scenario", (object?)game.ScenarioId ?? DBNull.Value);
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
}
