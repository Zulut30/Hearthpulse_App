namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Ravaging Scorpid (BG36_209): "After a friendly minion attacks, your Beetles have +4/+4 this game. Deathrattle: Summon
/// a {0}/{1} Beetle." Golden: "+8/+8 ... Summon two {0}/{1} Beetles."
/// </summary>
/// <remarks>
/// "A friendly minion" includes the Scorpid itself. The bonus goes to living Beetles and to Beetles summoned later this
/// fight (see <see cref="Beetles"/>). The engine runs after-attack hooks after combat damage and before the death phase,
/// so a Scorpid mortally wounded by that attack still gives its bonus (engine rule, unverified against real logs).
/// </remarks>
[CardEffect("BG36_209")]
internal sealed class RavagingScorpid : CombatEffect
{
    public override void AfterFriendlyAttack(Fight fight, Unit self, Unit attacker, Unit target, AttackOutcome outcome)
    {
        var bonus = self.Pick(4, 8);
        Beetles.Improve(fight, self.Side, bonus, bonus);
    }

    public override void Deathrattle(Fight fight, Unit self) => Beetles.Summon(fight, self, self.Pick(1, 2));
}
