using System.Globalization;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Api;

public sealed partial class HearthpulseData
{
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
