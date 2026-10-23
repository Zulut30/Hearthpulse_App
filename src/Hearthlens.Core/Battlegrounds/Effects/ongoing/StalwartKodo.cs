namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Stalwart Kodo (BG34_322): "After you summon a minion in combat, give it this minion's maximum stats. (3 times per
/// combat.)" Golden: "give it double this minion's maximum stats." Maximum stats are the Kodo's current Attack and
/// maximum Health. Uses left are <see cref="Unit.Counter"/>: the captured TAG_SCRIPT_DATA_NUM_1 ("{0} left!"); a missing
/// or 0 value is read as 3, as is a Kodo summoned in combat (a reborn copy is a new minion with its own 3 uses).
/// </summary>
[CardEffect("BG34_322")]
internal sealed class StalwartKodo : CombatEffect
{
    private const int UsesPerCombat = 3;

    public override void OnFightStart(Fight fight, Unit self)
    {
        if (self.Counter <= 0)
        {
            self.Counter = UsesPerCombat;
        }
    }

    public override void OnSummoned(Fight fight, Unit self) => self.Counter = UsesPerCombat;

    public override void AfterFriendlySummoned(Fight fight, Unit self, Unit summoned)
    {
        if (self.Counter <= 0 || !summoned.IsOnBoard)
        {
            return;
        }

        self.Counter--;
        var factor = self.Pick(1, 2);
        fight.Buff(summoned, self.Attack * factor, self.MaxHealth * factor);
    }
}
