namespace Hearthlens.Core.Battlegrounds.Effects.Attack;

/// <summary>
/// Hoarding Hyena (BG36_210): "Rally: Summon a Tasty Lobster." Golden: "Rally: Summon a Golden Tasty Lobster."
/// The Lobster (BG36_202, 2/1 Beast; golden 4/2) appears to the right of the Hyena, before damage. Its deathrattle
/// ("Give a random friendly Beast +{0}/+{1}") is the Lobster's own effect, registered separately.
/// </summary>
/// <remarks>
/// Assumption: the summoned Lobster carries the card's printed TAG_SCRIPT_DATA_NUM_1/2 (+2/+1; golden +4/+2) as its
/// counters, like a Lobster captured from the board. "Improve your future Tasty Lobsters" upgrades from earlier in the game
/// are not known for a Lobster created in combat, so they are not included.
/// </remarks>
[CardEffect("BG36_210")]
internal sealed class HoardingHyena : CombatEffect
{
    public override void OnAttack(Fight fight, Unit self, Unit target)
    {
        var lobster = Tokens.TastyLobster(self.Golden) with { ScriptData1 = self.Pick(2, 4), ScriptData2 = self.Pick(1, 2) };
        fight.SummonAt(self, lobster);
    }
}
