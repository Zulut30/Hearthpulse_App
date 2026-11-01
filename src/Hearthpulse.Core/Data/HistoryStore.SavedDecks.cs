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

    /// <summary>Adds the decks, or updates the ones of the same source and id. Returns how many were written.</summary>
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
            var written = 0;
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
                written += command.ExecuteNonQuery();
            }

            tx.Commit();
            return written;
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
    /// True when History already has <paramref name="game"/>: the same key, or a game of the same type starting within
    /// <see cref="SameGameWindow"/> against the same opponent class (Battlegrounds: of the same hero).
    /// </summary>
    public bool HasSameGame(GameRecord game)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            // Start times are stored as round-trip text; the second-precision prefix orders like the time itself.
            command.CommandText = """
                SELECT game_key, opponent_class, hero FROM games
                WHERE game_key = $key OR (game_type = $type AND substr(started_at, 1, 19) BETWEEN $from AND $to);
                """;
            command.Parameters.AddWithValue("$key", game.Key);
            command.Parameters.AddWithValue("$type", game.GameType);
            command.Parameters.AddWithValue("$from", (game.StartedAt - SameGameWindow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$to", (game.StartedAt + SameGameWindow).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(0) == game.Key
                    || (game.IsBattlegrounds ? reader.GetString(2) == game.HeroCardId : reader.GetString(1) == game.OpponentClass))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
