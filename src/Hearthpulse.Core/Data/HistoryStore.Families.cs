using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Core.Data;

/// <summary>
/// A deck family as saved: deck versions (by version key, see <c>DeckFamilies.VersionKey</c>) the player merged into one
/// deck, its own name (null = the main version's name) and the main version.
/// </summary>
public sealed record DeckFamilyRecord(long Id, string? Name, string? MainKey, IReadOnlyList<string> Members);

/// <summary>
/// The saved state of some deck versions before a family change, so it can be undone: the families they were in (with
/// every member) and the "different decks" pairs among all of those versions.
/// </summary>
public sealed record DeckFamilySnapshot(
    IReadOnlyList<string> Keys,
    IReadOnlyList<(DeckFamilyRecord Family, string CreatedAt)> Families,
    IReadOnlyList<(string, string)> Dismissed);

/// <summary>Deck families: which deck versions the player counts as one deck.</summary>
public sealed partial class HistoryStore
{
    /// <summary>Raised after a family was merged, split, renamed or got a new main version (games are unchanged).</summary>
    public event Action? DeckFamiliesChanged;

    // Tables of their own, created on open like the games table, so an older history file gets them unchanged otherwise.
    // A deck version belongs to at most one family; a version in no family is a deck of its own.
    private static void CreateFamilyTables(SqliteConnection connection)
    {
        CreateFamilyTablesSql(connection);
        NormalizeFamilyKeys(connection);
    }

