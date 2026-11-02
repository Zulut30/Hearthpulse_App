using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

public sealed partial class HistoryStore
{
    /// <summary>
    /// Steps the player bookmarked in a replay (<c>docs/match-replay.md</c>), by the replay file's name: a starred copy in
    /// Saved has the same name, so it shares them. Local only: never uploaded or shared.
    /// </summary>
    private static void CreateBookmarkTables(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS replay_bookmarks (
            replay TEXT NOT NULL,
            frame INTEGER NOT NULL,
            added_at TEXT NOT NULL,
            PRIMARY KEY (replay, frame)
        );
        """);

    /// <summary>The bookmarked steps of a replay, first step first.</summary>
    public IReadOnlyList<int> Bookmarks(string replay)
    {
        ArgumentException.ThrowIfNullOrEmpty(replay);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT frame FROM replay_bookmarks WHERE replay = $replay ORDER BY frame;";
            command.Parameters.AddWithValue("$replay", replay);
            using var reader = command.ExecuteReader();
            var frames = new List<int>();
            while (reader.Read())
            {
                frames.Add(reader.GetInt32(0));
            }

            return frames;
        }
    }

    /// <summary>Puts a bookmark on a step of a replay, or takes it off.</summary>
    public void SetBookmark(string replay, int frame, bool on)
    {
        ArgumentException.ThrowIfNullOrEmpty(replay);
        ArgumentOutOfRangeException.ThrowIfNegative(frame);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$replay", replay);
            command.Parameters.AddWithValue("$frame", frame);
            if (on)
            {
                command.CommandText = "INSERT OR IGNORE INTO replay_bookmarks (replay, frame, added_at) VALUES ($replay, $frame, $added);";
                command.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                command.CommandText = "DELETE FROM replay_bookmarks WHERE replay = $replay AND frame = $frame;";
            }

            command.ExecuteNonQuery();
        }
    }
}
