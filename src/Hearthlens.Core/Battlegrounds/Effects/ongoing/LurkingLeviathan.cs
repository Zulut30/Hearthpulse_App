namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Lurking Leviathan (BG35_602): "Whenever you summon a Beast, give it +{1} Attack and improve this permanently."
/// Printed {1} is 3 (golden 6). The current bonus is the captured TAG_SCRIPT_DATA_NUM_2 (kept in
/// <see cref="Unit.Counter2"/>); a Leviathan summoned in combat starts at the printed value. The card does not say by
/// how much it improves: +1 per Beast (golden +2) is assumed.
/// </summary>
[CardEffect("BG35_602")]
internal sealed class LurkingLeviathan : CombatEffect
{
    public override void WheneverFriendlySummoned(Fight fight, Unit self, Unit summoned)
    {
        if (!summoned.Is(Tribe.Beast) || !summoned.IsOnBoard)
        {
            return;
        }

        var bonus = self.Counter2 > 0 ? self.Counter2 : self.Pick(3, 6);
        fight.Buff(summoned, bonus, 0);
        self.Counter2 = bonus + self.Pick(1, 2);
    }
}
