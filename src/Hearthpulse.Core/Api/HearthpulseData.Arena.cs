using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Arena;

namespace Hearthpulse.Core.Api;

/// <summary>Arena numbers: ArenaSmith card scores and class results (docs/arena-draft.md).</summary>
public sealed partial class HearthpulseData
{
    /// <summary>The published Arena card dataset: one all-classes slice with an ArenaSmith score per card.</summary>
    internal const string ArenaCardsPath = "datasets/hsreplay_arena_cards_advanced";

    /// <summary>Class win rates, pick rates and 7+ win shares.</summary>
    internal const string ArenaClassesPath = "v1/arena/classes?limit=20";

    /// <summary>Card scores and class results; empty parts when the server and the cache have nothing. Never throws.</summary>
    public async Task<ArenaMeta> LoadArenaMetaAsync(CancellationToken cancellationToken = default)
    {
        var cards = LoadArenaCardsAsync(cancellationToken);
        var classes = LoadArenaClassesAsync(cancellationToken);
        var (scores, fetchedAt) = await cards.ConfigureAwait(false);
        return new ArenaMeta(scores, await classes.ConfigureAwait(false), fetchedAt);
    }

    private async Task<(IReadOnlyList<ArenaCardScore> Cards, DateTime? FetchedAt)> LoadArenaCardsAsync(CancellationToken cancellationToken)
    {
        (IReadOnlyList<ArenaCardScore>, DateTime?) empty = ([], null);
        try
        {
            using var doc = await FetchAsync(ArenaCardsPath, "arena-cards.json", cancellationToken, ShrinkArenaCards).ConfigureAwait(false);
            return doc is null ? empty : Read(() => ParseArenaCards(Data(doc)), "arena cards", empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return empty;
        }
    }

    private async Task<IReadOnlyList<ArenaClassStat>> LoadArenaClassesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await FetchAsync(ArenaClassesPath, "arena-classes.json", cancellationToken).ConfigureAwait(false);
            return doc is null ? [] : Read(() => ParseArenaClasses(Data(doc)), "arena classes", (IReadOnlyList<ArenaClassStat>)[]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>
    /// The card dataset is about 1.7 MB, most of it repeated (the list is published twice) or unused by the app: the
    /// cache keeps the fetch time and, per card, the id, dbf id, score, win rate, pick rate and games.
    /// </summary>
    internal static object ShrinkArenaCards(JsonElement data)
    {
        var structured = Structured(data);
        if (structured.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, object?> { ["cards"] = Array.Empty<object>() };
        }

        var cards = Items(structured.TryGetProperty("cards", out var list) ? list : default)
            .Select(c => new Dictionary<string, object?>
            {
                ["id"] = Str(c, "card_id") ?? Str(c, "id"),
                ["dbfId"] = Num(c, "dbfId"),
                ["score"] = Num(c, "score"),
                ["win_rate"] = Num(c, "win_rate"),
                ["pick_rate"] = Num(c, "pick_rate"),
                ["times_played"] = Num(c, "times_played"),
            })
            .ToList();
        var observed = structured.TryGetProperty("upstream_freshness", out var fresh) ? Str(fresh, "observed_at") : null;
        return new Dictionary<string, object?> { ["fetched_at"] = observed, ["cards"] = cards };
    }

    /// <summary>
    /// Parses the card answer, whole (<c>structured.cards</c>) or as the cache keeps it (<c>cards</c>). Percentages
    /// arrive as 0-100; a card without an id is skipped.
    /// </summary>
    internal static (IReadOnlyList<ArenaCardScore> Cards, DateTime? FetchedAt) ParseArenaCards(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return ([], null);
        }

        var root = data.TryGetProperty("cards", out _) ? data : Structured(data);
        var fetchedAt = Str(root, "fetched_at")
                        ?? (root.TryGetProperty("upstream_freshness", out var fresh) ? Str(fresh, "observed_at") : null);
        var cards = new List<ArenaCardScore>();
        foreach (var c in Items(root.TryGetProperty("cards", out var list) ? list : default))
        {
            var id = Str(c, "card_id") ?? Str(c, "id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            cards.Add(new ArenaCardScore(
                id,
                (int)(Num(c, "dbfId") ?? 0),
                Num(c, "score") is { } score && double.IsFinite(score) ? score : null,
                Num(c, "win_rate") / 100,
                Num(c, "pick_rate") / 100,
                (int)(Num(c, "times_played") ?? 0)));
        }

        return (cards, ParseTime(fetchedAt));
    }

    /// <summary>Parses <c>v1/arena/classes</c>: <c>class</c>, <c>win_rate</c>, <c>pick_rate</c>, <c>pct_7_plus</c> (0-100), <c>num_drafts</c>.</summary>
    internal static IReadOnlyList<ArenaClassStat> ParseArenaClasses(JsonElement data)
    {
        var result = new List<ArenaClassStat>();
        foreach (var c in Items(data))
        {
            if (ArenaMeta.ClassKey(Str(c, "class") ?? Str(c, "slug")) is not { } key || Num(c, "win_rate") is not { } winRate)
            {
                continue;
            }

            result.Add(new ArenaClassStat(key, winRate / 100, Num(c, "pick_rate") / 100, Num(c, "pct_7_plus") / 100, (int)(Num(c, "num_drafts") ?? 0)));
        }

        return result;
    }

    private static JsonElement Structured(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("structured", out var s) && s.ValueKind == JsonValueKind.Object ? s : data;

    private static DateTime? ParseTime(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ? time : null;
}
