namespace Hearthlens.Core.Battlegrounds.Effects.Deaths;

/// <summary>
/// Titus Rivendare (BG25_354): "Your Deathrattles trigger an extra time." Golden: "trigger 2 extra times."
/// </summary>
/// <remarks>
/// Counted by the engine while Titus is alive: a Titus that dies in the same death phase as other minions no longer
/// adds its triggers (engine rule).
/// </remarks>
[CardEffect("BG25_354")]
internal sealed class TitusRivendare : CombatEffect
{
    public override int ExtraDeathrattles(Fight fight, Unit self) => self.Pick(1, 2);
}
