namespace Hearthlens.Core.Battlegrounds.Effects.Ongoing;

/// <summary>
/// Banana Slamma (BG26_802): "After you summon a Beast in combat, double its Attack." Golden: "triple its Attack."
/// An "after you summon" trigger, so it applies after "whenever you summon" buffs (Lurking Leviathan) and after the
/// lingering "summoned later this combat" buffs. Reborn copies are summons too (engine rule).
/// </summary>
[CardEffect("BG26_802")]
internal sealed class BananaSlamma : CombatEffect
{
    public override void AfterFriendlySummoned(Fight fight, Unit self, Unit summoned)
    {
        if (!summoned.Is(Tribe.Beast) || !summoned.IsOnBoard)
        {
            return;
        }

        fight.Buff(summoned, summoned.Attack * (self.Pick(2, 3) - 1), 0);
    }
}
