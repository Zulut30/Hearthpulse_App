using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>Every card of the deaths family is registered (normal and golden) and the effects run in full simulations.</summary>
public sealed class DeathsFamilyRegistrationTests
{
    private static readonly string[] Cards =
    [
        Ids.Vermin, Ids.CordPuller, Ids.Bonehead, Ids.Underrot, Ids.Caretaker, Ids.Handless, Ids.Rover, Ids.Ghastcoiler, Ids.SewerLord,
        Ids.Skitterer, Ids.Salvager, Ids.ScarletSkull, Ids.Lobster, Ids.Plaguerunner, Ids.Goldrinn, Ids.Eudora, Ids.Cyclist, Ids.Mummifier,
        Ids.Nadina, Ids.Leeroy, Ids.Titus, Ids.Scorpid, Ids.SewerRat,
    ];

    public static TheoryData<string> FamilyCards => new(Cards);

    [Theory]
    [MemberData(nameof(FamilyCards))]
    public void Family_card_is_simulated_for_normal_and_golden_on_both_sides(string cardId)
    {
        var registry = EffectRegistry.Default;
        var row = CombatRelevance.Find(cardId);
        Assert.NotNull(row);
        Assert.Equal(Relevance.Relevant, row.Relevance);

        var effect = registry.Get(cardId, golden: false);
        Assert.NotNull(effect);
        Assert.Equal("Hearthlens.Core.Battlegrounds.Effects.Deaths", effect.GetType().Namespace);
        Assert.Same(effect, registry.Get(cardId, golden: true));
        Assert.Same(effect, registry.Get(row.GoldenCardId, golden: false)); // the golden card id resolves to the same effect
        Assert.False(effect.NeedsHand);

        foreach (var (golden, opponent) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            Assert.False(CombatRelevance.IsUnsimulated(cardId, golden, combatRelevant: true, grantedEffect: false, [], opponent, registry));
        }
    }

    [Fact]
    public void Family_has_twenty_three_cards() => Assert.Equal(23, Cards.Distinct().Count());

    [Fact]
    public void Deathrattle_boards_simulate_without_errors()
    {
        // Two full boards of deathrattle cards (normal and golden) through the default registry, with odds that add up.
        var ours = Side(
            Minion(2, 2, Keyword.Taunt, Ids.Vermin, Tribe.Beast, golden: true) with { ScriptData1 = 4, ScriptData2 = 4 },
            Minion(5, 5, cardId: Ids.Skitterer, tribes: Tribe.Beast),
            Minion(6, 7, cardId: Ids.Scorpid, tribes: Tribe.Beast),
            Minion(7, 7, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast),
            Minion(1, 7, cardId: Ids.Titus),
            Minion(6, 2, cardId: Ids.Leeroy),
            Minion(16, 4, cardId: Ids.Salvager, tribes: Tribe.Undead) with { Remembered = [Minion(4, 6, cardId: Ids.SewerLord, tribes: Tribe.Beast)] });
        var theirs = Side(
            Minion(2, 2, cardId: Ids.Bonehead, tribes: Tribe.Undead, golden: true),
            Minion(2, 1, Keyword.Reborn, Ids.ScarletSkull, Tribe.Undead),
            Minion(8, 12, cardId: Ids.SewerLord, tribes: Tribe.Beast, golden: true),
            Minion(7, 7, cardId: Ids.Goldrinn, tribes: Tribe.Beast),
            Minion(16, 6, cardId: Ids.Eudora, tribes: Tribe.Pirate, golden: true),
            Minion(8, 4, cardId: Ids.Nadina),
            Minion(5, 2, cardId: Ids.Mummifier, tribes: Tribe.Undead));

        var odds = CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 300 });

        Assert.Equal(300, odds.Runs);
        Assert.Equal(1.0, odds.Win + odds.Tie + odds.Loss, 6);
        Assert.Equal(0, odds.Unsimulated);
    }
}
