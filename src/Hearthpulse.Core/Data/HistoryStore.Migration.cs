using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

/// <summary>A deck kept in the deck library, played or not (decks brought over from HDT; the deck manager, roadmap 1.4).</summary>
public sealed record SavedDeck(
    string Source,
    string ExternalId,
    string Name,
    string PlayerClass,
    string? DeckCode,
    string? Format,
    IReadOnlyList<string> Tags,
    string? Note,
    bool Archived,
    DateTime EditedAt);

public sealed partial class HistoryStore
{
    /// <summary>
    /// Games of a mode starting this close to another are the same game seen twice (a log import and another tracker's
    /// record of it differ by seconds).
    /// </summary>
    public static readonly TimeSpan SameGameWindow = TimeSpan.FromMinutes(2);

    private static void CreateSavedDeckTables(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS saved_decks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            external_id TEXT NOT NULL,
            name TEXT NOT NULL,
            player_class TEXT NOT NULL,
            deck_code TEXT NULL,
            format TEXT NULL,
            tags TEXT NULL,
            note TEXT NULL,
            archived INTEGER NOT NULL DEFAULT 0,
            edited_at TEXT NOT NULL,
            UNIQUE (source, external_id)
        );
        """);

    /// <summary>Adds the decks, or updates the ones of the same source and id. Returns how many were new.</summary>
    public int SaveDecks(IReadOnlyCollection<SavedDeck> decks)
    {
        if (decks.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var known = new HashSet<(string, string)>();
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = tx;
                existing.CommandText = "SELECT source, external_id FROM saved_decks;";
                using var reader = existing.ExecuteReader();
                while (reader.Read())
                {
                    known.Add((reader.GetString(0), reader.GetString(1)));
                }
            }

            var added = 0;
            foreach (var deck in decks)
            {
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO saved_decks (source, external_id, name, player_class, deck_code, format, tags, note, archived, edited_at)
                    VALUES ($source, $id, $name, $class, $code, $format, $tags, $note, $archived, $edited)
                    ON CONFLICT (source, external_id) DO UPDATE SET
                        name = excluded.name, player_class = excluded.player_class, deck_code = excluded.deck_code,
                        format = excluded.format, tags = excluded.tags, note = excluded.note, archived = excluded.archived,
                        edited_at = excluded.edited_at;
                    """;
                command.Parameters.AddWithValue("$source", deck.Source);
                command.Parameters.AddWithValue("$id", deck.ExternalId);
                command.Parameters.AddWithValue("$name", deck.Name);
                command.Parameters.AddWithValue("$class", deck.PlayerClass);
                command.Parameters.AddWithValue("$code", (object?)deck.DeckCode ?? DBNull.Value);
                command.Parameters.AddWithValue("$format", (object?)deck.Format ?? DBNull.Value);
                command.Parameters.AddWithValue("$tags", deck.Tags.Count > 0 ? string.Join(',', deck.Tags) : DBNull.Value);
                command.Parameters.AddWithValue("$note", deck.Note is { Length: > 0 } note ? note : DBNull.Value);
                command.Parameters.AddWithValue("$archived", deck.Archived ? 1 : 0);
                command.Parameters.AddWithValue("$edited", deck.EditedAt.ToString("O", CultureInfo.InvariantCulture));
                command.ExecuteNonQuery();
                added += known.Add((deck.Source, deck.ExternalId)) ? 1 : 0;
            }

            tx.Commit();
            return added;
        }
    }

    /// <summary>The saved decks, newest edit first.</summary>
    public IReadOnlyList<SavedDeck> SavedDecks()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source, external_id, name, player_class, deck_code, format, tags, note, archived, edited_at FROM saved_decks ORDER BY edited_at DESC;";
            using var reader = command.ExecuteReader();
            var decks = new List<SavedDeck>();
            while (reader.Read())
            {
                decks.Add(new SavedDeck(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? [] : reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt32(8) != 0,
                    DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
            }

            return decks;
        }
    }

    /// <summary>
    /// The game History already has that <paramref name="game"/> is again: the same key, or a game of the same type
    /// starting within <see cref="SameGameWindow"/> against the same opponent class (Battlegrounds: with the same hero);
    /// null when there is none.
    /// </summary>
    public GameRecord? FindSameGame(GameRecord game)
    {
        string? key;
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT game_key, opponent_class, hero FROM games WHERE game_key = $key OR ({InWindow});";
            BindWindow(command, game);
            using var reader = command.ExecuteReader();
            var found = new List<(string Key, string OpponentClass, string Hero)>();
            while (reader.Read())
            {
                found.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }

            key = found.FirstOrDefault(f => f.Key == game.Key).Key
                ?? found.FirstOrDefault(f => game.IsBattlegrounds ? f.Hero == game.HeroCardId : f.OpponentClass == game.OpponentClass).Key;
        }

        return key is null ? null : Game(key);
    }

    /// <summary>
    /// Puts <paramref name="game"/> in place of the row <paramref name="oldKey"/> (the same game, a fuller record of it).
    /// The old row's replay file goes unless the new row keeps it. True when the new row is in.
    /// </summary>
    public bool ReplaceGame(string oldKey, GameRecord game)
    {
        string? file;
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT replay FROM games WHERE game_key = $key; DELETE FROM games WHERE game_key = $key;";
            command.Parameters.AddWithValue("$key", oldKey);
            file = command.ExecuteScalar() as string;
        }

        if (file is not null && file != game.Replay)
        {
            Replays?.Delete(file);
        }

        return Add(game);
    }

    /// <summary>
    /// A game the app recorded itself (live, from the logs) is the fuller record: a row brought over from another tracker
    /// of the same game goes. Returns the replay files of the rows deleted.
    /// </summary>
    private static List<string> DeleteOtherTrackersSameGame(SqliteConnection connection, GameRecord own)
    {
        var match = own.IsBattlegrounds ? "hero = $hero" : "opponent_class = $oclass";
        var where = $"source IN ('{Migration.HdtImport.Source}', '{Migration.FirestoneImport.Source}') AND game_key <> $key AND {InWindow} AND {match}";
        var files = new List<string>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT replay FROM games WHERE {where} AND replay IS NOT NULL;";
            BindWindow(select, own);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                files.Add(reader.GetString(0));
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM games WHERE {where};";
            BindWindow(delete, own);
            delete.ExecuteNonQuery();
        }

        return files;
    }

    // Start times are stored as round-trip text; the second-precision prefix orders like the time itself.
    private const string InWindow = "game_type = $type AND substr(started_at, 1, 19) BETWEEN $from AND $to";

    private static void BindWindow(SqliteCommand command, GameRecord game)
    {
        command.Parameters.AddWithValue("$key", game.Key);
        command.Parameters.AddWithValue("$type", game.GameType);
        command.Parameters.AddWithValue("$hero", game.HeroCardId);
        command.Parameters.AddWithValue("$oclass", game.OpponentClass);
        command.Parameters.AddWithValue("$from", (game.StartedAt - SameGameWindow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", (game.StartedAt + SameGameWindow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
    }
}
