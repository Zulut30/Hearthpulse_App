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

/// <summary>Laying the pool's meta over the API's (heroes, cards and trinkets blended by sample size, bracket by bracket).</summary>
public static partial class PoolClient
{
    /// <summary>
    /// Adds the shared games to the base numbers, weighted by sample size. Nothing is replaced: the base and the
    /// pool both stay in the average, and the pool's share grows as more games arrive. The pool reports all ranks,
    /// so its hero and card numbers are merged into every MMR bracket of the base. A base row without a sample size
    /// (the API's heroes) cannot be weighed and keeps its own numbers; a hero the base lacks is added only once the
    /// pool has <see cref="MinGames"/> games of it. What the base has none of (comps, trinkets, tribes, percentiles,
    /// turn results) comes from the pool. When the pool has its own rating brackets (2.1, trusted MMR floors) that the
    /// base has too, those are merged bracket by bracket instead of its all-ranks rows.
    /// </summary>
    internal static BattlegroundsMeta Merge(BattlegroundsMeta baseMeta, BattlegroundsMeta pool)
    {
        var ownBrackets = pool.HeroesByBracket
            .Where(kv => kv.Key != 100 && baseMeta.HeroesByBracket.ContainsKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var poolHeroes = ownBrackets.Count > 0
            ? ownBrackets
            : pool.SharedHeroRows.Count > 0
                ? new Dictionary<int, IReadOnlyDictionary<string, MetaHero>> { [100] = pool.SharedHeroRows }
                : pool.HeroesByBracket;
        var heroes = MergeBrackets(baseMeta.HeroesByBracket, poolHeroes, (a, b) => Blend(a, b), h => h.DataPoints >= MinGames);
        var cards = MergeBrackets(baseMeta.CardsByBracket, pool.CardsByBracket, (a, b) => Blend(a, b), _ => true);

        var trinkets = baseMeta.Trinkets.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        foreach (var (id, add) in pool.Trinkets)
        {
            trinkets[id] = trinkets.TryGetValue(id, out var have) ? Blend(have, add) : add;
        }

        var updated = pool.Updated > baseMeta.Updated ? pool.Updated : baseMeta.Updated;
        return new BattlegroundsMeta(
            heroes,
            baseMeta.Percentiles.Count > 0 ? baseMeta.Percentiles : pool.Percentiles,
            baseMeta.Comps.Count > 0 ? baseMeta.Comps : pool.Comps,
            trinkets,
            cards,
            updated)
        {
            Tribes = pool.Tribes.Count > 0 ? pool.Tribes : baseMeta.Tribes,
            SharedGames = pool.SharedGames,
            Source = baseMeta.StoredSource == pool.StoredSource ? baseMeta.StoredSource : $"{baseMeta.StoredSource} + {BattlegroundsMeta.PoolName}",
        };
    }

    /// <summary>
    /// Pools two bracket maps. When the pool only has the all-ranks bracket (100), it is merged into every bracket
    /// of the base and adds no bracket of its own; when it has real per-bracket data, only the matching bracket is
    /// merged. Rows the base lacks are added when <paramref name="addMissing"/> allows.
    /// </summary>
    private static Dictionary<int, IReadOnlyDictionary<string, T>> MergeBrackets<T>(
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, T>> baseByBracket,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, T>> poolByBracket,
        Func<T, T, T> blend,
        Func<T, bool> addMissing)
    {
        var allRanksOnly = poolByBracket.Count == 1 && poolByBracket.ContainsKey(100);
        var brackets = allRanksOnly && baseByBracket.Count > 0 ? baseByBracket.Keys : baseByBracket.Keys.Union(poolByBracket.Keys);
        var result = new Dictionary<int, IReadOnlyDictionary<string, T>>();
        foreach (var bracket in brackets)
        {
            var target = baseByBracket.TryGetValue(bracket, out var existing)
                ? new Dictionary<string, T>(existing, StringComparer.Ordinal)
                : new Dictionary<string, T>(StringComparer.Ordinal);
            var adds = allRanksOnly ? poolByBracket.GetValueOrDefault(100) : poolByBracket.GetValueOrDefault(bracket);
            if (adds is not null)
            {
                foreach (var (id, add) in adds)
                {
                    if (target.TryGetValue(id, out var have))
                    {
                        target[id] = blend(have, add);
                    }
                    else if (addMissing(add))
                    {
                        target[id] = add;
                    }
                }
            }

            result[bracket] = target;
        }

        return result;
    }

    private static MetaHero Blend(MetaHero a, MetaHero b)
    {
        var total = a.DataPoints + b.DataPoints;
        if (a.DataPoints <= 0 || b.DataPoints <= 0)
        {
            // Without the base's sample size there is nothing to weigh by: its numbers stay, the pool only fills gaps.
            return a.TribeImpact.Count == 0 && b.TribeImpact.Count > 0 ? a with { TribeImpact = b.TribeImpact } : a;
        }

        double Mix(double x, double y) => ((x * a.DataPoints) + (y * b.DataPoints)) / total;
        var impact = new Dictionary<int, double>(a.TribeImpact);
        foreach (var (tribe, value) in b.TribeImpact)
        {
            impact[tribe] = value;
        }

        return a with
        {
            DataPoints = total,
            AveragePosition = Mix(a.AveragePosition, b.AveragePosition),
            PickRate = Mix(a.PickRate, b.PickRate),
            Top4Rate = Mix(a.Top4Rate, b.Top4Rate),
            FirstRate = Mix(a.FirstRate, b.FirstRate),
            TribeImpact = impact,
        };
    }

    private static MetaCard Blend(MetaCard a, MetaCard b)
    {
        // Turn results only come from one side (the API has none), so they are taken as they are, not blended.
        var turns = a.Turns.Count > 0 ? a.Turns : b.Turns;
        var total = a.DataPoints + b.DataPoints;
        return a.DataPoints <= 0 || b.DataPoints <= 0
            ? a with { Turns = turns }
            : a with
            {
                DataPoints = total,
                AveragePlacement = ((a.AveragePlacement * a.DataPoints) + (b.AveragePlacement * b.DataPoints)) / total,
                Turns = turns,
            };
    }

    private static MetaTrinket Blend(MetaTrinket a, MetaTrinket b)
    {
        var total = a.DataPoints + b.DataPoints;
        return a.DataPoints <= 0 || b.DataPoints <= 0
            ? a
            : a with
            {
                DataPoints = total,
                AveragePlacement = ((a.AveragePlacement * a.DataPoints) + (b.AveragePlacement * b.DataPoints)) / total,
                PickRate = ((a.PickRate * a.DataPoints) + (b.PickRate * b.DataPoints)) / total,
            };
    }
}
