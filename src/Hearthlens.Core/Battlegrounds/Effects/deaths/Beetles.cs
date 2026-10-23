namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// The Beetle package: Buzzing Vermin, Forest Rover, Turquoise Skitterer and Ravaging Scorpid summon "a {0}/{1} Beetle"
/// and Skitterer/Scorpid make "your Beetles have +X/+Y this game".
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Base stats: the summoner's TAG_SCRIPT_DATA_NUM_1/2, which its card text shows as the Beetle's stats (2/2 plus
/// the player's Beetle bonus so far). A summoner without script data (made in combat, or the log did not send it) uses
/// the first captured friendly Beetle summoner that has them (the bonus is player-wide), else 2/2.</item>
/// <item>"+X/+Y this game" during the fight: living Beetles on the board get it now, and every Beetle summoned later this
/// fight gets it through <see cref="Side.AddSummonBuff(Func{Unit, bool}, int, int)"/>, whoever summons it (Beetle Band,
/// reborn copies included). Beetles are recognised by the Beetle token's card id.</item>
/// </list>
/// </remarks>
internal static class Beetles
{
    // Cards whose text shows the player's current Beetle as "{0}/{1} Beetle".
    private static readonly string[] Summoners = ["BG31_803", "BG31_801", "BG31_809", "BG36_209"];

    private static readonly Func<Unit, bool> IsBeetleCheck = IsBeetle;

    public static bool IsBeetle(Unit unit) => unit.CardId == Tokens.BeetleId;

    /// <summary>The stats of the Beetle <paramref name="summoner"/> summons, before in-fight "+X/+Y this game" bonuses.</summary>
    public static (int Attack, int Health) Stats(Unit summoner)
    {
        if (HasStats(summoner.Spec))
        {
            return Of(summoner.Spec);
        }

        var info = summoner.Side.Info;
        foreach (var minion in info.Minions)
        {
            if (IsSummoner(minion) && HasStats(minion))
            {
                return Of(minion);
            }
        }

        if (!summoner.Side.IsOpponent)
        {
            foreach (var minion in info.Hand)
            {
                if (IsSummoner(minion) && HasStats(minion))
                {
                    return Of(minion);
                }
            }
        }

        return (2, 2);
    }

    /// <summary>Summons <paramref name="count"/> Beetles where <paramref name="summoner"/> is (its old slot when it died).</summary>
    public static void Summon(Fight fight, Unit summoner, int count)
    {
        var (attack, health) = Stats(summoner);
        fight.SummonAt(summoner, Tokens.Make(Tokens.BeetleId, attack, health, Tribe.Beast), count);
    }

    /// <summary>"Your Beetles have +X/+Y this game": living Beetles now, and Beetles summoned later this fight.</summary>
    public static void Improve(Fight fight, Side side, int attack, int health)
    {
        foreach (var unit in fight.Alive(side))
        {
            if (IsBeetle(unit))
            {
                fight.Buff(unit, attack, health);
            }
        }

        side.AddSummonBuff(IsBeetleCheck, attack, health);
    }

    private static bool IsSummoner(SimMinion minion) => Array.IndexOf(Summoners, minion.CardId) >= 0;

    private static bool HasStats(SimMinion minion) => minion.ScriptData1 > 0 || minion.ScriptData2 > 0;

    private static (int, int) Of(SimMinion minion) => (minion.ScriptData1, Math.Max(1, minion.ScriptData2));
}
