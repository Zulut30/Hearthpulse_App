using System.Globalization;
using System.Text;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Stats;

/// <summary>
/// One exact list the player took into games: a deck code, or for games without one the deck's name and class.
/// </summary>
/// <param name="Cards">dbfId to copies; empty without a deck code.</param>
/// <param name="Games">Oldest first.</param>
public sealed record DeckVersion(
    string Key,
    string Name,
    string PlayerClass,
    string Format,
    string? DeckCode,
    long? DeckId,
    IReadOnlyDictionary<int, int> Cards,
    IReadOnlyList<GameRecord> Games,
    WinLoss Record,
    DateTime FirstPlayed,
    DateTime LastPlayed);

/// <summary>
/// A deck as the player thinks of it: the versions merged into one family, or a single version. Every stat of the
/// deck page counts all of its games.
/// </summary>
/// <param name="Key">"family:&lt;id&gt;" for a saved family, else the version's key.</param>
/// <param name="Versions">The main version first, then the most recently played.</param>
/// <param name="Games">Oldest first.</param>
public sealed record DeckFamily(
    string Key,
    long? Id,
    string Name,
    string PlayerClass,
    string Format,
    DeckVersion Main,
    IReadOnlyList<DeckVersion> Versions,
    IReadOnlyList<GameRecord> Games,
    WinLoss Record,
    DateTime LastPlayed)
{
    public bool IsMerged => Versions.Count > 1;
}

/// <summary>Two decks that look like versions of one: same class and format, mostly the same cards.</summary>
/// <param name="Overlap">Share of cards in common (copies counted), 0..1.</param>
public sealed record MergeSuggestion(DeckFamily First, DeckFamily Second, double Overlap, bool SameDeckId, bool SimilarNames);

/// <summary>Another deck of the same class and format the player can add to a family by hand.</summary>
/// <param name="Overlap">Share of cards in common with the family (best pair of versions), 0..1; 0 without card lists.</param>
/// <param name="Dismissed">The player said earlier it is another deck ("Not the same deck" or a split).</param>
public sealed record MergeCandidate(DeckFamily Family, double Overlap, bool Dismissed);

/// <summary>A card whose copies differ between two versions: positive = more in the newer one.</summary>
public sealed record CardDelta(int DbfId, int Delta);

/// <summary>Deck versions, their families and merge suggestions: pure functions over saved games.</summary>
public static class DeckFamilies
{
    /// <summary>Share of common cards from which two decks of one class and format are suggested as one deck.</summary>
    public const double SuggestOverlap = 0.8;

    /// <summary>Lower share that is still suggested when the names are alike or the client's deck id is the same.</summary>
    public const double SuggestOverlapWithHint = 0.7;

    public const string FamilyPrefix = "family:";

    /// <summary>Games played with a Constructed deck of the player's own (Arena runs and Brawls are not decks).</summary>
    public static bool IsDeckGame(GameRecord game) =>
        game.Mode is "Ranked" or "Casual" or "Friendly" or "Practice" && !game.IsAbandoned
        && (!string.IsNullOrEmpty(game.DeckCode) || !string.IsNullOrEmpty(game.DeckName));

    /// <summary>The version a game was played with: <see cref="DeckRecords.DeckKey"/> (card list, else "name|CLASS").</summary>
    public static string VersionKey(GameRecord game) => DeckRecords.DeckKey(game);

    /// <summary>Every deck version of the games, most recently played first.</summary>
    public static IReadOnlyList<DeckVersion> Versions(IEnumerable<GameRecord> games) =>
        games.Where(IsDeckGame)
            .GroupBy(VersionKey)
            .Select(group =>
            {
                var ordered = group.OrderBy(g => g.StartedAt).ToList();
                var latest = ordered[^1];
                var code = latest.DeckCode is { Length: > 0 } c ? c : null;
                return new DeckVersion(
                    group.Key,
                    latest.DeckName is { Length: > 0 } name ? name : "Deck",
                    latest.PlayerClass,
                    latest.Format,
                    code,
                    ordered.LastOrDefault(g => g.DeckId is > 0)?.DeckId,
                    code is null ? new Dictionary<int, int>() : DeckString.TryDecode(code)?.Cards ?? new Dictionary<int, int>(),
                    ordered,
                    WinLoss.Of(ordered),
                    ordered[0].StartedAt,
                    latest.StartedAt);
            })
            .OrderByDescending(v => v.LastPlayed)
            .ToList();

