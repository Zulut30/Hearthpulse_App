using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Api;

/// <summary>
/// Community stats from the Hearthpulse API: Battlegrounds heroes and minions per MMR slice, and every
/// ranked Constructed number the app shows (HSGuru meta and decks, HSReplay archetypes, mulligan and matchups).
/// Answers are cached on disk, and a stale cache is used when the server is down. Never throws.
/// </summary>
/// <param name="cacheDirectory">Where answers are cached; the app's cache folder by default (tests pass a scratch one).</param>
public sealed partial class HearthpulseData(HearthpulseApi api, string? cacheDirectory = null)
{
    /// <summary>The name every number from the Hearthpulse API goes by in the UI, in both languages.</summary>
    public const string Attribution = "Hearthpulse data";

    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>How old a real answer may be and still stand in for a 404 (a slice the server dropped for a while).</summary>
    private static readonly TimeSpan MaxStaleForMissing = TimeSpan.FromDays(2);

    /// <summary>Values from answers that become part of a URL or cache file name must look like the API's own keys.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9_.]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SafeKey { get; }

    private readonly string _cacheDirectory = cacheDirectory ?? Path.Combine(AppPaths.Cache, "api");

    /// <summary>MMR slices the API serves for heroes (<c>/api/bg/heroes</c>); it has no all-ranks slice.</summary>
    private static readonly HashSet<string> HeroMmrs = new(StringComparer.Ordinal) { "TOP_50_PERCENT", "TOP_20_PERCENT", "TOP_5_PERCENT", "TOP_1_PERCENT" };

    /// <summary>MMR slices the API serves for minions (<c>/v1/battlegrounds/minions/stats/slices</c>).</summary>
    private static readonly HashSet<string> MinionMmrs = new(StringComparer.Ordinal) { "ALL", "TOP_50_PERCENT", "TOP_20_PERCENT" };

