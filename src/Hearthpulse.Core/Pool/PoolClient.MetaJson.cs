using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

/// <summary>Reading the pool's compact meta arrays (heroes, cards, trinkets, comps and guides per bracket).</summary>
public static partial class PoolClient
{
    /// <summary>
    /// Reads the compact arrays written by the importer: heroes per bracket, cards per bracket with their turn
    /// results, trinkets per MMR, comps with their MMR and hero placement, and the comp guides.
    /// </summary>
    internal static BattlegroundsMeta? ParseMeta(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var heroes = new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>();
        if (root.TryGetProperty("heroes", out var heroBrackets) && heroBrackets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bracket in heroBrackets.EnumerateObject())
            {
                if (Bracket(bracket.Name) is { } value && bracket.Value.ValueKind == JsonValueKind.Array
                    && ParseMetaHeroes(bracket.Value) is { Count: > 0 } parsed)
                {
                    heroes[value] = parsed;
                }
            }
        }

        if (heroes.Count == 0)
        {
            return null;
        }

        var cards = new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>();
        if (root.TryGetProperty("cards", out var cardBrackets) && cardBrackets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bracket in cardBrackets.EnumerateObject())
            {
                if (Bracket(bracket.Name) is { } value && bracket.Value.ValueKind == JsonValueKind.Array
                    && ParseMetaCards(bracket.Value) is { Count: > 0 } parsed)
                {
                    cards[value] = parsed;
                }
            }
        }

        var trinkets = new Dictionary<string, MetaTrinket>(StringComparer.Ordinal);
        foreach (var e in JsonArray(root, "trinkets"))
        {
            if (TextAt(e, 0) is not { Length: > 0 } id)
            {
                continue;
            }

            var atMmr = new Dictionary<int, (double, int)>();
            if (e.GetArrayLength() > 4 && e[4].ValueKind == JsonValueKind.Array)
            {
                foreach (var m in e[4].EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 3 && m[0].TryGetInt32(out var mmr))
                    {
                        atMmr[mmr] = (NumberAt(m, 2), IntAt(m, 1));
                    }
                }
            }

            trinkets[id] = new MetaTrinket(id, IntAt(e, 1), NumberAt(e, 3), NumberAt(e, 2), atMmr);
        }

        var guides = GuideIndex(root);
        var comps = ParseMetaComps(root, guides);
        var percentiles = new List<(int, int)>();
        if (root.TryGetProperty("percentiles", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() >= 2
                    && p[0].TryGetInt32(out var percentile) && p[1].TryGetInt32(out var mmr))
                {
                    percentiles.Add((percentile, mmr));
                }
            }
        }

        var updated = Text(root, "updated") is { Length: > 0 } date
            && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsedDate)
                ? parsedDate
                : DateTime.UtcNow;

        return new BattlegroundsMeta(heroes, percentiles, comps, trinkets, cards, updated) { Source = BattlegroundsMeta.PoolAttribution };
    }

    private static int? Bracket(string name) =>
        int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bracket) ? bracket : null;

    private static Dictionary<string, MetaHero> ParseMetaHeroes(JsonElement list)
    {
        var result = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 7 || TextAt(e, 0) is not { Length: > 0 } id)
            {
                continue;
            }

            var impact = new Dictionary<int, double>();
            if (e.GetArrayLength() > 7 && e[7].ValueKind == JsonValueKind.Array)
            {
                foreach (var tribe in e[7].EnumerateArray())
                {
                    if (tribe.ValueKind == JsonValueKind.Array && tribe.GetArrayLength() >= 2 && tribe[0].TryGetInt32(out var tribeId))
                    {
                        impact[tribeId] = NumberAt(tribe, 1);
                    }
                }
            }

            var offered = IntAt(e, 2);
            result[id] = new MetaHero(
                id,
                IntAt(e, 1),
                NumberAt(e, 4),
                offered > 0 ? (double)IntAt(e, 3) / offered : 0,
                impact,
                NumberAt(e, 5),
                NumberAt(e, 6));
        }

        return result;
    }

    private static Dictionary<string, MetaCard> ParseMetaCards(JsonElement list)
    {
        var result = new Dictionary<string, MetaCard>(StringComparer.Ordinal);
        foreach (var e in list.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3 || TextAt(e, 0) is not { Length: > 0 } id)
            {
                continue;
            }

            var turns = new Dictionary<int, (int, double)>();
            if (e.GetArrayLength() > 3 && e[3].ValueKind == JsonValueKind.Array)
            {
                foreach (var t in e[3].EnumerateArray())
                {
                    if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 3 && t[0].TryGetInt32(out var turn))
                    {
                        turns[turn] = (IntAt(t, 1), NumberAt(t, 2));
                    }
                }
            }

            result[id] = new MetaCard(id, IntAt(e, 1), NumberAt(e, 2), turns);
        }

        return result;
    }

    private static List<MetaComp> ParseMetaComps(JsonElement root, Dictionary<string, JsonElement> guides)
    {
        var comps = new List<MetaComp>();
        foreach (var e in JsonArray(root, "comps"))
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() < 3 || TextAt(e, 0) is not { Length: > 0 } id)
            {
                continue;
            }

            var atMmr = new Dictionary<int, double>();
            if (e.GetArrayLength() > 3 && e[3].ValueKind == JsonValueKind.Array)
            {
                foreach (var m in e[3].EnumerateArray())
                {
                    if (m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 3 && m[0].TryGetInt32(out var mmr))
                    {
                        atMmr[mmr] = NumberAt(m, 2);
                    }
                }
            }

            var heroPlacement = new Dictionary<string, (int, double)>(StringComparer.Ordinal);
            if (e.GetArrayLength() > 4 && e[4].ValueKind == JsonValueKind.Array)
            {
                foreach (var h in e[4].EnumerateArray())
                {
                    if (h.ValueKind == JsonValueKind.Array && h.GetArrayLength() >= 3 && TextAt(h, 0) is { Length: > 0 } heroId)
                    {
                        heroPlacement[heroId] = (IntAt(h, 1), NumberAt(h, 2));
                    }
                }
            }

            var name = BattlegroundsMeta.Prettify(id);
            var power = string.Empty;
            var difficulty = string.Empty;
            string? when = null;
            string? tip = null;
            var cards = new List<MetaCompCard>();
            var tribes = new List<string>();

            if (guides.TryGetValue(id, out var guide))
            {
                name = Text(guide, "name") is { Length: > 0 } guideName ? guideName : name;
                power = Text(guide, "power") ?? string.Empty;
                difficulty = Text(guide, "difficulty") ?? string.Empty;
                tip = Text(guide, "tip") is { Length: > 0 } guideTip ? guideTip : null;
                when = Text(guide, "when") is { Length: > 0 } guideWhen ? guideWhen : null;

                if (guide.TryGetProperty("cards", out var guideCards) && guideCards.ValueKind == JsonValueKind.Array)
                {
                    foreach (var card in guideCards.EnumerateArray())
                    {
                        if (card.ValueKind == JsonValueKind.Array && card.GetArrayLength() >= 2 && TextAt(card, 0) is { Length: > 0 } cardId)
                        {
                            cards.Add(new MetaCompCard(cardId, TextAt(card, 1) is { Length: > 0 } status ? status : "ADDON"));
                        }
                    }
                }

                if (guide.TryGetProperty("tribes", out var guideTribes) && guideTribes.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tribe in guideTribes.EnumerateArray())
                    {
                        if (tribe.ValueKind == JsonValueKind.Number && tribe.TryGetInt32(out var raceId)
                            && Cards.Races.ById.TryGetValue(raceId, out var race) && race is { Length: > 0 })
                        {
                            tribes.Add(race);
                        }
                        else if (tribe.ValueKind == JsonValueKind.String && tribe.GetString() is { Length: > 0 } raceName)
                        {
                            tribes.Add(raceName);
                        }
                    }
                }
            }

            // The imported guides can arrive with an empty tribe list; the id prefix still says what the comp is.
            if (tribes.Count == 0 && BattlegroundsMeta.TribeForComp(id) is { } prefixTribe)
            {
                tribes.Add(prefixTribe);
            }

            comps.Add(new MetaComp(id, name, power, difficulty, tribes, cards, when, tip, NumberAt(e, 2), atMmr, heroPlacement));
        }

        return comps;
    }

    private static Dictionary<string, JsonElement> GuideIndex(JsonElement root)
    {
        var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var guide in JsonArray(root, "guides"))
        {
            if (guide.ValueKind == JsonValueKind.Object && Text(guide, "id") is { Length: > 0 } id)
            {
                index[id] = guide;
            }
        }

        return index;
    }

    private static IEnumerable<JsonElement> JsonArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    private static string TextAt(JsonElement array, int index) =>
        index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.String ? array[index].GetString() ?? string.Empty : string.Empty;

    private static int IntAt(JsonElement array, int index) =>
        index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number && array[index].TryGetInt32(out var value) ? value : 0;

    private static double NumberAt(JsonElement array, int index) =>
        index < array.GetArrayLength() && array[index].ValueKind == JsonValueKind.Number ? array[index].GetDouble() : 0;
}
