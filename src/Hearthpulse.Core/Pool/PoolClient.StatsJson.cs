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

/// <summary>Reading the pool's totals (heroes, cards, trinkets, tribes and their impacts).</summary>
public static partial class PoolClient
{
    internal static BattlegroundsMeta? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("heroes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var heroes = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
        var allHeroes = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
        var impacts = TribeImpacts(root, list);
        foreach (var hero in list.EnumerateArray())
        {
            var id = Text(hero, "hero");
            var games = Int(hero, "games");
            if (string.IsNullOrEmpty(id) || games < 1)
            {
                continue;
            }

            var offered = Int(hero, "offered");
            var stats = new MetaHero(
                id,
                games,
                Number(hero, "avgPlacement"),
                offered > 0 ? (double)Int(hero, "picked") / offered : 0,
                impacts.GetValueOrDefault(id) ?? new Dictionary<int, double>(),
                Number(hero, "top"),
                Number(hero, "first"));
            allHeroes[id] = stats;
            if (games >= MinGames)
            {
                heroes[id] = stats;
            }
        }

        // Pool 2.1: MMR floors of the top 50/20/5/1 % of the pool's own rated games. They decide which bracket of the API's
        // (HSReplay) stats a player sees, so they count only with a large sample: MinRatedGames rated games in all and
        // MinBracketGames at or above each floor. Brackets of a floor that does not count are not read either.
        var percentiles = new List<(int, int)>();
        if (Int(root, "ratedGames") >= MinRatedGames)
        {
            foreach (var p in Array(root, "percentiles"))
            {
                if (Int(p, "top") is > 0 and var top && Int(p, "bracket") is > 0 and var floor && Int(p, "games") >= MinBracketGames)
                {
                    percentiles.Add((top, floor));
                }
            }
        }

        var trusted = percentiles.Select(p => p.Item1).ToHashSet();
        var heroesByBracket = new Dictionary<int, IReadOnlyDictionary<string, MetaHero>> { [100] = heroes };
        if (root.TryGetProperty("heroesByBracket", out var heroBrackets) && heroBrackets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bracket in heroBrackets.EnumerateObject())
            {
                if (Bracket(bracket.Name) is not { } top || !trusted.Contains(top) || bracket.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var rows = new Dictionary<string, MetaHero>(StringComparer.Ordinal);
                foreach (var hero in bracket.Value.EnumerateArray())
                {
                    if (Text(hero, "hero") is { Length: > 0 } id && Int(hero, "games") >= MinGames)
                    {
                        rows[id] = new MetaHero(id, Int(hero, "games"), Number(hero, "avgPlacement"), allHeroes.GetValueOrDefault(id)?.PickRate ?? 0,
                            impacts.GetValueOrDefault(id) ?? new Dictionary<int, double>(), Number(hero, "top"), Number(hero, "first"));
                    }
                }

                if (rows.Count > 0)
                {
                    heroesByBracket[top] = rows;
                }
            }
        }

        var cards = new Dictionary<string, MetaCard>(StringComparer.Ordinal);
        foreach (var card in Array(root, "cards"))
        {
            if (Text(card, "card") is { Length: > 0 } id)
            {
                cards[id] = new MetaCard(id, Int(card, "games"), Number(card, "avgPlacement"), new Dictionary<int, (int, double)>());
            }
        }

        var trinketsAt = new Dictionary<string, Dictionary<int, (double, int)>>(StringComparer.Ordinal);
        if (root.TryGetProperty("trinketsByBracket", out var trinketBrackets) && trinketBrackets.ValueKind == JsonValueKind.Object)
        {
            foreach (var bracket in trinketBrackets.EnumerateObject())
            {
                if (Bracket(bracket.Name) is not { } top || !trusted.Contains(top) || bracket.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var trinket in bracket.Value.EnumerateArray())
                {
                    if (Text(trinket, "card") is { Length: > 0 } id)
                    {
                        (trinketsAt.TryGetValue(id, out var at) ? at : trinketsAt[id] = new())[top] = (Number(trinket, "avgPlacement"), Int(trinket, "picked"));
                    }
                }
            }
        }

        var trinkets = new Dictionary<string, MetaTrinket>(StringComparer.Ordinal);
        foreach (var trinket in Array(root, "trinkets"))
        {
            if (Text(trinket, "card") is { Length: > 0 } id)
            {
                var offered = Int(trinket, "offered");
                trinkets[id] = new MetaTrinket(
                    id,
                    Int(trinket, "picked"),
                    Number(trinket, "avgPlacement"),
                    offered > 0 ? (double)Int(trinket, "picked") / offered : 0,
                    trinketsAt.GetValueOrDefault(id) ?? new Dictionary<int, (double, int)>());
            }
        }

        var tribes = Array(root, "comps")
            .Select(c => new PoolTribe(Text(c, "comp") ?? string.Empty, Int(c, "games"), Number(c, "avgPlacement"), Number(c, "top"), Number(c, "first")))
            .Where(t => t.Tribe.Length > 0 && t.Games >= MinGames)
            .ToList();

        var updated = Text(root, "updated") is { } date
            && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : DateTime.UtcNow;
        return new BattlegroundsMeta(
            heroesByBracket,
            percentiles,
            [],
            trinkets,
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>> { [100] = cards },
            updated)
        {
            Tribes = tribes,
            SharedGames = Int(root, "games"),
            SharedHeroRows = allHeroes,
            Source = BattlegroundsMeta.PoolAttribution,
        };
    }

    /// <summary>
    /// Per hero, how its average placement changes with a tribe in the lobby (pool 2.1 <c>heroTribes</c>): the placement
    /// with the tribe minus the hero's overall placement, negative = better, by tribe id (<see cref="Races.ById"/>), for
    /// tribes with at least <see cref="TribeMinGames"/> games of the hero, shrunk by <see cref="TribeShrink"/>. What the advisor adds up for the lobby.
    /// </summary>
    private static Dictionary<string, Dictionary<int, double>> TribeImpacts(JsonElement root, JsonElement heroes)
    {
        var overall = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var hero in heroes.EnumerateArray())
        {
            if (Text(hero, "hero") is { Length: > 0 } id)
            {
                overall[id] = Number(hero, "avgPlacement");
            }
        }

        var tribeIds = Races.ById.ToDictionary(r => r.Value, r => r.Key, StringComparer.Ordinal);
        var result = new Dictionary<string, Dictionary<int, double>>(StringComparer.Ordinal);
        foreach (var row in Array(root, "heroTribes"))
        {
            if (Text(row, "hero") is { Length: > 0 } hero && overall.TryGetValue(hero, out var average)
                && Text(row, "tribe") is { } tribe && tribeIds.TryGetValue(tribe, out var tribeId) && Int(row, "games") is >= TribeMinGames and var n)
            {
                (result.TryGetValue(hero, out var impact) ? impact : result[hero] = new())[tribeId] = Math.Round((Number(row, "avgPlacement") - average) * n / (n + TribeShrink), 3);
            }
        }

        return result;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            : [];

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;

    private static double Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
}
