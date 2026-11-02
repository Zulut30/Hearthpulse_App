using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

/// <summary>
/// A bookmarked step of a replay (<c>docs/match-replay.md</c>): its step and the replay's step count when it was set,
/// and where it sits in the game, its stretch of the timeline (mulligan, a turn, a tavern, a combat) and its place in
/// it. A replay written again with other steps (a newer recorder, a reconnect, an import) moves the step numbers; the
/// stretch and the place find it again.
/// </summary>
/// <param name="Stretch">The index of its timeline stretch (<c>MatchPage.Timeline</c>).</param>
/// <param name="Offset">Steps from the stretch's first step.</param>
public sealed record ReplayBookmark(int Frame, int FrameCount, int Stretch, int Offset)
{
    /// <summary>
    /// The step this bookmark is on in a replay of <paramref name="frameCount"/> steps whose stretches start at
    /// <paramref name="stretchStarts"/>: its own step while the count is the same; else its place in its stretch, when the
    /// stretch is still there and that long; else null (the bookmark is gone).
    /// </summary>
    public int? Resolve(int frameCount, IReadOnlyList<int> stretchStarts)
    {
        if (frameCount == FrameCount)
        {
            return Frame < frameCount ? Frame : null;
        }

        if (Stretch < 0 || Stretch >= stretchStarts.Count)
        {
            return null;
        }

        var end = Stretch + 1 < stretchStarts.Count ? stretchStarts[Stretch + 1] : frameCount;
        var frame = stretchStarts[Stretch] + Offset;
        return Offset >= 0 && frame < end ? frame : null;
    }

    /// <summary>A bookmark on <paramref name="frame"/> of a replay of <paramref name="frameCount"/> steps.</summary>
    public static ReplayBookmark At(int frame, int frameCount, IReadOnlyList<int> stretchStarts)
    {
        var stretch = -1;
        for (var i = 0; i < stretchStarts.Count; i++)
        {
            if (stretchStarts[i] <= frame)
            {
                stretch = i;
            }
        }

        return new ReplayBookmark(frame, frameCount, stretch, stretch < 0 ? frame : frame - stretchStarts[stretch]);
    }
}

public sealed partial class HistoryStore
{
    /// <summary>
    /// Steps the player bookmarked in a replay, by the replay file's name: a starred copy in Saved has the same name, so
    /// it shares them. Local only: never uploaded or shared.
    /// </summary>
    private static void CreateBookmarkTables(SqliteConnection connection)
    {
        // The first test builds had no step count or stretch: such a table holds nothing to keep.
        if (BookmarkColumns(connection) is { Count: > 0 } columns && !columns.Contains("frame_count"))
        {
            Execute(connection, "DROP TABLE replay_bookmarks;");
        }

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS replay_bookmarks (
                replay TEXT NOT NULL,
                frame INTEGER NOT NULL,
                frame_count INTEGER NOT NULL,
                stretch INTEGER NOT NULL,
                stretch_step INTEGER NOT NULL,
                added_at TEXT NOT NULL,
                PRIMARY KEY (replay, frame)
            );
            """);
    }

    private static HashSet<string> BookmarkColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(replay_bookmarks);";
        using var reader = command.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    /// <summary>The bookmarks of a replay, first step first.</summary>
    public IReadOnlyList<ReplayBookmark> Bookmarks(string replay)
    {
        ArgumentException.ThrowIfNullOrEmpty(replay);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT frame, frame_count, stretch, stretch_step FROM replay_bookmarks WHERE replay = $replay ORDER BY frame;";
            command.Parameters.AddWithValue("$replay", replay);
            using var reader = command.ExecuteReader();
            var bookmarks = new List<ReplayBookmark>();
            while (reader.Read())
            {
                bookmarks.Add(new ReplayBookmark(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
            }

            return bookmarks;
        }
    }

    /// <summary>Puts a bookmark on a replay (or moves the one on its step), or takes the one on its step off.</summary>
    public void SetBookmark(string replay, ReplayBookmark bookmark, bool on)
    {
        ArgumentException.ThrowIfNullOrEmpty(replay);
        ArgumentNullException.ThrowIfNull(bookmark);
        if (bookmark.Frame < 0)
        {
            throw new ArgumentException("A bookmark's step is 0 or more.", nameof(bookmark));
        }
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$replay", replay);
            command.Parameters.AddWithValue("$frame", bookmark.Frame);
            if (on)
            {
                command.CommandText = """
                    INSERT INTO replay_bookmarks (replay, frame, frame_count, stretch, stretch_step, added_at)
                    VALUES ($replay, $frame, $count, $stretch, $offset, $added)
                    ON CONFLICT (replay, frame) DO UPDATE SET frame_count = $count, stretch = $stretch, stretch_step = $offset;
                    """;
                command.Parameters.AddWithValue("$count", bookmark.FrameCount);
                command.Parameters.AddWithValue("$stretch", bookmark.Stretch);
                command.Parameters.AddWithValue("$offset", bookmark.Offset);
                command.Parameters.AddWithValue("$added", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            }
            else
            {
                command.CommandText = "DELETE FROM replay_bookmarks WHERE replay = $replay AND frame = $frame;";
            }

            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The replay's bookmarks as they are now in it (its step count changed: <see cref="ReplayBookmark.Resolve"/>): the
    /// ones found again are kept at their new step, the rest go.
    /// </summary>
    public void ReplaceBookmarks(string replay, IReadOnlyCollection<ReplayBookmark> bookmarks)
    {
        ArgumentException.ThrowIfNullOrEmpty(replay);
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM replay_bookmarks WHERE replay = $replay;";
                delete.Parameters.AddWithValue("$replay", replay);
                delete.ExecuteNonQuery();
            }

            var added = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach (var bookmark in bookmarks)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = tx;
                insert.CommandText = """
                    INSERT OR IGNORE INTO replay_bookmarks (replay, frame, frame_count, stretch, stretch_step, added_at)
                    VALUES ($replay, $frame, $count, $stretch, $offset, $added);
                    """;
                insert.Parameters.AddWithValue("$replay", replay);
                insert.Parameters.AddWithValue("$frame", bookmark.Frame);
                insert.Parameters.AddWithValue("$count", bookmark.FrameCount);
                insert.Parameters.AddWithValue("$stretch", bookmark.Stretch);
                insert.Parameters.AddWithValue("$offset", bookmark.Offset);
                insert.Parameters.AddWithValue("$added", added);
                insert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>The replays that have bookmarks.</summary>
    public IReadOnlyList<string> BookmarkedReplays()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT replay FROM replay_bookmarks;";
            using var reader = command.ExecuteReader();
            var replays = new List<string>();
            while (reader.Read())
            {
                replays.Add(reader.GetString(0));
            }

            return replays;
        }
    }

    /// <summary>Takes every bookmark of a replay off (its last file is gone).</summary>
    public void RemoveBookmarks(string replay)
    {
        ArgumentException.ThrowIfNullOrEmpty(replay);
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM replay_bookmarks WHERE replay = $replay;";
            command.Parameters.AddWithValue("$replay", replay);
            command.ExecuteNonQuery();
        }
    }
}