    /// <summary>
    /// Solo hero results of one MMR slice (current Battlegrounds patch), by hero card id, with the time the server fetched
    /// them. Sample sizes are not published: DataPoints is 0. Empty for a slice the API does not have.
    /// </summary>
    public async Task<BgHeroSlice> LoadHeroesAsync(string mmr = "TOP_50_PERCENT", CancellationToken cancellationToken = default)
    {
        var empty = new BgHeroSlice(new Dictionary<string, MetaHero>(), default);
        if (!HeroMmrs.Contains(mmr))
        {
            return empty;
        }

        using var doc = await FetchAsync($"api/bg/heroes?mode=solo&mmr={mmr}", $"bg-heroes-{mmr.ToLowerInvariant()}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? empty : Read(() => ParseHeroSlice(Data(doc)), "heroes", empty);
    }

    /// <summary>
    /// Minion results of one MMR slice ("ALL" = all ranks, current Battlegrounds patch), by card id: average final
    /// placement of players who had the minion and how many games that is. No per-turn results.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, MetaCard>> LoadMinionsAsync(string mmr = "ALL", CancellationToken cancellationToken = default)
    {
        if (!MinionMmrs.Contains(mmr))
        {
            return new Dictionary<string, MetaCard>();
        }

        using var doc = await FetchAsync($"v1/battlegrounds/minions/stats?mmr={mmr}&period=CURRENT_BATTLEGROUNDS_PATCH&limit=500", $"bg-minions-{mmr.ToLowerInvariant()}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? new Dictionary<string, MetaCard>() : Read(() => ParseMinions(Data(doc)), "minions", new Dictionary<string, MetaCard>());
    }

    /// <summary>Ranked archetypes for a format ("standard"/"wild") and an app rank key ("legend", "all", ...).</summary>
    public async Task<IReadOnlyList<MetaArchetype>> LoadArchetypesAsync(string format, string rank, CancellationToken cancellationToken = default)
    {
        var gameType = format == "wild" ? "RANKED_WILD" : "RANKED_STANDARD";
        var rankRange = RankRange(rank);
        using var doc = await FetchAsync($"api/db/archetypes?game_type={gameType}&rank_range={rankRange}&limit=200", $"archetypes-{gameType}-{rankRange}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseArchetypes(Data(doc)), "archetypes", new List<MetaArchetype>());
    }

    /// <summary>An archetype's HSReplay decklists (Standard Legend), for matching the player's deck to an archetype.</summary>
    public async Task<IReadOnlyList<GuideDeck>> LoadArchetypeDecksAsync(int archetypeId, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"api/db/archetypes/{archetypeId}/decks?limit=200", $"archetype-{archetypeId}-decks.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseArchetypeDecks(Data(doc)), "archetype decks", new List<GuideDeck>());
    }

    /// <summary>Every card's mulligan numbers for an archetype (Standard Legend), not only the rows HSReplay displays.</summary>
    public async Task<IReadOnlyList<GuideCard>> LoadArchetypeMulliganAsync(int archetypeId, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"api/db/archetypes/{archetypeId}/mulligan?display_only=false&limit=250", $"archetype-{archetypeId}-mulligan.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseMulligan(Data(doc)), "archetype mulligan", new List<GuideCard>());
    }

    /// <summary>
    /// A generator's Hearthstone Wiki pools (<c>wiki.generated_card_pools</c>); null when the API has no answer (offline,
    /// unknown card, an id that is not a card id).
    /// </summary>
    public async Task<CardPool.CardWiki?> LoadCardWikiAsync(string cardId, CancellationToken cancellationToken = default)
    {
        if (!CardIdKey.IsMatch(cardId))
        {
            return null;
        }

        using var doc = await FetchAsync($"api/v1/constructed-cards/{cardId}/wiki", $"card-wiki-{cardId}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read(() => CardPool.WikiPools.Parse(Data(doc)), "card wiki", (CardPool.CardWiki?)null);
    }

    /// <summary>Card ids that may go into a URL and a cache file name.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_]{1,48}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex CardIdKey { get; }

    public async Task<IReadOnlyList<GuideMatchup>> LoadArchetypeMatchupsAsync(int archetypeId, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"api/db/archetypes/{archetypeId}/matchups?limit=500", $"archetype-{archetypeId}-matchups.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseMatchups(Data(doc)), "archetype matchups", new List<GuideMatchup>());
    }

    /// <summary>
    /// HSGuru's meta for one format, rank and period (keys as the API takes them, e.g. "standard", "legend",
    /// "past_week"), with the periods the API offers. Null when neither the server nor the cache has it.
    /// </summary>
    public async Task<HsguruMetaSlice?> LoadHsguruMetaAsync(string format, string rank, string period, CancellationToken cancellationToken = default)
    {
        // The period can come from an earlier answer ("current_period"): only key-like values reach the URL and the file name.
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(rank) || !SafeKey.IsMatch(period))
        {
            return null;
        }

        using var doc = await FetchAsync($"v1/hsguru/meta?format={format}&rank={rank}&period={Uri.EscapeDataString(period)}&min_games=100", $"hsguru-meta-{format}-{rank}-{period}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read<HsguruMetaSlice?>(() => ParseHsguruMeta(doc.RootElement), "HSGuru meta", null);
    }

    /// <summary>Every HSGuru archetype of a format (current expansion, all ranks) with its most played decklists.</summary>
    public async Task<IReadOnlyList<HsguruArchetype>> LoadHsguruArchetypesAsync(string format, CancellationToken cancellationToken = default)
    {
        using var doc = await FetchAsync($"v1/hsguru/archetypes?format={format}&min_games=50&limit=500", $"hsguru-archetypes-{format}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseHsguruArchetypes(Data(doc)), "HSGuru archetypes", new List<HsguruArchetype>());
    }

    /// <summary>
    /// HSGuru's per-card mulligan numbers and class matchups for one archetype (Legend, past week): the mulligan source
    /// for Wild and for archetypes HSReplay does not have. Null when the API has no analysis for it.
    /// </summary>
    public async Task<HsguruAnalysis?> LoadHsguruAnalysisAsync(string archetype, string format, CancellationToken cancellationToken = default)
    {
        if (!SafeKey.IsMatch(format) || archetype.Length is 0 or > 120)
        {
            return null;
        }

        var file = $"hsguru-analysis-{format}-{ApiConstructedMeta.StableId(archetype):x8}.json";
        using var doc = await FetchAsync($"v1/hsguru/archetypes/analysis?archetype={Uri.EscapeDataString(archetype)}&format={format}", file, cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read<HsguruAnalysis?>(() => ParseHsguruAnalysis(Data(doc)), "HSGuru analysis", null);
    }

    /// <summary>
    /// One HSGuru mulligan slice of an archetype (current expansion): a rank ("legend", "all", ...), the coin
    /// ("any_player", "going_first", "on_coin") and the opponent's class ("any" or "WARLOCK"). The server keeps the coin
    /// and the class apart, so a slice sets at most one of them. Null when the server has not collected the slice (a 404,
    /// remembered like any answer).
    /// </summary>
    public async Task<MulliganSlice?> LoadHsguruMulliganAsync(string archetype, string format, string rank, string coin, string opponentClass, CancellationToken cancellationToken = default)
    {
        var opponent = opponentClass.ToLowerInvariant();
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(rank) || !SafeKey.IsMatch(coin) || !SafeKey.IsMatch(opponent) || archetype.Length is 0 or > 120)
        {
            return null;
        }

        var server = opponent == MulliganGuide.AnyOpponent ? opponent : opponent.ToUpperInvariant();
        var file = $"hsguru-mulligan-{format}-{ApiConstructedMeta.StableId(archetype):x8}-{rank}-{coin}-{opponent}.json";
        using var doc = await FetchAsync($"v1/hsguru/mulligan?archetype={Uri.EscapeDataString(archetype)}&format={format}&rank={rank}&coin={coin}&opponent_class={server}",
            file, cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read(() => ParseHsguruMulligan(Data(doc)), "HSGuru mulligan", (MulliganSlice?)null);
    }

    /// <summary>
    /// HSGuru's results of an archetype against each class (current expansion, all players) at one rank ("legend",
    /// "all", ...): the mulligan guide's class shift when HSReplay has no matchups for the archetype. Empty when the
    /// server has not collected it (a 404, remembered like any answer).
    /// </summary>
    public async Task<IReadOnlyList<GuideMatchup>> LoadHsguruClassMatchupsAsync(string archetype, string format, string rank, CancellationToken cancellationToken = default)
    {
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(rank) || archetype.Length is 0 or > 120)
        {
            return [];
        }

        var file = $"hsguru-class-matchups-{format}-{ApiConstructedMeta.StableId(archetype):x8}-{rank}.json";
        using var doc = await FetchAsync($"v1/hsguru/class-matchups?archetype={Uri.EscapeDataString(archetype)}&format={format}&rank={rank}", file, cancellationToken).ConfigureAwait(false);
        return doc is null ? [] : Read(() => ParseHsguruClassMatchups(Data(doc)), "HSGuru class matchups", new List<GuideMatchup>());
    }

    /// <summary>The ranks <c>v1/hsguru/matchups</c> takes; the meta's narrower ones (top 500, top 100) read its top-Legend slice.</summary>
    internal static string MatchupRank(string rank) => rank switch
    {
        "all" or "top_legend" or "top_5k" or "legend" or "diamond_to_legend" or "diamond_4to1" or "bronze_plat" => rank,
        "top_500" or "top_100" => "top_legend",
        _ => "all",
    };

    /// <summary>
    /// HSGuru's matchup matrix of the <paramref name="limit"/> most played archetypes of a format, rank and period (an API
    /// period such as "past_week" or "black_empire"). Null when neither the server nor the cache has it.
    /// </summary>
    public async Task<(IReadOnlyList<MatchupArchetype> Archetypes, IReadOnlyList<MatchupCell> Cells, int Games)?> LoadHsguruMatchupsAsync(
        string format, string rank, string period, int limit = 12, CancellationToken cancellationToken = default)
    {
        var server = MatchupRank(rank);
        if (!SafeKey.IsMatch(format) || !SafeKey.IsMatch(period) || limit is < 2 or > 40)
        {
            return null;
        }

        using var doc = await FetchAsync($"v1/hsguru/matchups?format={format}&rank={server}&period={Uri.EscapeDataString(period)}&limit={limit}",
            $"hsguru-matchups-{format}-{server}-{period}-{limit}.json", cancellationToken).ConfigureAwait(false);
        return doc is null ? null : Read(() => ParseHsguruMatchups(Data(doc)), "HSGuru matchups", ((IReadOnlyList<MatchupArchetype>, IReadOnlyList<MatchupCell>, int)?)null);
    }

    /// <summary>
    /// <c>{"total_games", "archetypes": [{"archetype", "class", "games", "winrate", "popularity_pct"}], "matchups":
    /// [{"archetype", "opponent", "winrate", "games"}]}</c>; rates as fractions, names as the API writes them.
    /// </summary>
    internal static (IReadOnlyList<MatchupArchetype>, IReadOnlyList<MatchupCell>, int)? ParseHsguruMatchups(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var archetypes = new List<MatchupArchetype>();
        foreach (var a in Items(data.TryGetProperty("archetypes", out var list) ? list : default))
        {
            if (Str(a, "archetype") is { Length: > 0 } name && Num(a, "games") is { } games && games > 0)
            {
                archetypes.Add(new MatchupArchetype(name, name, (Str(a, "class") ?? string.Empty).ToUpperInvariant(), (int)games,
                    (Num(a, "winrate") ?? 50) / 100, (Num(a, "popularity_pct") ?? 0) / 100));
            }
        }

        var cells = new List<MatchupCell>();
        foreach (var c in Items(data.TryGetProperty("matchups", out var rows) ? rows : default))
        {
            if (Str(c, "archetype") is { Length: > 0 } a && Str(c, "opponent") is { Length: > 0 } o && Num(c, "winrate") is { } winRate)
            {
                cells.Add(new MatchupCell(a, o, winRate / 100, (int)(Num(c, "games") ?? 0)));
            }
        }

        var total = (int)(Num(data, "total_games") ?? archetypes.Sum(a => a.Games));
        return (archetypes.OrderByDescending(a => a.Games).ToList(), cells, total);
    }

    /// <summary>HSReplay publishes Legend and a few brackets; anything wider than Legend maps to its all-ranks view.</summary>
    internal static string RankRange(string rank) => rank switch
    {
        "legend" or "top-2000-legend" or "top-500-legend" or "top-100-legend" or "top_5k" or "top_legend" or "top_500" or "top_100" => "LEGEND",
        "diamond" or "diamond_4to1" or "diamond_to_legend" => "DIAMOND_THROUGH_LEGEND",
        _ => "ALL",
    };

    /// <summary>
    /// The bare <c>/api/bg/heroes</c> answer: <c>{"fetched_at", "filters", "heroes": [...]}</c>. A plain hero list (the
    /// <c>/v1/battlegrounds/heroes</c> "data") reads too, without a fetch time.
    /// </summary>
    internal static BgHeroSlice ParseHeroSlice(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new BgHeroSlice(ParseHeroes(root), default);
        }

        var fetched = Str(root, "fetched_at") is { } at
                      && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : default;
        return new BgHeroSlice(root.TryGetProperty("heroes", out var list) ? ParseHeroes(list) : new Dictionary<string, MetaHero>(), fetched);
    }

    internal static Dictionary<string, MetaHero> ParseHeroes(JsonElement list)
    {
        var result = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
        foreach (var hero in Items(list))
        {
            if (Str(hero, "id") is not { Length: > 0 } id || Num(hero, "avg_placement") is not { } placement || placement <= 0)
            {
                continue;
            }

            double top4 = 0, first = 0;
            var shares = new List<double>();
            if (hero.TryGetProperty("placement_distribution", out var places) && places.ValueKind == JsonValueKind.Array)
            {
                foreach (var place in places.EnumerateArray())
                {
                    var share = Percent(place) / 100;
                    shares.Add(share);
                    top4 += shares.Count <= 4 ? share : 0;
                    first = shares.Count == 1 ? share : first;
                }
            }

            // Tribe impact and the per-turn combat win rate are not in the API's hero list: left empty, not zero.
            result[id] = new MetaHero(id, 0, placement, (Num(hero, "pick_rate_value") ?? 0) / 100, new Dictionary<int, double>(), Math.Min(1, top4), first,
                shares.Count == 8 ? shares : null);
        }

        return result;
    }

    internal static Dictionary<string, MetaCard> ParseMinions(JsonElement list)
    {
        var result = new Dictionary<string, MetaCard>(StringComparer.Ordinal);
        foreach (var minion in Items(list))
        {
            if (Str(minion, "card_id") is not { Length: > 0 } id || !minion.TryGetProperty("normal", out var normal) || normal.ValueKind != JsonValueKind.Object
                || Num(normal, "avg_placement_with") is not { } placement || placement <= 0)
            {
                continue;
            }

            result[id] = new MetaCard(id, (int)(Num(normal, "games_with_minion") ?? 0), placement, new Dictionary<int, (int, double)>());
        }

        return result;
    }

    internal static List<MetaArchetype> ParseArchetypes(JsonElement root)
    {
        var result = new List<MetaArchetype>();
        if (!root.TryGetProperty("archetypes", out var list))
        {
            return result;
        }

        foreach (var a in Items(list))
        {
            if (Num(a, "archetype_id") is not { } id || Str(a, "name") is not { Length: > 0 } name)
            {
                continue;
            }

            var games = (int)(Num(a, "total_games") ?? 0);
            var winRate = (Num(a, "win_rate") ?? 0) / 100;
            result.Add(new MetaArchetype(
                (int)id,
                Str(a, "slug") ?? name,
                name,
                Str(a, "player_class") ?? string.Empty,
                games,
                (int)Math.Round(games * winRate),
                winRate,
                (Num(a, "pct_of_total") ?? 0) / 100,
                0, 0, 0, 0, [], []));
        }

        return result;
    }

    /// <summary>The cached <c>{"data": {"items": [...]}, "meta": {"fetched_at", "current_period", "current_patch_period"}}</c>; rates as fractions.</summary>
    internal static HsguruMetaSlice ParseHsguruMeta(JsonElement root)
    {
        var items = new List<HsguruMetaItem>();
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var list))
        {
            foreach (var item in Items(list))
            {
                if (Str(item, "archetype") is { Length: > 0 } name && Num(item, "winrate") is { } winRate && Num(item, "games") is { } games && games > 0)
                {
                    items.Add(new HsguruMetaItem(name, winRate / 100, (Num(item, "popularity") ?? 0) / 100, (int)games));
                }
            }
        }

        var meta = root.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        var fetched = meta.ValueKind == JsonValueKind.Object && Str(meta, "fetched_at") is { } at
                      && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : DateTime.MinValue;
        return new HsguruMetaSlice(
            items,
            fetched,
            meta.ValueKind == JsonValueKind.Object ? Str(meta, "current_period") : null,
            meta.ValueKind == JsonValueKind.Object ? Str(meta, "current_patch_period") : null);
    }

    /// <summary><c>[{"archetype", "games", "winrate", "decks": [{"class", "deck_code", "games", "win_rate", "updated_at"}]}]</c>.</summary>
    internal static List<HsguruArchetype> ParseHsguruArchetypes(JsonElement list)
    {
        var result = new List<HsguruArchetype>();
        foreach (var a in Items(list))
        {
            if (Str(a, "archetype") is not { Length: > 0 } name)
            {
                continue;
            }

            var decks = new List<HsguruDeck>();
            if (a.TryGetProperty("decks", out var deckList))
            {
                foreach (var d in Items(deckList))
                {
                    if (Str(d, "deck_code") is { Length: > 0 } code)
                    {
                        var updated = Str(d, "updated_at") is { } at && DateTime.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
                            ? time
                            : DateTime.MinValue;
                        decks.Add(new HsguruDeck(code, Str(d, "class") ?? string.Empty, (int)(Num(d, "games") ?? 0), (Num(d, "win_rate") ?? 0) / 100, updated));
                    }
                }
            }

            result.Add(new HsguruArchetype(name, (int)(Num(a, "games") ?? 0), (Num(a, "winrate") ?? 0) / 100, decks));
        }

        return result;
    }

    /// <summary><c>{"decks": [{"total_games", "raw_deck_list": "[[dbf, copies], ...]"}]}</c>; sideboards are left out.</summary>
    internal static List<GuideDeck> ParseArchetypeDecks(JsonElement root)
    {
        var result = new List<GuideDeck>();
        if (!root.TryGetProperty("decks", out var list))
        {
            return result;
        }

        foreach (var deck in Items(list))
        {
            if (Str(deck, "raw_deck_list") is not { Length: > 0 } raw)
            {
                continue;
            }

            // One bad list (or pair) is skipped, not the archetype's whole answer.
            JsonDocument cards;
            try
            {
                cards = JsonDocument.Parse(raw);
            }
            catch (JsonException)
            {
                continue;
            }

            var counts = new Dictionary<int, int>();
            using (cards)
            {
                foreach (var pair in Items(cards.RootElement, JsonValueKind.Array))
                {
                    if (pair.GetArrayLength() == 2 && pair[0].ValueKind == JsonValueKind.Number && pair[1].ValueKind == JsonValueKind.Number
                        && pair[0].TryGetInt32(out var dbf) && pair[1].TryGetInt32(out var copies))
                    {
                        counts[dbf] = counts.GetValueOrDefault(dbf) + copies;
                    }
                }
            }

            if (counts.Count > 0)
            {
                result.Add(new GuideDeck((int)(Num(deck, "total_games") ?? 0), counts));
            }
        }

        return result;
    }

    /// <summary>
    /// <c>{"card_stats": [{card_id, dbf_id, mulligan_count, kept_count, kept_impact, ...}], "class_matchups": [{class_key,
    /// winrate, games}]}</c>; impacts are win rate points against the archetype's own win rate, here fractions.
    /// </summary>
    internal static HsguruAnalysis ParseHsguruAnalysis(JsonElement root)
    {
        var cards = new List<HsguruCardStat>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("card_stats", out var list))
        {
            foreach (var c in Items(list))
            {
                if (Str(c, "card_id") is { Length: > 0 } id)
                {
                    cards.Add(new HsguruCardStat(id, (int)(Num(c, "dbf_id") ?? 0), (int)(Num(c, "mulligan_count") ?? 0), (int)(Num(c, "kept_count") ?? 0),
                        Num(c, "kept_impact") / 100, Num(c, "mulligan_impact") / 100));
                }
            }
        }

        var matchups = new List<GuideMatchup>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("class_matchups", out var classes))
        {
            foreach (var m in Items(classes))
            {
                if (Str(m, "class_key") is { Length: > 0 } key && Num(m, "winrate") is { } winRate && Num(m, "games") is { } games && games > 0)
                {
                    matchups.Add(new GuideMatchup(key.ToUpperInvariant(), (int)games, winRate / 100));
                }
            }
        }

        return new HsguruAnalysis(cards, matchups);
    }

