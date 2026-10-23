namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Crimson Vindicator (BG36_241): "Divine Shield. Rally: Cast Mighty Dragonbreath." Golden: "Cast Mighty Dragonbreath twice."
/// </summary>
/// <remarks>
/// Mighty Dragonbreath (BG36_246, a Tavern spell): "Give your minions +{0}/+{1}. Repeat for your Dragons. Repeat for your
/// minions with Divine Shield." with printed +3/+2 (TAG_SCRIPT_DATA_NUM_1/2). Assumptions: the player's Tavern spell bonus
/// (player tags <c>TAVERN_SPELL_ATTACK_INCREASE</c> / <c>TAVERN_SPELL_HEALTH_INCREASE</c>, read as 0 when missing, e.g. on
/// the opponent's player entity) is added to each +{0}/+{1}; "your minions" are the living friendly minions, this one
/// included, and "with Divine Shield" is checked when that part resolves. Each cast goes through
/// <see cref="Fight.CastSpell"/>, so "after you cast a Tavern spell" effects can react. Divine Shield itself is a
/// captured keyword.
/// </remarks>
[CardEffect("BG36_241")]
internal sealed class CrimsonVindicator : CombatEffect
{
    public const string MightyDragonbreathId = "BG36_246";
    public const string SpellAttackTag = "TAVERN_SPELL_ATTACK_INCREASE";
    public const string SpellHealthTag = "TAVERN_SPELL_HEALTH_INCREASE";

    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        var side = self.Side;
        for (var cast = 0; cast < self.Pick(1, 2); cast++)
        {
            fight.CastSpell(side, MightyDragonbreathId, null, f => MightyDragonbreath(f, side));
        }
    }

    private static void MightyDragonbreath(Fight fight, Side side)
    {
        var attack = 3 + Math.Max(0, side.Counter(SpellAttackTag));
        var health = 2 + Math.Max(0, side.Counter(SpellHealthTag));
        foreach (var unit in fight.Alive(side))
        {
            fight.Buff(unit, attack, health);
        }

        foreach (var unit in fight.Alive(side, Tribe.Dragon))
        {
            fight.Buff(unit, attack, health);
        }

        foreach (var unit in fight.Alive(side).Where(u => u.DivineShield).ToList())
        {
            fight.Buff(unit, attack, health);
        }
    }
}