    /// <summary>
    /// Early builds keyed versions by the raw deck code; keys are card lists now (<see cref="DeckRecords.DeckKey"/>).
    /// A stored key that still decodes as a code is rewritten, so its family and dismissed pairs keep matching.
    /// </summary>
    private static void NormalizeFamilyKeys(SqliteConnection connection)
    {
        var codes = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT deck_key FROM deck_family_members UNION SELECT key_a FROM deck_family_dismissed UNION SELECT key_b FROM deck_family_dismissed;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                if (Cards.DeckString.TryDecode(key) is not null)
                {
                    codes.Add(key);
                }
            }
        }

        if (codes.Count == 0)
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        foreach (var code in codes)
        {
            CarryDeckKey(connection, tx, code, DeckRecords.CardListKey(code));
            using var drop = Command(connection, tx, "DELETE FROM deck_family_members WHERE deck_key = $old; DELETE FROM deck_family_dismissed WHERE key_a = $old OR key_b = $old;");
            drop.Parameters.AddWithValue("$old", code);
            drop.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void CreateFamilyTablesSql(SqliteConnection connection) => Execute(connection, """
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
    /// Puts the deck versions into one family: <paramref name="into"/> when one of them is in it (the deck the player is
    /// on keeps its name), else the oldest family any of them is in, else a new one. Other families they were in are
    /// folded into it. Returns the family id.
    /// </summary>
    public long MergeDecks(IReadOnlyCollection<string> keys, string? name = null, string? mainKey = null, long? into = null)
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

            if (into is { } preferred && involved.Contains(preferred))
            {
                target = preferred;
            }
            else if (involved.Count > 0)
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

    /// <summary>Records that every version of one deck and every version of another are different decks.</summary>
    public void DismissDecks(IReadOnlyCollection<string> first, IReadOnlyCollection<string> second)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            foreach (var a in first)
            {
                foreach (var b in second)
                {
                    InsertDismissed(connection, tx, a, b);
                }
            }

            tx.Commit();
        }

        DeckFamiliesChanged?.Invoke();
    }

    /// <summary>The families of these deck versions and the dismissed pairs among them, before a merge (see <see cref="RestoreDeckFamilies"/>).</summary>
    public DeckFamilySnapshot SnapshotDeckFamilies(IReadOnlyCollection<string> keys)
    {
        lock (_gate)
        {
            using var connection = Open();
            var ids = keys.Select(k => FamilyOf(connection, null, k)).OfType<long>().Distinct().Order().ToList();
            var families = new List<(DeckFamilyRecord, string)>();
            foreach (var id in ids)
            {
                using var command = Command(connection, null, "SELECT name, main_key, created_at FROM deck_families WHERE id = $id;");
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    families.Add((new DeckFamilyRecord(id, reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                        Members(connection, null, id)), reader.GetString(2)));
                }
            }

            var all = keys.Concat(families.SelectMany(f => f.Item1.Members)).ToHashSet(StringComparer.Ordinal);
            var dismissed = new List<(string, string)>();
            using (var command = Command(connection, null, "SELECT key_a, key_b FROM deck_family_dismissed;"))
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (all.Contains(reader.GetString(0)) && all.Contains(reader.GetString(1)))
                    {
                        dismissed.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }
            }

            return new DeckFamilySnapshot(all.Order(StringComparer.Ordinal).ToList(), families, dismissed);
        }
    }

    /// <summary>
    /// Puts the snapshot's versions back as they were: the families they are in now are dropped, the saved ones come
    /// back with their ids, names and main versions, and so do the dismissed pairs a merge cleared.
    /// </summary>
    public void RestoreDeckFamilies(DeckFamilySnapshot snapshot)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var now = snapshot.Keys.Select(k => FamilyOf(connection, tx, k)).OfType<long>()
                .Concat(snapshot.Families.Select(f => f.Family.Id))
                .Distinct()
                .ToList();
            foreach (var id in now)
            {
                using var drop = Command(connection, tx, "DELETE FROM deck_family_members WHERE family_id = $id; DELETE FROM deck_families WHERE id = $id;");
                drop.Parameters.AddWithValue("$id", id);
                drop.ExecuteNonQuery();
            }

            foreach (var key in snapshot.Keys)
            {
                using var loose = Command(connection, tx, "DELETE FROM deck_family_members WHERE deck_key = $key;");
                loose.Parameters.AddWithValue("$key", key);
                loose.ExecuteNonQuery();
            }

            foreach (var (family, createdAt) in snapshot.Families)
            {
                using (var insert = Command(connection, tx, "INSERT INTO deck_families (id, name, main_key, created_at) VALUES ($id, $name, $main, $at);"))
                {
                    insert.Parameters.AddWithValue("$id", family.Id);
                    insert.Parameters.AddWithValue("$name", (object?)family.Name ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$main", (object?)family.MainKey ?? DBNull.Value);
                    insert.Parameters.AddWithValue("$at", createdAt);
                    insert.ExecuteNonQuery();
                }

                foreach (var key in family.Members)
                {
                    using var add = Command(connection, tx, "INSERT OR REPLACE INTO deck_family_members (deck_key, family_id) VALUES ($key, $family);");
                    add.Parameters.AddWithValue("$key", key);
                    add.Parameters.AddWithValue("$family", family.Id);
                    add.ExecuteNonQuery();
                }
            }

            foreach (var (a, b) in snapshot.Dismissed)
            {
                InsertDismissed(connection, tx, a, b);
            }

            tx.Commit();
        }

        DeckFamiliesChanged?.Invoke();
    }

    /// <summary>The "name|CLASS" deck key of a game saved with a deck name and no code, else null.</summary>
    private static string? NameKeyOf(SqliteConnection connection, SqliteTransaction tx, string gameKey)
    {
        using var command = Command(connection, tx,
            "SELECT deck_name, player_class FROM games WHERE game_key = $key AND (deck_code IS NULL OR deck_code = '') AND deck_name IS NOT NULL AND deck_name <> '';");
        command.Parameters.AddWithValue("$key", gameKey);
        using var reader = command.ExecuteReader();
        return reader.Read() ? DeckRecords.NameKey(reader.GetString(0), reader.GetString(1)) : null;
    }

    /// <summary>
    /// Gives a new deck key the family and dismissed pairs of an old one (a game of the old version got its code). The
    /// old key keeps them too: other games of that version may still have no code.
    /// </summary>
    private static void CarryDeckKey(SqliteConnection connection, SqliteTransaction tx, string oldKey, string newKey)
    {
        if (oldKey == newKey)
        {
            return;
        }

        using (var member = Command(connection, tx, """
                   INSERT OR IGNORE INTO deck_family_members (deck_key, family_id)
                   SELECT $new, family_id FROM deck_family_members WHERE deck_key = $old;
                   UPDATE deck_families SET main_key = $new WHERE main_key = $old;
                   """))
        {
            member.Parameters.AddWithValue("$old", oldKey);
            member.Parameters.AddWithValue("$new", newKey);
            member.ExecuteNonQuery();
        }

        var others = new List<string>();
        using (var pairs = Command(connection, tx, "SELECT key_a, key_b FROM deck_family_dismissed WHERE key_a = $old OR key_b = $old;"))
        {
            pairs.Parameters.AddWithValue("$old", oldKey);
            using var reader = pairs.ExecuteReader();
            while (reader.Read())
            {
                others.Add(reader.GetString(0) == oldKey ? reader.GetString(1) : reader.GetString(0));
            }
        }

        foreach (var other in others)
        {
            InsertDismissed(connection, tx, newKey, other);
        }
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
