using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

/// <summary>
/// A deck family as saved: deck versions (by version key, see <c>DeckFamilies.VersionKey</c>) the player merged into one
/// deck, its own name (null = the main version's name) and the main version.
/// </summary>
public sealed record DeckFamilyRecord(long Id, string? Name, string? MainKey, IReadOnlyList<string> Members);

/// <summary>Deck families: which deck versions the player counts as one deck.</summary>
public sealed partial class HistoryStore
{
    /// <summary>Raised after a family was merged, split, renamed or got a new main version (games are unchanged).</summary>
    public event Action? DeckFamiliesChanged;

    // Tables of their own, created on open like the games table, so an older history file gets them unchanged otherwise.
    // A deck version belongs to at most one family; a version in no family is a deck of its own.
    private static void CreateFamilyTables(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS deck_families (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NULL,
            main_key TEXT NULL,
            created_at TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS deck_family_members (
            deck_key TEXT PRIMARY KEY,
            family_id INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_family_members ON deck_family_members(family_id);
        CREATE TABLE IF NOT EXISTS deck_family_dismissed (
            key_a TEXT NOT NULL,
            key_b TEXT NOT NULL,
            PRIMARY KEY (key_a, key_b)
        );
        """);

    public IReadOnlyList<DeckFamilyRecord> DeckFamilies()
    {
        lock (_gate)
        {
            using var connection = Open();
            var members = new Dictionary<long, List<string>>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT family_id, deck_key FROM deck_family_members ORDER BY rowid;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    if (!members.TryGetValue(id, out var list))
                    {
                        members[id] = list = new List<string>();
                    }

                    list.Add(reader.GetString(1));
                }
            }

            var families = new List<DeckFamilyRecord>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, name, main_key FROM deck_families ORDER BY id;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt64(0);
                    families.Add(new DeckFamilyRecord(id, reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2), members.GetValueOrDefault(id) ?? []));
                }
            }

            return families;
        }
    }

    /// <summary>Pairs of deck versions the player said are different decks (a dismissed suggestion or a split).</summary>
    public IReadOnlySet<(string, string)> DismissedDeckPairs()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT key_a, key_b FROM deck_family_dismissed;";
            using var reader = command.ExecuteReader();
            var pairs = new HashSet<(string, string)>();
            while (reader.Read())
            {
                pairs.Add((reader.GetString(0), reader.GetString(1)));
            }

            return pairs;
        }
    }

    /// <summary>Ordered key pair for <c>deck_family_dismissed</c>.</summary>
    public static (string, string) DeckPair(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);

    /// <summary>
    /// Puts the deck versions into one family: the oldest family any of them is in already, else a new one. Other
    /// families they were in are folded into it. Returns the family id.
    /// </summary>
    public long MergeDecks(IReadOnlyCollection<string> keys, string? name = null, string? mainKey = null)
    {
        if (keys.Count == 0)
        {
            throw new ArgumentException("No deck to merge.", nameof(keys));
        }

        long target;
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var involved = new SortedSet<long>();
            foreach (var key in keys)
            {
                if (FamilyOf(connection, tx, key) is { } id)
                {
                    involved.Add(id);
                }
            }

            if (involved.Count > 0)
            {
                target = involved.Min;
            }
            else
            {
                using var insert = Command(connection, tx, "INSERT INTO deck_families (name, main_key, created_at) VALUES (NULL, NULL, $at); SELECT last_insert_rowid();");
                insert.Parameters.AddWithValue("$at", DateTime.Now.ToString("O", CultureInfo.InvariantCulture));
                target = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            foreach (var other in involved.Where(id => id != target))
            {
                using var move = Command(connection, tx, "UPDATE deck_family_members SET family_id = $target WHERE family_id = $other; DELETE FROM deck_families WHERE id = $other;");
                move.Parameters.AddWithValue("$target", target);
                move.Parameters.AddWithValue("$other", other);
                move.ExecuteNonQuery();
            }

            foreach (var key in keys)
            {
                using var add = Command(connection, tx, "INSERT OR REPLACE INTO deck_family_members (deck_key, family_id) VALUES ($key, $family);");
                add.Parameters.AddWithValue("$key", key);
                add.Parameters.AddWithValue("$family", target);
                add.ExecuteNonQuery();
            }

            // Merging overrules an earlier "these are different decks" between any two of them.
            var all = Members(connection, tx, target);
            foreach (var a in all)
            {
                foreach (var b in all.Where(b => string.CompareOrdinal(a, b) < 0))
                {
                    using var undo = Command(connection, tx, "DELETE FROM deck_family_dismissed WHERE key_a = $a AND key_b = $b;");
                    undo.Parameters.AddWithValue("$a", a);
                    undo.Parameters.AddWithValue("$b", b);
                    undo.ExecuteNonQuery();
                }
            }

            using (var update = Command(connection, tx, """
                       UPDATE deck_families SET
                           name = COALESCE($name, name),
                           main_key = CASE WHEN $main IS NOT NULL THEN $main WHEN main_key IS NULL THEN $first ELSE main_key END
                       WHERE id = $id;
                       """))
            {
                update.Parameters.AddWithValue("$name", string.IsNullOrWhiteSpace(name) ? DBNull.Value : name.Trim());
                update.Parameters.AddWithValue("$main", (object?)mainKey ?? DBNull.Value);
                update.Parameters.AddWithValue("$first", keys.First());
                update.Parameters.AddWithValue("$id", target);
                update.ExecuteNonQuery();
            }

            tx.Commit();
        }

        DeckFamiliesChanged?.Invoke();
        return target;
    }

    /// <summary>
    /// Takes a deck version out of its family; it is a deck of its own again and is not suggested for that family
    /// again. A family left with one version is dissolved.
    /// </summary>
    public void SplitDeck(string key)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            if (FamilyOf(connection, tx, key) is not { } family)
            {
                return;
            }

            using (var remove = Command(connection, tx, "DELETE FROM deck_family_members WHERE deck_key = $key; UPDATE deck_families SET main_key = NULL WHERE id = $id AND main_key = $key;"))
            {
                remove.Parameters.AddWithValue("$key", key);
                remove.Parameters.AddWithValue("$id", family);
                remove.ExecuteNonQuery();
            }

            var rest = Members(connection, tx, family);
            foreach (var other in rest)
            {
                InsertDismissed(connection, tx, key, other);
            }

            if (rest.Count <= 1)
            {
                using var drop = Command(connection, tx, "DELETE FROM deck_family_members WHERE family_id = $id; DELETE FROM deck_families WHERE id = $id;");
                drop.Parameters.AddWithValue("$id", family);
                drop.ExecuteNonQuery();
            }
            else
            {
                using var main = Command(connection, tx, "UPDATE deck_families SET main_key = $first WHERE id = $id AND main_key IS NULL;");
                main.Parameters.AddWithValue("$first", rest[0]);
                main.Parameters.AddWithValue("$id", family);
                main.ExecuteNonQuery();
            }

            tx.Commit();
        }

        DeckFamiliesChanged?.Invoke();
    }

    /// <summary>Names a family; null or blank goes back to the main version's own name.</summary>
    public void RenameDeckFamily(long id, string? name) =>
        UpdateFamily("UPDATE deck_families SET name = $value WHERE id = $id;", id, string.IsNullOrWhiteSpace(name) ? null : name.Trim());

    /// <summary>Makes one of the family's versions its main one (shown name, card list, archetype).</summary>
    public void SetDeckFamilyMain(long id, string key) =>
        UpdateFamily("UPDATE deck_families SET main_key = $value WHERE id = $id AND EXISTS (SELECT 1 FROM deck_family_members WHERE family_id = $id AND deck_key = $value);", id, key);

    /// <summary>Records that two deck versions are different decks, so they are not suggested for merging again.</summary>
    public void DismissDeckPair(string a, string b)
    {
        lock (_gate)
        {
            using var connection = Open();
            InsertDismissed(connection, null, a, b);
        }

        DeckFamiliesChanged?.Invoke();
    }

    private void UpdateFamily(string sql, long id, string? value)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, sql);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        DeckFamiliesChanged?.Invoke();
    }

    private static long? FamilyOf(SqliteConnection connection, SqliteTransaction? tx, string key)
    {
        using var command = Command(connection, tx, "SELECT family_id FROM deck_family_members WHERE deck_key = $key;");
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is long id ? id : null;
    }

    private static List<string> Members(SqliteConnection connection, SqliteTransaction? tx, long family)
    {
        using var command = Command(connection, tx, "SELECT deck_key FROM deck_family_members WHERE family_id = $id ORDER BY rowid;");
        command.Parameters.AddWithValue("$id", family);
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static void InsertDismissed(SqliteConnection connection, SqliteTransaction? tx, string a, string b)
    {
        if (a == b)
        {
            return;
        }

        var (first, second) = DeckPair(a, b);
        using var command = Command(connection, tx, "INSERT OR IGNORE INTO deck_family_dismissed (key_a, key_b) VALUES ($a, $b);");
        command.Parameters.AddWithValue("$a", first);
        command.Parameters.AddWithValue("$b", second);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? tx, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        return command;
    }
}
