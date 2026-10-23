using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Api;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// One pool Hearthstone Wiki lists for a generator ("Discovered Blood Rune cards"), as the Hearthpulse API serves it
/// (<c>api/v1/constructed-cards/{card_id}/wiki</c>, <c>wiki.generated_card_pools</c>).
/// </summary>
/// <param name="HeroClasses">Classes the wiki query was run for (CLASS keys such as DEATHKNIGHT); empty when not stated.</param>
/// <param name="Format">The wiki query's format ("Standard"), or null when not stated.</param>
public sealed record WikiPool(string Name, IReadOnlyList<string> CardIds, IReadOnlySet<string> HeroClasses, string? Format)
{
    /// <summary>The pool's name without the wiki's wording around it: "Discovered Blood Rune cards" -> "Blood Rune".</summary>
    public string Label => WikiPools.LabelOf(Name);
}

/// <summary>A generator's wiki pools and when the server last read the wiki page.</summary>
public sealed record CardWiki(IReadOnlyList<WikiPool> Pools, DateTime? FetchedAt)
{
    public static CardWiki None { get; } = new([], null);
}

/// <summary>Reads the wiki answer of the Hearthpulse API.</summary>
public static partial class WikiPools
{
    public const string Source = "Hearthstone Wiki";

    /// <summary>
    /// The <c>data</c> of a wiki answer. A pool lists its cards in <c>cards</c> (objects with <c>card_id</c>) or in
    /// <c>card_ids</c>; ids are trimmed and kept once. Pools without cards are dropped.
    /// </summary>
    public static CardWiki Parse(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return CardWiki.None;
        }

        var pools = new List<WikiPool>();
        if (data.TryGetProperty("generated_card_pools", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var pool in list.EnumerateArray())
            {
                if (pool.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var ids = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in CardIdsOf(pool))
                {
                    if (id.Length > 0 && seen.Add(id))
                    {
                        ids.Add(id);
                    }
                }

                if (ids.Count == 0)
                {
                    continue;
                }

                var name = Str(pool, "pool") ?? Str(pool, "name") ?? "Generated cards";
                var (classes, format) = QueryOf(Str(pool, "query_url"));
                pools.Add(new WikiPool(name, ids, classes, format));
            }
        }

        var fetched = Str(data, "fetched_at") is { } text && DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at) ? at : (DateTime?)null;
        return new CardWiki(pools, fetched);
    }

    /// <summary>"Discovered Blood Rune cards" -> "Blood Rune"; a name without that wording stays as it is.</summary>
    public static string LabelOf(string name)
    {
        var label = LabelRegex().Replace(name.Trim(), string.Empty).Trim();
        return label.Length > 0 ? label : name;
    }

    private static IEnumerable<string> CardIdsOf(JsonElement pool)
    {
        if (pool.TryGetProperty("cards", out var cards) && cards.ValueKind == JsonValueKind.Array && cards.GetArrayLength() > 0)
        {
            foreach (var card in cards.EnumerateArray())
            {
                if ((Str(card, "card_id") ?? Str(card, "id"))?.Trim() is { } id)
                {
                    yield return id;
                }
            }

            yield break;
        }

        if (pool.TryGetProperty("card_ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind == JsonValueKind.String && id.GetString()?.Trim() is { } value)
                {
                    yield return value;
                }
            }
        }
    }

    /// <summary>The hero classes and format of the wiki query a pool came from (its Special:RunQuery URL).</summary>
    private static (IReadOnlySet<string> Classes, string? Format) QueryOf(string? url)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        if (url is null)
        {
            return (classes, null);
        }

        var query = Uri.UnescapeDataString(url.Replace('+', ' '));
        if (ClassesRegex().Match(query) is { Success: true } c)
        {
            foreach (var name in c.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                classes.Add(name.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant());
            }
        }

        return (classes, FormatRegex().Match(query) is { Success: true } f ? f.Groups[1].Value.Trim() : null);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"^(?:Discovered|Generated|Summoned|Possible|Random)\s+|\s+cards?$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelRegex();

    [GeneratedRegex(@"\[options_heroClasses\]=([^&]*)")]
    private static partial Regex ClassesRegex();

    [GeneratedRegex(@"\[options_format\]=([^&]*)")]
    private static partial Regex FormatRegex();
}

/// <summary>
/// Loads generators' wiki pools in the background (disk cache through <see cref="HearthpulseData"/>), so the log thread
/// never waits on the network: <see cref="TryGet"/> answers from memory and asks for a card it does not know yet.
/// </summary>
public sealed class WikiPoolSource(HearthpulseData data)
{
    /// <summary>A failed lookup is tried again after this long.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, CardWiki> _known = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _asked = new(StringComparer.Ordinal);

    /// <summary>Raised on a worker thread when a card's pools arrived (the overlay builds its pool again).</summary>
    public event Action? Changed;

    /// <summary>True with the card's wiki when it is loaded; otherwise starts loading it and returns false.</summary>
    public bool TryGet(string cardId, out CardWiki wiki)
    {
        if (_known.TryGetValue(cardId, out wiki!))
        {
            return true;
        }

        Request(cardId);
        wiki = CardWiki.None;
        return false;
    }

    /// <summary>Starts loading a card's pools unless they are known or already asked for (a deck's generators at game start).</summary>
    public void Request(string cardId)
    {
        var now = DateTime.UtcNow;
        if (_known.ContainsKey(cardId) || _asked.TryGetValue(cardId, out var at) && now - at < RetryAfter)
        {
            return;
        }

        _asked[cardId] = now;
        Task.Run(async () =>
        {
            try
            {
                await LoadAsync(cardId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                Diag.Info($"Card pool: no Hearthstone Wiki pools for {cardId} ({ex.Message})");
            }
        }).Forget("Hearthstone Wiki pool lookup");
    }

    /// <summary>Loads (or reads from the cache) a card's pools and keeps them; the <c>--render-ui</c> scenes wait on it.</summary>
    public async Task<CardWiki> LoadAsync(string cardId, CancellationToken cancellationToken = default)
    {
        if (_known.TryGetValue(cardId, out var known))
        {
            return known;
        }

        var wiki = await data.LoadCardWikiAsync(cardId, cancellationToken).ConfigureAwait(false);
        if (wiki is null)
        {
            return CardWiki.None;
        }

        _known[cardId] = wiki;
        if (wiki.Pools.Count > 0)
        {
            Changed?.Invoke();
        }

        return wiki;
    }
}
