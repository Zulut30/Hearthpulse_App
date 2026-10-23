using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;

namespace Hearthlens.Tests.Sim;

/// <summary>
/// Helpers for deterministic combat tests. Build boards with <see cref="Minion"/>, run one fight with scripted random
/// picks (<see cref="Fight"/>) and assert on the survivors (<see cref="FightResult.Ours"/>/<see cref="FightResult.Theirs"/>)
/// or the fight log (<see cref="FightResult.Log"/>).
/// </summary>
public static class SimTestKit
{
    /// <summary>A minion whose printed stats and keywords are the given ones.</summary>
    public static SimMinion Minion(int attack, int health, Keyword keywords = Keyword.None, string cardId = "", Tribe tribes = Tribe.None,
                                   bool golden = false, CardMechanic mechanics = CardMechanic.None) =>
        SimMinion.Create(cardId, attack, health, tribes, keywords, golden, mechanics);

    public static SimSide Side(params SimMinion[] minions) => new(minions);

    /// <summary>
    /// One fight. Random picks come from <paramref name="picks"/> in order (each clamped to the number of options; 0 once
    /// they run out): target choices, "random friendly minion" choices and the first-attacker coin flip all use them.
    /// </summary>
    public static FightResult Fight(SimSide ours, SimSide theirs, bool? oursFirst = true, EffectRegistry? effects = null, params int[] picks) =>
        CombatSimulator.RunSingle(ours, theirs, oursFirst, new ScriptedRandom(picks), effects ?? EffectRegistry.Default);

    /// <summary>A registry with only these effect classes.</summary>
    public static EffectRegistry Effects(params Type[] types) => EffectRegistry.FromTypes(types);

    /// <summary>Log lines (in order) that start with <paramref name="prefix"/>.</summary>
    public static List<string> Lines(this FightResult result, string prefix) =>
        result.Log.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)).ToList();

    /// <summary>Index of the first log line containing <paramref name="text"/>, or -1.</summary>
    public static int IndexOf(this FightResult result, string text)
    {
        for (var i = 0; i < result.Log.Count; i++)
        {
            if (result.Log[i].Contains(text, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>A <see cref="Random"/> whose picks are scripted: each call returns the next value (clamped), then 0.</summary>
public sealed class ScriptedRandom(params int[] picks) : Random
{
    private int _index;

    public int Calls { get; private set; }

    public override int Next(int maxValue)
    {
        Calls++;
        var value = _index < picks.Length ? picks[_index++] : 0;
        return maxValue <= 1 ? 0 : Math.Clamp(value, 0, maxValue - 1);
    }

    public override int Next(int minValue, int maxValue) => minValue + Next(maxValue - minValue);

    public override int Next() => Next(int.MaxValue);

    public override double NextDouble() => 0;
}