    /// <summary>
    /// The decks to show: saved families with the versions they hold that have games, and every other version as a
    /// deck of its own. Most recently played first.
    /// </summary>
    public static IReadOnlyList<DeckFamily> Build(IReadOnlyList<DeckVersion> versions, IReadOnlyList<DeckFamilyRecord> stored)
    {
        var byKey = versions.ToDictionary(v => v.Key, StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var families = new List<DeckFamily>();
        foreach (var record in stored)
        {
            var members = record.Members.Where(byKey.ContainsKey).Where(taken.Add).Select(k => byKey[k]).ToList();
            if (members.Count > 0)
            {
                families.Add(Family(FamilyPrefix + record.Id.ToString(CultureInfo.InvariantCulture), record.Id, record.Name, record.MainKey, members));
            }
        }

        families.AddRange(versions.Where(v => !taken.Contains(v.Key)).Select(v => Family(v.Key, null, null, null, [v])));
        return families.OrderByDescending(f => f.LastPlayed).ToList();
    }

    private static DeckFamily Family(string key, long? id, string? name, string? mainKey, List<DeckVersion> members)
    {
        var main = members.FirstOrDefault(v => v.Key == mainKey) ?? members.MaxBy(v => v.LastPlayed)!;
        var ordered = new List<DeckVersion> { main };
        ordered.AddRange(members.Where(v => v != main).OrderByDescending(v => v.LastPlayed));
        var games = members.SelectMany(v => v.Games).OrderBy(g => g.StartedAt).ToList();
        return new DeckFamily(
            key,
            id,
            string.IsNullOrWhiteSpace(name) ? main.Name : name,
            main.PlayerClass,
            main.Format,
            main,
            ordered,
            games,
            WinLoss.Of(games),
            members.Max(v => v.LastPlayed));
    }

    /// <summary>Cards in common, copies counted, over the larger list (0 when either list is unknown).</summary>
    public static double Overlap(DeckVersion a, DeckVersion b)
    {
        var sizeA = a.Cards.Values.Sum();
        var sizeB = b.Cards.Values.Sum();
        if (sizeA == 0 || sizeB == 0)
        {
            return 0;
        }

        var shared = a.Cards.Sum(c => Math.Min(c.Value, b.Cards.GetValueOrDefault(c.Key)));
        return (double)shared / Math.Max(sizeA, sizeB);
    }

    /// <summary>Cards in common between two decks: the best pair of their versions.</summary>
    public static double Overlap(DeckFamily a, DeckFamily b) =>
        a.Versions.SelectMany(x => b.Versions.Select(y => Overlap(x, y))).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The other decks of <paramref name="family"/>'s class and format that can be added to it, most alike first (then
    /// the most recently played). <paramref name="search"/> keeps the decks whose name or a version's name holds it.
    /// </summary>
    public static IReadOnlyList<MergeCandidate> Candidates(DeckFamily family, IReadOnlyList<DeckFamily> families,
        IReadOnlySet<(string, string)> dismissed, string? search = null)
    {
        var query = search?.Trim() ?? string.Empty;
        return families
            .Where(f => f.Key != family.Key
                        && string.Equals(f.PlayerClass, family.PlayerClass, StringComparison.Ordinal)
                        && string.Equals(f.Format, family.Format, StringComparison.Ordinal))
            .Where(f => query.Length == 0
                        || f.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                        || f.Versions.Any(v => v.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .Select(f => new MergeCandidate(f, Overlap(family, f),
                family.Versions.Any(x => f.Versions.Any(y => dismissed.Contains(HistoryStore.DeckPair(x.Key, y.Key))))))
            .OrderByDescending(c => c.Overlap)
            .ThenByDescending(c => c.Family.LastPlayed)
            .ThenBy(c => c.Family.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// True for names that differ only in case, digits, punctuation or spacing ("Death Knight2", "Death Knight 3"), or
    /// when one holds the other ("Quest Priest", "Quest Priest v2").
    /// </summary>
    public static bool SimilarNames(string a, string b)
    {
        var x = Letters(a);
        var y = Letters(b);
        if (x.Length == 0 || y.Length == 0)
        {
            return false;
        }

        return x == y || (Math.Min(x.Length, y.Length) >= 4 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)));
    }

    private static string Letters(string name)
    {
        var text = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsLetter(ch))
            {
                text.Append(char.ToLowerInvariant(ch));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Pairs of decks (families or single versions) of one class and format that look like versions of one deck:
    /// <see cref="SuggestOverlap"/> of the cards in common, or <see cref="SuggestOverlapWithHint"/> with an alike name
    /// or the same client deck id. Pairs the player called different (<paramref name="dismissed"/>, ordered with
    /// <see cref="HistoryStore.DeckPair"/>) are left out. Best match first.
    /// </summary>
    public static IReadOnlyList<MergeSuggestion> Suggest(IReadOnlyList<DeckFamily> families, IReadOnlySet<(string, string)> dismissed)
    {
        var suggestions = new List<MergeSuggestion>();
        for (var i = 0; i < families.Count; i++)
        {
            for (var j = i + 1; j < families.Count; j++)
            {
                var (a, b) = (families[i], families[j]);
                if (!string.Equals(a.PlayerClass, b.PlayerClass, StringComparison.Ordinal) || !string.Equals(a.Format, b.Format, StringComparison.Ordinal)
                    || a.Versions.Any(x => b.Versions.Any(y => dismissed.Contains(HistoryStore.DeckPair(x.Key, y.Key)))))
                {
                    continue;
                }

                var overlap = Overlap(a, b);
                var sameId = a.Versions.Any(x => x.DeckId is > 0 && b.Versions.Any(y => y.DeckId == x.DeckId));
                var similar = a.Versions.Any(x => b.Versions.Any(y => SimilarNames(x.Name, y.Name))) || SimilarNames(a.Name, b.Name);
                if (overlap >= SuggestOverlap || (overlap >= SuggestOverlapWithHint && (sameId || similar)))
                {
                    suggestions.Add(new MergeSuggestion(a, b, overlap, sameId, similar));
                }
            }
        }

        return suggestions
            .OrderByDescending(s => s.SameDeckId)
            .ThenByDescending(s => s.Overlap)
            .ToList();
    }

    /// <summary>
    /// <paramref name="matched"/> (a deck's games, e.g. <see cref="DeckRecords.Matching"/>) plus every game of the saved
    /// families its versions belong to, so the in-game deck record counts the same games as the deck page. Oldest first.
    /// </summary>
    public static IReadOnlyList<GameRecord> WithFamily(IEnumerable<GameRecord> all, IReadOnlyList<GameRecord> matched, IReadOnlyList<DeckFamilyRecord> stored)
    {
        var keys = matched.Select(VersionKey).ToHashSet(StringComparer.Ordinal);
        var members = stored.Where(f => f.Members.Any(keys.Contains)).SelectMany(f => f.Members).ToHashSet(StringComparer.Ordinal);
        if (members.Count == 0)
        {
            return matched;
        }

        var seen = matched.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return matched.Concat(all.Where(g => IsDeckGame(g) && members.Contains(VersionKey(g)) && seen.Add(g.Key)))
            .OrderBy(g => g.StartedAt)
            .ToList();
    }

    /// <summary>
    /// Cards added (+) and taken out (−) going from one version to another, most changed first. Empty when either list
    /// is unknown (a version saved without a deck code), not "all cards changed".
    /// </summary>
    public static IReadOnlyList<CardDelta> Diff(DeckVersion from, DeckVersion to) => from.Cards.Count == 0 || to.Cards.Count == 0
        ? []
        : from.Cards.Keys.Union(to.Cards.Keys)
            .Select(dbf => new CardDelta(dbf, to.Cards.GetValueOrDefault(dbf) - from.Cards.GetValueOrDefault(dbf)))
            .Where(d => d.Delta != 0)
            .OrderByDescending(d => d.Delta > 0)
            .ThenByDescending(d => Math.Abs(d.Delta))
            .ThenBy(d => d.DbfId)
            .ToList();
}
