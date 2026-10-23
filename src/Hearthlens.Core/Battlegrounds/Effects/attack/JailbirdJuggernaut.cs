namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Jailbird Juggernaut (BG36_333): "Rally: Summon a Golem with this minion's stats to attack the target first."
/// Golden: "Summon a Golem with double this minion's stats to attack the target first."
/// </summary>
/// <remarks>
/// The Golem is the Deserted Golem token (BG29_864t, no type; a normal one for the golden card too) created with the
/// Juggernaut's current Attack and Health (golden: doubled) as its stats, so "summoned later this combat" buffs still apply
/// on top. It is summoned to the right of the Juggernaut and attacks the same target at once, out of turn; then the
/// Juggernaut's own attack goes on, or is cancelled if the Golem killed the target. With a full board there is no Golem
/// and the Juggernaut attacks normally. The Golem stays on the board and later attacks in turn like any minion.
/// </remarks>
[CardEffect("BG36_333")]
internal sealed class JailbirdJuggernaut : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        var factor = self.Pick(1, 2);
        var golem = Tokens.Make(Tokens.DesertedGolemId, self.Attack * factor, Math.Max(1, self.Health) * factor, Tribe.None);
        if (fight.SummonAt(self, golem) is { } summoned)
        {
            fight.AttackNow(summoned, target);
        }
    }
}
