namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Falling Sky Golem (BG35_342): "Divine Shield. Has +4/+2 for each Deathrattle you've triggered this game (wherever this
/// is)." Golden: "+8/+4". Divine Shield is a keyword the engine handles. After every friendly deathrattle trigger
/// (multiplied triggers included; <see cref="Side.DeathrattlesTriggered"/>) each living friendly Golem gains the bonus
/// once. Only deathrattles with a registered effect are seen as triggered. A Golem summoned in combat (a reborn copy)
/// comes with the bonus for every trigger this game (see <see cref="CountingAura"/>).
/// </summary>
[CardEffect("BG35_342")]
internal sealed class FallingSkyGolem : CountingAura
{
    protected override string CardId => "BG35_342";

    protected override (int Attack, int Health) Printed => (4, 2);

    protected override (int Attack, int Health) PerEvent => (4, 2);

    protected override int EventsThisFight(Fight fight, Side side) => side.DeathrattlesTriggered;

    public override void OnFriendlyDeathrattle(Fight fight, Unit self, Unit source) => CatchUp(fight, self);
}
