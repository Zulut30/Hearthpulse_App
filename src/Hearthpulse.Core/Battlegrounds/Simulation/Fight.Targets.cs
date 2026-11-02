using Hearthpulse.Core.Battlegrounds.Effects;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>What effects pick from (alive units, random, best, neighbours) and the hooks they are raised on.</summary>
public sealed partial class Fight
{
    /// <summary>Living minions of a side (left to right), optionally of a tribe and without one minion.</summary>
    public List<Unit> Alive(Side side, Tribe tribe = Tribe.All, Unit? except = null)
    {
        var result = new List<Unit>(side.Count);
        foreach (var unit in side.Minions)
        {
            if (unit.IsAlive && unit != except && unit.Is(tribe))
            {
                result.Add(unit);
            }
        }

        return result;
    }

    public Unit? RandomOf(IReadOnlyList<Unit> candidates) => candidates.Count == 0 ? null : candidates[Random.Next(candidates.Count)];

    /// <summary>A random living friendly minion of <paramref name="tribe"/>, by default not <paramref name="self"/>.</summary>
    public Unit? RandomFriendly(Unit self, Tribe tribe = Tribe.All, bool includeSelf = false) =>
        RandomOf(Alive(self.Side, tribe, includeSelf ? null : self));

    public Unit? RandomEnemy(Unit self, Tribe tribe = Tribe.All) => RandomOf(Alive(self.Enemy, tribe));

    /// <summary>Up to <paramref name="count"/> different random picks, returned in board order.</summary>
    public List<Unit> RandomDistinct(IReadOnlyList<Unit> candidates, int count)
    {
        var pool = candidates.ToList();
        var picked = new List<Unit>(Math.Min(count, pool.Count));
        while (picked.Count < count && pool.Count > 0)
        {
            var index = Random.Next(pool.Count);
            picked.Add(pool[index]);
            pool.RemoveAt(index);
        }

        picked.Sort((a, b) => a.Position.CompareTo(b.Position));
        return picked;
    }

    /// <summary>The candidate with the highest <paramref name="key"/>; ties are broken at random.</summary>
    public Unit? Best(IReadOnlyList<Unit> candidates, Func<Unit, int> key)
    {
        Unit? best = null;
        int bestKey = int.MinValue, ties = 0;
        foreach (var unit in candidates)
        {
            var value = key(unit);
            if (best is null || value > bestKey)
            {
                best = unit;
                bestKey = value;
                ties = 1;
            }
            else if (value == bestKey && Random.Next(++ties) == 0)
            {
                best = unit;
            }
        }

        return best;
    }

    /// <summary>The minions next to <paramref name="unit"/> on its board (dying ones included), or null at the edges.</summary>
    public (Unit? Left, Unit? Right) Neighbours(Unit unit)
    {
        var index = unit.Position;
        if (index < 0)
        {
            return (null, null);
        }

        var board = unit.Side.Minions;
        return (index > 0 ? board[index - 1] : null, index + 1 < board.Count ? board[index + 1] : null);
    }

    // ================================================================================================ dispatch

    private void Raise(Side side, Hook hook, Unit? a, Unit? b = null, int amount = 0, AttackOutcome outcome = default,
                       string? text = null, Unit? exclude = null)
    {
        if ((side.Hooks & hook) == 0 || _depth >= MaxDepth)
        {
            return;
        }

        _depth++;
        try
        {
            var observers = Snapshot(side, out var count);
            for (var i = 0; i < count; i++)
            {
                var observer = observers[i];
                if (observer == exclude || (observer.Hooks & hook) == 0 || (!observer.IsOnBoard && !observer.IsExtra))
                {
                    continue;
                }

                foreach (var effect in observer.Effects)
                {
                    if ((effect.Hooks & hook) == 0)
                    {
                        continue;
                    }

                    switch (hook)
                    {
                        case Hook.FriendlyAttack: effect.OnFriendlyAttack(this, observer, a!, b!); break;
                        case Hook.FriendlyAttacked: effect.OnFriendlyAttacked(this, observer, a!, b!); break;
                        case Hook.AfterFriendlyAttack: effect.AfterFriendlyAttack(this, observer, a!, b!, outcome); break;
                        case Hook.FriendlyDealtDamage: effect.AfterFriendlyDealtDamage(this, observer, a!, b!, amount); break;
                        case Hook.ShieldLost: effect.OnFriendlyShieldLost(this, observer, a!); break;
                        case Hook.FriendlyDeathrattle: effect.OnFriendlyDeathrattle(this, observer, a!); break;
                        case Hook.FriendlyDeath: effect.AfterFriendlyDeath(this, observer, a!); break;
                        case Hook.EnemyDeath: effect.AfterEnemyDeath(this, observer, a!); break;
                        case Hook.WheneverSummon: effect.WheneverFriendlySummoned(this, observer, a!); break;
                        case Hook.AfterSummon: effect.AfterFriendlySummoned(this, observer, a!); break;
                        case Hook.Reborn: effect.AfterFriendlyReborn(this, observer, a!); break;
                        case Hook.SpellCast: effect.AfterFriendlySpellCast(this, observer, text ?? string.Empty, a); break;
                    }
                }
            }
        }
        finally
        {
            _depth--;
        }
    }

    private static string Name(Unit unit) => $"{(unit.Side.IsOpponent ? "their" : "our")} {unit}";
}
