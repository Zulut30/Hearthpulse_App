using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hearthpulse.Core.Battlegrounds.Site;

/// <summary>
/// Reads the answers of the site's Battlegrounds service (docs/bg-site-stats.md) into the <see cref="BgSiteStats"/>
/// records. Only lists whose source is on the allow list are read; the rest is cut from an answer before it is cached. Nothing
/// here throws on a malformed answer: the list or the entry is dropped with a line in the log.
/// </summary>
internal static partial class BgSiteParser
{
    internal static readonly string[] TierLetters = ["S", "A", "B", "C", "D"];

    private const int MaxNameLength = 120;

    private const int MaxTipLength = 600;

    /// <summary>Card ids become dictionary keys and art file names: only what a card id looks like.</summary>
    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CardIdPattern { get; }

    /// <summary>Slice names such as "TOP_50_PERCENT".</summary>
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern { get; }

    /// <summary>What <c>/api/bg/heroes</c> adds to the heroes tier list: card ids and placement shares by dbfId, and the slice.</summary>
    internal sealed record HeroDetails(IReadOnlyDictionary<int, (string CardId, double? First, double? Top4)> ByDbf, string? Mmr, string? TimeRange);

    /// <summary>Null when the answer is refused by source or has no hero rows.</summary>
    internal static HeroDetails? ParseHeroes(JsonElement root)
    {
        if (HeroRows(root, "heroes", slice: false) is not { } rows)
        {
            return null;
        }

        var byDbf = new Dictionary<int, (string, double?, double?)>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && CardId(row, "id") is { } id && Int(row, "dbfId") is { } dbf)
            {
                var (first, top) = Places(row);
                byDbf[dbf] = (id, first, top);
            }
        }

        var filters = Filters(root);
        return byDbf.Count == 0 ? null : new HeroDetails(byDbf, Key(filters, "mmr_percentile"), Key(filters, "time_range"));
    }

    /// <summary>
    /// A heroes list of one mode and MMR slice (<c>/api/bg/heroes?mode=&amp;mmr=</c>): the answer carries the tier of
    /// each hero itself, and no source: <paramref name="baseSource"/> is the allowed source the answer of the same
    /// address without parameters named. Heroes the slice has no tier for (too few games) are left out. Null when the
    /// answer has no hero rows.
    /// </summary>
    internal static BgSiteTierList? ParseHeroSlice(JsonElement root, string name, string baseSource)
    {
        if (HeroRows(root, name, slice: true) is not { } rows)
        {
            return null;
        }

        var byTier = TierLetters.ToDictionary(t => t, _ => new List<BgSiteTierEntry>(), StringComparer.Ordinal);
        var dropped = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || CardId(row, "id") is not { } id || Str(row, "tier") is not { } tier || !byTier.TryGetValue(tier, out var bucket))
            {
                dropped++;
                continue;
            }

            var english = Clean(Str(row, "hero"), MaxNameLength) ?? string.Empty;
            var (first, top) = Places(row);
            bucket.Add(new BgSiteTierEntry(id, Int(row, "dbfId") ?? 0, tier, english, english, 0,
                Placement(row, "avg_placement"), Share(row, "pick_rate"), top, first, Games: null));
        }

        if (dropped > 0)
        {
            Diag.Info($"Battlegrounds site {name}: {dropped} heroes without a card id or a tier left out");
        }

        var entries = TierLetters.SelectMany(t => byTier[t]).ToList();
        var filters = Filters(root);
        return entries.Count == 0
            ? null
            : new BgSiteTierList(BgSiteList.Heroes, entries, TierLetters, Time(root, "fetched_at") ?? default, Str(root, "source_id") ?? baseSource,
                Key(filters, "mmr_percentile"), Key(filters, "time_range"), GamesNote: null);
    }

    /// <summary>The hero rows of a heroes answer whose source is allowed; null (logged) otherwise.</summary>
    private static JsonElement? HeroRows(JsonElement root, string name, bool slice)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (slice ? !SliceSourceOk(Str(root, "source_id"), Str(root, "site")) : !HeroesUsable(Str(root, "source_id"), Str(root, "site"), Str(root, "url")))
        {
            Diag.Info($"Battlegrounds site {name}: source \"{Clean(Str(root, "source_id"), 80)}\" is not used");
            return null;
        }

        if (!root.TryGetProperty("view", out var view) || view.ValueKind != JsonValueKind.Object
            || !view.TryGetProperty("heroes", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            Diag.Info($"Battlegrounds site {name}: no hero rows");
            return null;
        }

        return rows;
    }

    private static JsonElement Filters(JsonElement root) =>
        root.TryGetProperty("view", out var view) && view.ValueKind == JsonValueKind.Object
        && view.TryGetProperty("filters", out var filters) && filters.ValueKind == JsonValueKind.Object
            ? filters
            : default;

    /// <summary>
    /// The share of first places and of the top half (places 1-4 of 8 solo, 1-2 of 4 in Duos) from a hero's
    /// <c>placement_distribution</c>; nulls when it is not a full list of percents.
    /// </summary>
    private static (double? First, double? TopHalf) Places(JsonElement row)
    {
        if (!row.TryGetProperty("placement_distribution", out var places) || places.ValueKind != JsonValueKind.Array || places.GetArrayLength() is not (8 or 4))
        {
            return (null, null);
        }

        var shares = places.EnumerateArray().Select(Share).ToList();
        return shares.TrueForAll(s => s is not null)
            ? (shares[0], Math.Min(1, shares.Take(shares.Count / 2).Sum(s => s!.Value)))
            : (null, null);
    }

    /// <summary>
    /// One list of <c>/api/bg/tier-lists</c>. Null when the answer has no such list, its source is refused, or (heroes)
    /// there are no <paramref name="heroes"/> to take card ids from.
    /// </summary>
    internal static BgSiteTierList? ParseTierList(JsonElement root, BgSiteList kind, HeroDetails? heroes)
    {
        var name = kind.ToString().ToLowerInvariant();
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("lists", out var lists) && lists.ValueKind == JsonValueKind.Object
               && lists.TryGetProperty(name, out var list)
            ? ParseList(list, kind, heroes, name)
            : null;
    }

    /// <summary>A tier list object: <c>lists.&lt;name&gt;</c> of the whole answer, or the answer of <c>?list=&lt;name&gt;</c>.</summary>
    internal static BgSiteTierList? ParseList(JsonElement list, BgSiteList kind, HeroDetails? heroes, string name)
    {
        if (list.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var source = Str(list, "source");
        if (!Usable(source, Str(list, "upstreamSource")))
        {
            Diag.Info($"Battlegrounds site {name}: source \"{Clean(source, 80)}\" is not used");
            return null;
        }

        if (kind == BgSiteList.Heroes && heroes is null)
        {
            Diag.Info("Battlegrounds site heroes: no card ids (the heroes answer is missing), list not used");
            return null;
        }

        if (!list.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Object)
        {
            Diag.Info($"Battlegrounds site {name}: no tiers");
            return null;
        }

        foreach (var bucket in tiers.EnumerateObject())
        {
            if (Array.IndexOf(TierLetters, bucket.Name) < 0)
            {
                Diag.Info($"Battlegrounds site {name}: unknown tier \"{Clean(bucket.Name, 16)}\" dropped");
            }
        }

        var entries = new List<BgSiteTierEntry>();
        var dropped = 0;
        foreach (var letter in TierLetters)
        {
            if (!tiers.TryGetProperty(letter, out var bucket) || bucket.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var item in bucket.EnumerateArray())
            {
                var entry = item.ValueKind != JsonValueKind.Object ? null : kind switch
                {
                    BgSiteList.Heroes => Hero(item, letter, heroes!),
                    BgSiteList.Trinkets => Trinket(item, letter),
                    _ => Card(item, letter),
                };
                if (entry is null)
                {
                    dropped++;
                }
                else
                {
                    entries.Add(entry);
                }
            }
        }

        if (dropped > 0)
        {
            Diag.Info($"Battlegrounds site {name}: {dropped} entries without a card id dropped");
        }

        var available = list.TryGetProperty("availableTiers", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).Where(t => Array.IndexOf(TierLetters, t) >= 0).Distinct(StringComparer.Ordinal).ToList()
            : [];
        if (available.Count == 0)
        {
            available = [.. TierLetters.Where(t => entries.Exists(e => e.Tier == t))];
        }

        return new BgSiteTierList(
            kind,
            entries,
            available,
            Time(list, "fetchedAt") ?? default,
            source!,
            kind == BgSiteList.Heroes ? heroes!.Mmr : Key(list, "mmr"),
            kind == BgSiteList.Heroes ? heroes!.TimeRange : Key(list, "timeRange"),
            Clean(Str(list, "gamesNote"), MaxTipLength));
    }

    private static BgSiteTierEntry? Hero(JsonElement item, string tier, HeroDetails heroes) =>
        Int(item, "dbfId") is { } dbf && heroes.ByDbf.TryGetValue(dbf, out var hero)
            ? new BgSiteTierEntry(hero.CardId, dbf, tier, Clean(Str(item, "hero"), MaxNameLength) ?? string.Empty, string.Empty, 0,
                Placement(item, "avgPlacement"), Share(item, "pickRate"), hero.Top4, hero.First, Games: null)
            : null;

    private static BgSiteTierEntry? Card(JsonElement item, string tier)
    {
        if (CardId(item, "id") is not { } id)
        {
            return null;
        }

        var english = Clean(Str(item, "name"), MaxNameLength) ?? string.Empty;
        return new BgSiteTierEntry(id, Int(item, "dbfId") ?? 0, tier, Clean(Str(item, "ruName"), MaxNameLength) ?? english, english,
            Int(item, "tavernTier") is { } t and >= 1 and <= 7 ? t : 0,
            AveragePlacement: null, Share(item, "popularity"), Top4Rate: null, FirstRate: null, Count(item, "games"));
    }

    private static BgSiteTierEntry? Trinket(JsonElement item, string tier)
    {
        if (CardId(item, "id") is not { } id)
        {
            return null;
        }

        var english = Clean(Str(item, "name"), MaxNameLength) ?? string.Empty;
        return new BgSiteTierEntry(id, Int(item, "dbfId") ?? 0, tier, Clean(Str(item, "ruName"), MaxNameLength) ?? english, english, 0,
            Placement(item, "avgPlacement"), Share(item, "pickRate"), Top4Rate: null, FirstRate: null, Count(item, "games"),
            Str(item, "size") is "LARGE" or "SMALL" ? Str(item, "size") : null, Count(item, "cost"));
    }

    /// <summary>The minion numbers by card id and the run they come from; empty when the source is refused.</summary>
    internal static (IReadOnlyDictionary<string, BgSiteCardStat> Stats, BgSiteRun? Run) ParseMinionStats(JsonElement root)
    {
        var stats = new Dictionary<string, BgSiteCardStat>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return (stats, null);
        }

        var latest = root.TryGetProperty("latest_run", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
        var source = Str(latest, "source");
        if (!Allowed(source))
        {
            Diag.Info($"Battlegrounds site minion stats: source \"{Clean(source, 80)}\" is not used");
            return (stats, null);
        }

        var run = new BgSiteRun(Key(latest, "mmr_percentile"), Key(latest, "time_range"), Time(latest, "completed_at"));
        if (!root.TryGetProperty("minions", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            Diag.Info("Battlegrounds site minion stats: no rows");
            return (stats, run);
        }

        var dropped = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || CardId(row, "card_id") is not { } id)
            {
                dropped++;
                continue;
            }

            stats[id] = new BgSiteCardStat(id, Int(row, "dbf_id") ?? 0, Int(row, "tavern_tier") is { } t and >= 1 and <= 7 ? t : 0, IsSpell: false,
                Number(row, "impact"), Share(row, "combat_winrate"), Share(row, "popularity"),
                Count(row, "games_with_minion"), Count(row, "games_without_minion"),
                Placement(row, "avg_placement_with"), Placement(row, "avg_placement_without"));
        }

        if (dropped > 0)
        {
            Diag.Info($"Battlegrounds site minion stats: {dropped} rows without a card id dropped");
        }

        return (stats, run);
    }

    /// <summary>The comps the app may show, in the service's order, and when the server fetched the list.</summary>
    internal static (IReadOnlyList<BgSiteComp> Comps, DateTime? FetchedAt) ParseComps(JsonElement root)
    {
        var comps = new List<BgSiteComp>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("comps", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return (comps, null);
        }

        var listSource = Str(root, "source");
        var refused = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!Usable(Str(row, "source") ?? listSource, url: Str(row, "sourceUrl")))
            {
                refused++;
                continue;
            }

            if (Clean(Str(row, "key"), 64) is not { } id || Clean(Str(row, "title"), MaxNameLength) is not { } title)
            {
                Diag.Info("Battlegrounds site comps: a comp without a key or a title dropped");
                continue;
            }

            comps.Add(new BgSiteComp(id, title, string.Empty,
                Str(row, "tier") is { } tier && Array.IndexOf(TierLetters, tier) >= 0 ? tier : null,
                [], CardIds(row, "coreCards"), Placement(row, "avgPlacement"), Games: null,
                Difficulty(Str(row, "difficulty")), Clean(Str(row, "description"), MaxTipLength)));
        }

        if (refused > 0)
        {
            Diag.Info($"Battlegrounds site comps: {refused} comps of a source that is not used dropped");
        }

        return (comps, Time(root, "fetchedAt"));
    }

    private static string? Difficulty(string? word) => word?.Trim().ToLowerInvariant().Replace('ё', 'е') switch
    {
        "легкая" or "easy" => "easy",
        "средняя" or "medium" => "medium",
        "сложная" or "hard" => "hard",
        _ => null,
    };

    private static List<string> CardIds(JsonElement row, string name)
    {
        var ids = new List<string>();
        if (row.TryGetProperty(name, out var cards) && cards.ValueKind == JsonValueKind.Array)
        {
            foreach (var card in cards.EnumerateArray())
            {
                if (card.ValueKind == JsonValueKind.Object && CardId(card, "id") is { } id && !ids.Contains(id, StringComparer.Ordinal))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private static string? Str(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? CardId(JsonElement item, string name) => Str(item, name) is { } id && CardIdPattern.IsMatch(id) ? id : null;

    private static string? Key(JsonElement item, string name) => Str(item, name) is { } key && KeyPattern.IsMatch(key) ? key : null;

    /// <summary>Text fit to show: no control characters, trimmed, cut to <paramref name="max"/>; null when nothing is left.</summary>
    private static string? Clean(string? text, int max)
    {
        if (text is null)
        {
            return null;
        }

        var clean = string.Concat(text.Where(c => !char.IsControl(c))).Trim();
        return clean.Length == 0 ? null : clean.Length <= max ? clean : clean[..max];
    }

    /// <summary>A number sent as a number or as text ("4.24", "78.10%").</summary>
    private static double? Number(JsonElement value)
    {
        double number;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number when value.TryGetDouble(out number):
                break;
            case JsonValueKind.String when double.TryParse(value.GetString()!.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out number):
                break;
            default:
                return null;
        }

        return double.IsFinite(number) ? number : null;
    }

    private static double? Number(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? Number(value) : null;

    /// <summary>A percent (78.1 or "78.10%") as a share; null outside 0..100.</summary>
    private static double? Share(JsonElement value) => Number(value) is { } percent and >= 0 and <= 100 ? percent / 100 : null;

    private static double? Share(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? Share(value) : null;

    /// <summary>An average place; null outside 1..8.</summary>
    private static double? Placement(JsonElement item, string name) => Number(item, name) is { } place and >= 1 and <= 8 ? place : null;

    private static int? Int(JsonElement item, string name) =>
        Number(item, name) is { } number and >= int.MinValue and <= int.MaxValue && Math.Abs(number - Math.Round(number)) < 1e-9 ? (int)Math.Round(number) : null;

    /// <summary>A count; null when negative.</summary>
    private static int? Count(JsonElement item, string name) => Int(item, name) is { } count and >= 0 ? count : null;

    private static DateTime? Time(JsonElement item, string name) =>
        Str(item, name) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.UtcDateTime : null;
}