    /// <summary><c>{"mulligan": [...]}</c>; percentages become fractions. Rows without a card id are skipped.</summary>
    internal static List<GuideCard> ParseMulligan(JsonElement root)
    {
        var result = new List<GuideCard>();
        if (!root.TryGetProperty("mulligan", out var list))
        {
            return result;
        }

        foreach (var row in Items(list))
        {
            if (Str(row, "card_id") is not { Length: > 0 } id)
            {
                continue;
            }

            result.Add(new GuideCard(
                id,
                (int)(Num(row, "dbf_id") ?? 0),
                (int)(Num(row, "cost") ?? 0),
                (int)(Num(row, "times_presented_in_initial_cards") ?? 0),
                (Num(row, "keep_percentage") ?? 0) / 100,
                (int)(Num(row, "times_in_opening_hand") ?? 0),
                Num(row, "opening_hand_winrate") / 100,
                Num(row, "avg_turn_played_on"))
            {
                Drawn = (int)(Num(row, "times_card_drawn") ?? 0),
                DrawnWinRate = Num(row, "winrate_when_drawn") / 100,
            });
        }

        return result;
    }

    /// <summary>
    /// <c>{"rank", "coin", "opponent_class", "games", "rows": [{card_id, dbf_id, mulligan_count, mulligan_impact,
    /// kept_count, kept_impact, drawn_count, drawn_impact}]}</c>; impacts become fractions. Null without rows.
    /// </summary>
    internal static MulliganSlice? ParseHsguruMulligan(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rows", out var list))
        {
            return null;
        }

        var cards = new List<SliceCard>();
        foreach (var row in Items(list))
        {
            if (Str(row, "card_id") is { Length: > 0 } id)
            {
                cards.Add(new SliceCard(id, (int)(Num(row, "dbf_id") ?? 0),
                    (int)(Num(row, "mulligan_count") ?? 0), Num(row, "mulligan_impact") / 100,
                    (int)(Num(row, "kept_count") ?? 0), Num(row, "kept_impact") / 100,
                    (int)(Num(row, "drawn_count") ?? 0), Num(row, "drawn_impact") / 100));
            }
        }

        return new MulliganSlice(Str(root, "rank") ?? "all", Str(root, "coin") ?? MulliganGuide.AnyCoin,
            Str(root, "opponent_class") ?? MulliganGuide.AnyOpponent, (int)(Num(root, "games") ?? 0), cards);
    }

