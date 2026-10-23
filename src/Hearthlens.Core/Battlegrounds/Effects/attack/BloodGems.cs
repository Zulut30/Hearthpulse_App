namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// "This plays a Blood Gem on X": a Blood Gem (BG20_GEM, "Give a minion +{0}/+{1}", printed +1/+1) played by a minion
/// in combat, using the owner's Blood Gem value.
/// </summary>
/// <remarks>
/// Assumptions (unverified against a real Power.log):
/// <list type="bullet">
/// <item>The player tags <c>BACON_BLOODGEMBUFFATKVALUE</c> / <c>BACON_BLOODGEMBUFFHEALTHVALUE</c> hold the bonus on top of the
/// printed +1/+1 (a gem gives +(1 + bonus)/+(1 + bonus)). The opponent's player entity may not carry them; a missing tag
/// reads as 0, so the opponent's gems default to +1/+1.</item>
/// <item>Each gem is applied as its own buff (one <see cref="Fight.Buff"/> per gem). Gems are not routed through
/// <see cref="Fight.CastSpell"/>: whether a Blood Gem played by a minion counts as "you cast a spell" for spell triggers
/// is not known, so "whenever a Blood Gem is played on this" (Geomagus Roogug, conditional) does not react to them.</item>
/// </list>
/// </remarks>
internal static class BloodGems
{
    public const string AttackTag = "BACON_BLOODGEMBUFFATKVALUE";
    public const string HealthTag = "BACON_BLOODGEMBUFFHEALTHVALUE";

    /// <summary>Stats one Blood Gem of this player gives.</summary>
    public static (int Attack, int Health) Value(Side side) =>
        (1 + Math.Max(0, side.Counter(AttackTag)), 1 + Math.Max(0, side.Counter(HealthTag)));

    /// <summary>Plays <paramref name="count"/> Blood Gems of <paramref name="owner"/>'s player on <paramref name="target"/>.</summary>
    public static void Play(Fight fight, Unit owner, Unit target, int count)
    {
        var (attack, health) = Value(owner.Side);
        for (var i = 0; i < count && target.IsOnBoard; i++)
        {
            fight.Buff(target, attack, health);
        }
    }
}
