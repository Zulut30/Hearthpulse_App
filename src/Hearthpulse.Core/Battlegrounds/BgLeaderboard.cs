using System.Text;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>
/// One day's public Blizzard Battlegrounds leaderboard of a region, every row of it (about 7000 in EU, from 8000
/// rating up), for matching lobby names on this computer (docs/bg-opponent-mmr.md). Leaderboard names carry no
/// "#1234", so two players can share one: such a name gets no rating rather than the better one (L-m1).
/// </summary>
public sealed class BgLeaderboard
{
    // Per name (any case): the rating of its row and how many rows have that name.
    private readonly Dictionary<string, (int Rating, int Rows)> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Name, int Rating)> _rows = [];

    /// <param name="day">The snapshot day the server reported ("2026-10-29"), when known.</param>
    public BgLeaderboard(IEnumerable<(string Name, int Rating)> rows, string? day = null)
    {
        Day = day;
        foreach (var (name, rating) in rows)
        {
            var key = Key(name);
            if (key.Length == 0)
            {
                continue;
            }

            _rows.Add((key, rating));
            _byName[key] = _byName.TryGetValue(key, out var seen) ? (seen.Rating, seen.Rows + 1) : (rating, 1);
        }
    }

    public string? Day { get; }

    /// <summary>How many rows the table holds.</summary>
    public int Count => _rows.Count;

    /// <summary>The rows as read, for the disk cache.</summary>
    internal IReadOnlyList<(string Name, int Rating)> Rows => _rows;

    /// <summary>
    /// The rating of <paramref name="name"/>, matched without regard to case: one row gives its rating, several give
    /// <see cref="BgMmrStatus.Ambiguous"/>, none gives <see cref="BgMmrStatus.BelowThreshold"/> (the table ends at its
    /// floor), no name gives <see cref="BgMmrStatus.Unknown"/>.
    /// </summary>
    public BgOpponentMmr Match(string? name)
    {
        var key = name is null ? string.Empty : Key(name);
        if (key.Length == 0)
        {
            return new BgOpponentMmr(BgMmrStatus.Unknown, null);
        }

        return _byName.TryGetValue(key, out var found)
            ? found.Rows == 1 ? new BgOpponentMmr(BgMmrStatus.Found, found.Rating) : new BgOpponentMmr(BgMmrStatus.Ambiguous, null)
            : new BgOpponentMmr(BgMmrStatus.BelowThreshold, null);
    }

    /// <summary>A name as both sides are compared: trimmed and in one Unicode form (a name typed with combining marks matches).</summary>
    private static string Key(string name) => name.Trim().Normalize(NormalizationForm.FormC);
}