    /// <summary>
    /// <c>{"games", "winrate", "rows": [{"opponent_class" (lower case, "deathknight"), "winrate", "games", "share_pct"}]}</c>;
    /// win rates become fractions, classes upper case like HSReplay's.
    /// </summary>
    internal static List<GuideMatchup> ParseHsguruClassMatchups(JsonElement root)
    {
        var result = new List<GuideMatchup>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rows", out var list))
        {
            return result;
        }

        foreach (var m in Items(list))
        {
            if (Str(m, "opponent_class") is { Length: > 0 } opponent && Num(m, "winrate") is { } winRate && Num(m, "games") is { } games && games > 0)
            {
                result.Add(new GuideMatchup(opponent.ToUpperInvariant(), (int)games, winRate / 100));
            }
        }

        return result;
    }

    /// <summary><c>{"matchups": [{"opponent_class", "total_games", "win_rate"}]}</c>.</summary>
    internal static List<GuideMatchup> ParseMatchups(JsonElement root)
    {
        var result = new List<GuideMatchup>();
        if (!root.TryGetProperty("matchups", out var list))
        {
            return result;
        }

        foreach (var m in Items(list))
        {
            if (Str(m, "opponent_class") is { Length: > 0 } opponent && Num(m, "win_rate") is { } winRate && Num(m, "total_games") is { } games && games > 0)
            {
                result.Add(new GuideMatchup(opponent, (int)games, winRate / 100));
            }
        }

        return result;
    }

