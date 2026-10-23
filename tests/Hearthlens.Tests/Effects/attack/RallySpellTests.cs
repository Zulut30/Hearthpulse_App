using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Attack;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Attack;

/// <summary>Crimson Vindicator's Rally spell (Mighty Dragonbreath). Expected stats worked out by hand from the spell text.</summary>
public sealed class RallySpellTests
{
    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    private static SimSide Board(bool golden) => Side(
        Minion(golden ? 16 : 8, golden ? 18 : 9, Keyword.DivineShield, "BG36_241", Tribe.Dragon, golden),
        Minion(1, 1, cardId: "D", tribes: Tribe.Dragon),
        Minion(1, 1, Keyword.DivineShield, "N"),
        Minion(1, 1, cardId: "P"));

    [Fact]
    public void Crimson_vindicator_casts_mighty_dragonbreath()
    {
        // +3/+2 to all, again to Dragons, again to minions with Divine Shield:
        // Vindicator (Dragon, shield) 8/9 → 17/15, D (Dragon) 1/1 → 7/5, N (shield) 1/1 → 7/5, P 1/1 → 4/3.
        // The 17-Attack Vindicator then kills the 0/17.
        var result = Fight(Board(golden: false), Side(Minion(0, 17, cardId: "E")), true, Effects(typeof(CrimsonVindicator)));

        Assert.Equal(1, result.Outcome);
        Assert.Single(result.Lines("we cast BG36_246"));
        Assert.Equal([(17, 15), (7, 5), (7, 5), (4, 3)], Stats(result.Ours));
    }

    [Fact]
    public void Crimson_vindicator_adds_the_tavern_spell_bonus_to_each_part()
    {
        // Tavern spells give +1/+1 more: each part is +4/+3. Vindicator 8/9 → 20/18, D → 9/7, N → 9/7, P → 5/4.
        var ours = Board(golden: false) with
        {
            Tags = new Dictionary<string, int> { [CrimsonVindicator.SpellAttackTag] = 1, [CrimsonVindicator.SpellHealthTag] = 1 },
        };
        var result = Fight(ours, Side(Minion(0, 20, cardId: "E")), true, Effects(typeof(CrimsonVindicator)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal([(20, 18), (9, 7), (9, 7), (5, 4)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_crimson_vindicator_casts_it_twice()
    {
        // Twice: Vindicator 16/18 → +18/+12 → 34/30, D → +12/+8 → 13/9, N → 13/9, P → +6/+4 → 7/5. Kills the 0/34.
        var result = Fight(Board(golden: true), Side(Minion(0, 34, cardId: "E")), true, Effects(typeof(CrimsonVindicator)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(2, result.Lines("we cast BG36_246").Count);
        Assert.Equal([(34, 30), (13, 9), (13, 9), (7, 5)], Stats(result.Ours));
    }

    [Fact]
    public void Crimson_vindicator_spell_reaches_after_spell_cast_hooks()
    {
        // A test-only "after you cast a spell, gain +1 Attack" minion sees the cast: 1/5 → +3/+2 (all) → 4/7, then +1 → 5/7.
        var result = Fight(
            Side(Minion(8, 9, Keyword.DivineShield, "BG36_241", Tribe.Dragon), Minion(1, 5, cardId: "WATCH")),
            Side(Minion(0, 17, cardId: "E")),
            true, Effects(typeof(CrimsonVindicator), typeof(SpellWatcher)));

        Assert.Equal((5, 7), (result.Ours[1].Attack, result.Ours[1].Health));
    }

    [CardEffect("WATCH")]
    private sealed class SpellWatcher : CombatEffect
    {
        public override void AfterFriendlySpellCast(Fight fight, Unit self, string spellCardId, Unit? target)
        {
            if (spellCardId == CrimsonVindicator.MightyDragonbreathId)
            {
                fight.Buff(self, 1, 0);
            }
        }
    }
}
