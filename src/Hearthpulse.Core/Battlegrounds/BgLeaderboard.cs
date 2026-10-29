using System.Text;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>
/// One day's public Blizzard Battlegrounds leaderboard of a region, every row of it (about 7000 in EU, from 8000
/// rating up), for matching lobby names on this computer (docs/bg-opponent-mmr.md). Leaderboard names carry no
/// "#1234", so two players can share one: a rating is given only when the name is on exactly one row, spelled the same
/// way, case included; anything less certain is "?" (L-m1, L-m5).
/// </summary>
public sealed class BgLeaderboard
{
    // The rating per name exactly as written (trimmed, one Unicode form), and how many rows have the name in any case.
    private readonly Dictionary<string, int> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _anyCase = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, int Rating)> _rows = [];

    /// <param name="day">The snapshot day the server reported ("2026-10-29"), when known.</param>
    /// <param name="takenAt">When the server took the snapshot (UTC), else when it was downloaded.</param>
    /// <param name="complete">False when the download stopped before the end of the table.</param>
    public BgLeaderboard(IEnumerable<(string Name, int Rating)> rows, string? day = null, DateTime? takenAt = null, bool complete = true)
    {
        Day = day;
        TakenAt = takenAt;
        foreach (var (name, rating) in rows)
        {
            var key = Key(name);
            if (key.Length == 0)
            {
                continue;
            }

            _rows.Add((key, rating));
            _exact.TryAdd(key, rating);
            _anyCase[key] = _anyCase.GetValueOrDefault(key) + 1;
        }

        Floor = _rows.Count == 0 ? null : _rows.Min(r => r.Rating);
        Complete = complete && _rows.Count > 0;
    }

    public string? Day { get; }

    /// <summary>When the server took the snapshot (UTC), else when it was downloaded; the table's age counts from it.</summary>
    public DateTime? TakenAt { get; }

    /// <summary>
    /// The whole table is here. An empty table (a new season, a failing mirror) or one cut short matches nobody: a name
    /// missing from it says nothing about the player's rating.
    /// </summary>
    public bool Complete { get; }

    /// <summary>The table's lowest rating (8000 on every table so far): a name not on it is "&lt;Floor". Null when empty.</summary>
    public int? Floor { get; }

    /// <summary>How many rows the table holds.</summary>
    public int Count => _rows.Count;

    /// <summary>The rows as read, for the disk cache.</summary>
    internal IReadOnlyList<(string Name, int Rating)> Rows => _rows;

    /// <summary>
    /// The rating of <paramref name="name"/>: one row with exactly this name gives its rating; a row that has it only in
    /// another case, or several rows, give <see cref="BgMmrStatus.Ambiguous"/>; none gives
    /// <see cref="BgMmrStatus.BelowThreshold"/>. No name, or a table that is not <see cref="Complete"/>, gives
    /// <see cref="BgMmrStatus.Unknown"/>. Every answer from the table carries its <see cref="Floor"/>.
    /// </summary>
    public BgOpponentMmr Match(string? name)
    {
        var key = name is null ? string.Empty : Key(name);
        if (key.Length == 0 || !Complete)
        {
            return new BgOpponentMmr(BgMmrStatus.Unknown, null);
        }

        if (!_anyCase.TryGetValue(key, out var rows))
        {
            return new BgOpponentMmr(BgMmrStatus.BelowThreshold, null) { Floor = Floor };
        }

        return rows == 1 && _exact.TryGetValue(key, out var rating)
            ? new BgOpponentMmr(BgMmrStatus.Found, rating) { Floor = Floor }
            : new BgOpponentMmr(BgMmrStatus.Ambiguous, null) { Floor = Floor };
    }

    /// <summary>Whether two lobby names could be the same leaderboard name: compared as <see cref="Match"/> compares them, in any case.</summary>
    public static bool SameName(string a, string b) => string.Equals(Key(a), Key(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>A name as both sides are compared: trimmed and in one Unicode form (a name typed with combining marks matches).</summary>
    private static string Key(string name) => name.Trim().Normalize(NormalizationForm.FormC);
}
