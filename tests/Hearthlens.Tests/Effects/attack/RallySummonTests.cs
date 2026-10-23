using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Attack;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Attack;

/// <summary>
/// Rally summons: Flittering Bat, Hoarding Hyena, Expert Aviator and Jailbird Juggernaut. Expected results are worked out
/// by hand from the card text in each comment.
/// </summary>
public sealed class RallySummonTests
{
    private static List<(int, int)> Stats(IEnumerable<SimMinion> minions) => minions.Select(m => (m.Attack, m.Health)).ToList();

    // ------------------------------------------------------------------------------------------- Flittering Bat

    [Fact]
    public void Flittering_bat_summons_a_bat_to_its_right_that_attacks_next()
    {
        // Bat 1/4 vs 0/2: the Rally summons a 1/1 Foraging Bat at slot 1 (right of the attacker, at the attack gap),
        // the Bat hits 1 (2→1); the token attacks next and kills.
        var result = Fight(Side(Minion(1, 4, cardId: "BG36_200", tribes: Tribe.Beast)), Side(Minion(0, 2, cardId: "E")), true,
                           Effects(typeof(FlitteringBat)));

        Assert.Equal(1, result.Outcome);
        Assert.Contains("summoned our BG36_200t 1/1 at 1", result.Log);
        Assert.True(result.IndexOf("our BG36_200t 1/1 attacks their E") > result.IndexOf("summoned our BG36_200t"));
        Assert.Equal([Tokens.ForagingBatId], result.Ours.Skip(1).Select(m => m.CardId));
        Assert.Equal([(1, 4), (1, 1)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_flittering_bat_summons_two_normal_bats()
    {
        // Golden 2/8 vs 0/4: two 1/1 bats (normal, not golden) at slots 1 and 2; Bat hits 2 (4→2), bat 1 (→1), bat 1: dies.
        var result = Fight(Side(Minion(2, 8, cardId: "BG36_200", tribes: Tribe.Beast, golden: true)), Side(Minion(0, 4, cardId: "E")), true,
                           Effects(typeof(FlitteringBat)));

        Assert.Equal(1, result.Outcome);
        Assert.Contains("summoned our BG36_200t 1/1 at 1", result.Log);
        Assert.Contains("summoned our BG36_200t 1/1 at 2", result.Log);
        Assert.Equal([(2, 8), (1, 1), (1, 1)], Stats(result.Ours));
        Assert.All(result.Ours.Skip(1), m => Assert.False(m.Golden));
    }

    // ------------------------------------------------------------------------------------------- Hoarding Hyena

    [Fact]
    public void Hoarding_hyena_summons_a_tasty_lobster()
    {
        // Hyena 4/6 vs 0/4: the Rally summons a 2/1 Tasty Lobster (Beast, deathrattle) right of it; the Hyena kills.
        var result = Fight(Side(Minion(4, 6, cardId: "BG36_210", tribes: Tribe.Beast)), Side(Minion(0, 4, cardId: "E")), true,
                           Effects(typeof(HoardingHyena)));

        Assert.Equal(1, result.Outcome);
        var lobster = result.Ours[1];
        Assert.Equal((Tokens.TastyLobsterId, 2, 1, false), (lobster.CardId, lobster.Attack, lobster.Health, lobster.Golden));
        Assert.Equal(Tribe.Beast, lobster.Tribes);
        Assert.Equal(CardMechanic.Deathrattle, lobster.Mechanics);
        Assert.Equal((2, 1), (lobster.ScriptData1, lobster.ScriptData2));
    }

    [Fact]
    public void Golden_hoarding_hyena_summons_a_golden_tasty_lobster()
    {
        // Golden 8/12 vs 0/8: a Golden Tasty Lobster 4/2 (its deathrattle values +4/+2).
        var result = Fight(Side(Minion(8, 12, cardId: "BG36_210", tribes: Tribe.Beast, golden: true)), Side(Minion(0, 8, cardId: "E")), true,
                           Effects(typeof(HoardingHyena)));

        var lobster = result.Ours[1];
        Assert.Equal((Tokens.TastyLobsterId, 4, 2, true), (lobster.CardId, lobster.Attack, lobster.Health, lobster.Golden));
        Assert.Equal((4, 2), (lobster.ScriptData1, lobster.ScriptData2));
    }

    // ------------------------------------------------------------------------------------------- Expert Aviator

    private static readonly SimMinion[] AviatorHand =
    [
        Minion(3, 3, cardId: "M1", tribes: Tribe.Murloc),
        Minion(9, 9, cardId: "X", tribes: Tribe.Beast),
        Minion(5, 2, cardId: "M2", tribes: Tribe.Murloc),
    ];

    [Fact]
    public void Expert_aviator_summons_the_highest_attack_murloc_from_our_hand()
    {
        // Aviator 3/5, hand M1 3/3, X 9/9 (Beast), M2 5/2; vs 0/8. Rally: M2 (highest-Attack Murloc; X is no Murloc)
        // comes in right of the Aviator. Aviator hits 3 (8→5); M2 attacks next: 5, dies.
        var ours = Side(Minion(3, 5, cardId: "BG34_140", tribes: Tribe.Murloc)) with { Hand = AviatorHand };
        var result = Fight(ours, Side(Minion(0, 8, cardId: "E")), true, Effects(typeof(ExpertAviator)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(["BG34_140", "M2"], result.Ours.Select(m => m.CardId));
        Assert.Equal([(3, 5), (5, 2)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_expert_aviator_summons_the_two_highest_attack_murlocs()
    {
        // Golden 6/10 with the same hand vs 0/20. Rally: M2 then M1, left to right. Aviator 6 (20→14), M2 5 (→9), M1 3 (→6).
        // Aviator again: no Murloc left in hand, hits 6: dies.
        var ours = Side(Minion(6, 10, cardId: "BG34_140", tribes: Tribe.Murloc, golden: true)) with { Hand = AviatorHand };
        var result = Fight(ours, Side(Minion(0, 20, cardId: "E")), true, Effects(typeof(ExpertAviator)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(["BG34_140", "M2", "M1"], result.Ours.Select(m => m.CardId));
        Assert.Equal(2, result.Lines("summoned our ").Count);
    }

    [Fact]
    public void Expert_aviator_does_nothing_for_the_opponent_whose_hand_is_hidden()
    {
        // Even if a hand were passed for the opponent, the engine never exposes it.
        var theirs = Side(Minion(3, 5, cardId: "BG34_140", tribes: Tribe.Murloc)) with { Hand = AviatorHand };
        var result = Fight(Side(Minion(0, 30, cardId: "E")), theirs, false, Effects(typeof(ExpertAviator)));

        Assert.Empty(result.Lines("summoned their "));
        Assert.False(EffectRegistry.Default.Implements("BG34_140", golden: false, isOpponent: true));
        Assert.True(EffectRegistry.Default.Implements("BG34_140", golden: true, isOpponent: false));
    }

    // ------------------------------------------------------------------------------------------- Jailbird Juggernaut

    [Fact]
    public void Jailbird_juggernaut_golem_attacks_the_target_first()
    {
        // Juggernaut 6/15 vs 4/10. Rally: a 6/15 Golem (its current stats) right of it attacks the 4/10 first:
        // 4/10 → 4/4, Golem → 6/11. Then the Juggernaut's attack goes on: 4/4 dies, Juggernaut → 6/11.
        var result = Fight(Side(Minion(6, 15, cardId: "BG36_333", tribes: Tribe.Quilboar)), Side(Minion(4, 10, cardId: "E")), true,
                           Effects(typeof(JailbirdJuggernaut)));

        Assert.Equal(1, result.Outcome);
        var attacks = result.Log.Where(l => l.Contains(" attacks ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["our BG36_333 6/15 attacks their E 4/10", "our BG29_864t 6/15 attacks their E 4/10"], attacks);
        Assert.Equal(["BG36_333", Tokens.DesertedGolemId], result.Ours.Select(m => m.CardId));
        Assert.Equal([(6, 11), (6, 11)], Stats(result.Ours));
    }

    [Fact]
    public void Jailbird_juggernaut_attack_is_cancelled_when_the_golem_kills_the_target()
    {
        // Juggernaut 6/15 vs 4/6: the 6/15 Golem kills the 4/6 (Golem → 6/11); the Juggernaut's own attack is cancelled,
        // so it takes no damage.
        var result = Fight(Side(Minion(6, 15, cardId: "BG36_333", tribes: Tribe.Quilboar)), Side(Minion(4, 6, cardId: "E")), true,
                           Effects(typeof(JailbirdJuggernaut)));

        Assert.Equal(1, result.Outcome);
        Assert.Contains("attack cancelled", result.Log);
        Assert.Equal([(6, 15), (6, 11)], Stats(result.Ours));
    }

    [Fact]
    public void Golden_jailbird_juggernaut_golem_has_double_its_stats()
    {
        // Golden 12/30 vs 4/30: a 24/60 Golem (normal token) hits first: 4/30 → 4/6, Golem → 24/56; Juggernaut kills, → 12/26.
        var result = Fight(Side(Minion(12, 30, cardId: "BG36_333", tribes: Tribe.Quilboar, golden: true)), Side(Minion(4, 30, cardId: "E")), true,
                           Effects(typeof(JailbirdJuggernaut)));

        Assert.Equal(1, result.Outcome);
        Assert.Contains("summoned our BG29_864t 24/60 at 1", result.Log);
        Assert.Equal([(12, 26), (24, 56)], Stats(result.Ours));
        Assert.False(result.Ours[1].Golden);
    }
}
