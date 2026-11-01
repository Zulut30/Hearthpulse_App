using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Battlegrounds.Effects.Deaths;
using static Hearthpulse.Tests.Sim.SimTestKit;

namespace Hearthpulse.Tests.Sim.Deaths;

/// <summary>Dark Gifts that act in combat, and which gifts make a minion "not simulated".</summary>
public sealed class DarkGiftsTests
{
    private const string Offensive = "BG36_MidGameEffect_000te2";
    private const string Defensive = "BG36_MidGameEffect_000t2e2";
    private const string Horror = "BG36_MidGameEffect_000t12e";

    [Fact]
    public void Offensive_sacrifice_gives_its_attack_to_another_friendly_minion()
    {
        // Their 1/3 attacks first: the 5/1 dies and kills it; its Deathrattle gives the 0/10 +5 Attack.
        var gifted = Minion(5, 1, cardId: "G", mechanics: CardMechanic.Deathrattle) with { Enchantments = [Offensive] };
        var result = Fight(Side(gifted, Minion(0, 10, cardId: "B")), Side(Minion(1, 3)), oursFirst: false, Effects(typeof(OffensiveSacrifice)));

        var survivor = Assert.Single(result.Ours);
        Assert.Equal((5, 10), (survivor.Attack, survivor.Health));
    }

    [Fact]
    public void Defensive_sacrifice_gives_its_max_health_to_another_friendly_minion()
    {
        // Their 4/3 kills the 1/4 (taking 1), the 2/10 gets +4 Health, then trades with the 4/2: 2/14 - 4 = 2/10.
        var gifted = Minion(1, 4, cardId: "G", mechanics: CardMechanic.Deathrattle) with { Enchantments = [Defensive] };
        var result = Fight(Side(gifted, Minion(2, 10, cardId: "B")), Side(Minion(4, 3)), oursFirst: false, Effects(typeof(DefensiveSacrifice)));

        var survivor = Assert.Single(result.Ours);
        Assert.Equal((2, 10, 14), (survivor.Attack, survivor.Health, survivor.MaxHealth));
    }

    [Fact]
    public void Persisting_horror_is_reborn_with_full_stats_and_bonus_keywords()
    {
        // A 1/1 card buffed to 3/2 with a gained Taunt. Without the gift it would come back as a plain 1/1.
        var horror = Minion(1, 1, Keyword.Reborn | Keyword.Taunt, "H") with
        {
            Attack = 3, Health = 2, MaxHealth = 2, BaseKeywords = Keyword.Reborn, Enchantments = [Horror],
        };
        var result = Fight(Side(horror), Side(Minion(5, 20)), oursFirst: false, Effects(typeof(PersistingHorror)));

        Assert.Contains(result.Log, l => l.StartsWith("our H 3/2 Taunt attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Tavern_only_gifts_explain_a_granted_deathrattle_and_combat_gifts_without_an_effect_do_not()
    {
        var registry = EffectRegistry.Default;
        // Fresh Perspective: "Deathrattle: Gain 2 free Refreshes" only acts in the Tavern.
        Assert.False(CombatRelevance.IsUnsimulated("X", false, combatRelevant: false, grantedEffect: true,
            ["BG36_MidGameEffect_000t52e"], isOpponent: false, registry));
        // An unknown enchantment explains nothing.
        Assert.True(CombatRelevance.IsUnsimulated("X", false, combatRelevant: false, grantedEffect: true,
            ["BG36_UNKNOWN_e"], isOpponent: false, registry));
        // Invulnerability ("Immune while attacking") changes the fight and has no effect yet: flagged even without a marker.
        Assert.True(CombatRelevance.IsUnsimulated("X", false, combatRelevant: false, grantedEffect: false,
            ["BG36_MidGameEffect_000t60e"], isOpponent: false, registry));
        // The sacrifices are simulated now.
        Assert.False(CombatRelevance.IsUnsimulated("X", false, combatRelevant: false, grantedEffect: true,
            [Offensive, "BG36_MidGameEffect_000te3"], isOpponent: false, registry));
    }

    [Fact]
    public void Relevant_player_cards_without_an_effect_count_as_not_simulated()
    {
        // Tentacular (hero power) has no effect yet: the fight is an estimate. Ceremonial Sword (trinket) is simulated.
        var ours = Side(Minion(1, 1)) with { Extras = ["BG23_HERO_201p", "BG30_MagicItem_925"] };
        var odds = CombatSimulator.Run(ours, Side(Minion(1, 1)), true, new SimOptions { Runs = 10 });

        Assert.Equal(1, odds.Unsimulated);
        Assert.Equal(Origin.Player, CombatRelevance.Find("BG30_MagicItem_925")!.Origin);
    }
}
