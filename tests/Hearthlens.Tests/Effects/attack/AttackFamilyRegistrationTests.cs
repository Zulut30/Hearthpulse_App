using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Attack;

/// <summary>
/// The attack family (Rally, on-attack, after-attack, cleave and splash) is registered in the default registry, so these
/// minions no longer count as "minion effects not simulated"; plus a smoke run of the real effects together.
/// </summary>
public sealed class AttackFamilyRegistrationTests
{
    public static readonly TheoryData<string> CardIds = new()
    {
        "BG29_888", "BG33_886", "BG36_207", "BG20_104", "BG34_604", "BG33_883", "BG34_320", "BG33_430", "BG29_816",
        "BG36_211", "BG36_208", "BG36_200", "BG36_210", "BG34_140", "BG36_333", "BG25_016", "BG33_318", "BG36_849",
        "BGS_126", "BG36_102", "BG26_817", "BG27_017", "BG36_241",
    };

    [Theory]
    [MemberData(nameof(CardIds))]
    public void Card_is_combat_relevant_and_now_simulated_normal_and_golden(string cardId)
    {
        var row = CombatRelevance.Find(cardId);
        Assert.NotNull(row);
        Assert.Equal(Relevance.Relevant, row.Relevance);

        var effects = EffectRegistry.Default;
        Assert.True(effects.Implements(cardId, golden: false));
        Assert.True(effects.Implements(cardId, golden: true));
        // The golden card id resolves to the same effect.
        Assert.Same(effects.Get(cardId, golden: true), effects.Get(row.GoldenCardId, golden: true));
        Assert.False(CombatRelevance.IsUnsimulated(cardId, false, true, false, [], isOpponent: false, effects));
        Assert.False(CombatRelevance.IsUnsimulated(cardId, true, true, false, [], isOpponent: false, effects));
        // Only Expert Aviator needs a hand: it stays "not simulated" on the opponent's side.
        Assert.Equal(cardId == "BG34_140", CombatRelevance.IsUnsimulated(cardId, false, true, false, [], isOpponent: true, effects));
    }

    [Fact]
    public void Registry_has_no_problems_with_the_attack_family()
    {
        Assert.Empty(EffectRegistry.Default.Problems);
    }

    [Fact]
    public void Boards_full_of_attack_effects_simulate_without_errors()
    {
        // Every effect of the family on two 7-minion boards (both sides, normal and golden), default registry.
        var ours = Side(
            Minion(3, 6, cardId: "BG36_207", tribes: Tribe.Beast),
            Minion(2, 7, Keyword.Windfury, "BG20_104", Tribe.Quilboar),
            Minion(10, 11, cardId: "BG36_208", tribes: Tribe.Beast),
            Minion(1, 4, cardId: "BG36_200", tribes: Tribe.Beast, mechanics: CardMechanic.Rally),
            Minion(4, 6, cardId: "BG36_210", tribes: Tribe.Beast, mechanics: CardMechanic.Rally),
            Minion(6, 15, cardId: "BG36_333", tribes: Tribe.Quilboar, mechanics: CardMechanic.Rally),
            Minion(8, 9, Keyword.DivineShield, "BG36_241", Tribe.Dragon, golden: true)) with
        {
            Hand = [Minion(4, 4, cardId: "M", tribes: Tribe.Murloc)],
        };
        var theirs = Side(
            Minion(3, 4, Keyword.DivineShield | Keyword.Windfury, "BG25_016"),
            Minion(1, 10, Keyword.Venomous, "BG33_318", Tribe.Murloc),
            Minion(6, 3, cardId: "BGS_126", tribes: Tribe.Elemental, golden: true),
            Minion(4, 8, cardId: "BG36_102", tribes: Tribe.Aberration),
            Minion(3, 2, cardId: "BG26_817", tribes: Tribe.Pirate),
            Minion(14, 14, cardId: "BG27_017", tribes: Tribe.Dragon, golden: true),
            Minion(3, 5, cardId: "BG34_140", tribes: Tribe.Murloc));

        var odds = CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 300 });
        Assert.Equal(300, odds.Runs);
        Assert.Equal(1.0, odds.Win + odds.Tie + odds.Loss, 6);

        var swap = CombatSimulator.Run(theirs, ours, true, new SimOptions { Runs = 300 });
        Assert.Equal(1.0, swap.Win + swap.Tie + swap.Loss, 6);
    }
}