    /// <param name="shrink">Keeps only what the app reads of a large answer, so the cache file stays small.</param>
    /// <param name="retry">Set when asking again after the cached file could not be read.</param>
    private async Task<JsonDocument?> FetchAsync(string path, string cacheName, CancellationToken cancellationToken, Func<JsonElement, object>? shrink = null,
        bool retry = false)
    {
        var file = Path.Combine(_cacheDirectory, cacheName);
        var info = new FileInfo(file);
        if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxAge)
        {
            var result = await api.GetAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.Ok)
            {
                // "meta" (periods, fetch time) is kept next to "data" when the endpoint sends it.
                var data = shrink is null ? (object)result.Data : shrink(result.Data);
                var text = result.Meta.ValueKind == JsonValueKind.Undefined
                    ? JsonSerializer.Serialize(new { data })
                    : JsonSerializer.Serialize(new { data, meta = result.Meta });
                // The whole answer is kept (not only "data"): some endpoints put their list next to it.
                await WriteCacheAsync(file, text, cancellationToken).ConfigureAwait(false);
                return JsonDocument.Parse(text);
            }

            Diag.Info($"Hearthpulse API {path}: {result.Error!.Code} {result.Error.Message}");
            if (result.Error.Code == "NOT_AVAILABLE")
            {
                // The server has nothing for this (404): remember that for as long as an answer, instead of asking every
                // load. A real answer from the last two days is used instead of being replaced by "nothing" (it keeps its
                // own time, so it is asked again on later loads and gives way after two days).
                if (!info.Exists || DateTime.UtcNow - info.LastWriteTimeUtc > MaxStaleForMissing || !HoldsData(file))
                {
                    await WriteCacheAsync(file, "{\"data\":null}", cancellationToken).ConfigureAwait(false);
                    return null;
                }
            }
        }

        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Null)
            {
                doc.Dispose();
                return null;
            }

            return doc;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Diag.Error($"Hearthpulse API cache unreadable ({cacheName})", ex);
            if (ex is JsonException && !retry && TryDelete(file))
            {
                // A young file that cannot be read (zeros after a power cut) kept the API from being asked for six hours.
                return await FetchAsync(path, cacheName, cancellationToken, shrink, retry: true).ConfigureAwait(false);
            }

            return null;
        }
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes a cache file through a temporary file, so a reader (another load, the mulligan guide) never sees half of it
    /// and a crash mid-write leaves the old answer.
    /// </summary>
    internal static async Task WriteCacheAsync(string file, string text, CancellationToken cancellationToken)
    {
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(temp, text, cancellationToken).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"Hearthpulse API cache not written ({Path.GetFileName(file)})", ex);
        }
        finally
        {
            // A cancelled or failed write leaves no half-written temporary file behind (after the move there is none).
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>True when the cache file is a real answer, not the remembered "nothing" of a 404.</summary>
    private static bool HoldsData(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("data", out var data)
                   && data.ValueKind != JsonValueKind.Null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>The list an endpoint returned, wrapped by <see cref="FetchAsync"/> as <c>{"data": answer}</c>.</summary>
    private static JsonElement Data(JsonDocument doc) => doc.RootElement.GetProperty("data");

    private static T Read<T>(Func<T> parse, string section, T empty)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Diag.Error($"Hearthpulse API data unreadable ({section})", ex);
            return empty;
        }
    }

    private static IEnumerable<JsonElement> Items(JsonElement list, JsonValueKind kind = JsonValueKind.Object) =>
        list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Where(e => e.ValueKind == kind) : [];

    // A field of another shape (null where an object was expected) reads as missing instead of throwing.
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>"22.00%" or 22.0.</summary>
    private static double Percent(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.String when double.TryParse(e.GetString()!.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
        _ => 0,
    };
}

/// <summary>One MMR slice of hero results and when the server fetched it (default when unknown).</summary>
public sealed record BgHeroSlice(IReadOnlyDictionary<string, MetaHero> Heroes, DateTime FetchedAt);
