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

    [Theory]
    [InlineData("BG36_MagicItem_205", "our H 5/4 Taunt attacks")] // Deathwhisper Sticker: +2/+2 after a Reborn
    [InlineData("TB_BaconShop_HP_107", "our H 4/4 Taunt attacks")] // Sprout It Out!: +1/+2 and Taunt to summons
    public void Persisting_horror_keeps_what_the_reborn_copy_gets_when_summoned(string extra, string line)
    {
        var horror = Minion(1, 1, Keyword.Reborn | Keyword.Taunt, "H") with
        {
            Attack = 3, Health = 2, MaxHealth = 2, BaseKeywords = Keyword.Reborn, Enchantments = [Horror],
        };
        var result = Fight(Side(horror) with { Extras = [extra] }, Side(Minion(5, 20)), oursFirst: false);

        Assert.Contains(result.Log, l => l.StartsWith(line, StringComparison.Ordinal));
    }

    [Fact]
    public void Tavern_only_gifts_explain_only_their_own_granted_marker_and_combat_gifts_without_an_effect_do_not()
    {
        var registry = EffectRegistry.Default;
        const string charisma = "BG36_MidGameEffect_000t3e2"; // "Rally: Get a random minion…" (Tavern)
        const string freshPerspective = "BG36_MidGameEffect_000t52e"; // "Deathrattle: Gain 2 free Refreshes" (Tavern)
        bool Unsimulated(string[] granted, params string[] enchantments) =>
            CombatRelevance.IsUnsimulated("X", false, false, granted, enchantments, isOpponent: false, registry);

        Assert.False(Unsimulated(["DEATHRATTLE"], freshPerspective));
        Assert.False(Unsimulated(["BACON_RALLY"], charisma));
        // Charisma explains a Rally, not a deathrattle granted by something else (e.g. Sky Golem Portrait's enchantment).
        Assert.True(Unsimulated(["DEATHRATTLE"], charisma));
        Assert.True(Unsimulated(["BACON_RALLY", "DEATHRATTLE"], charisma, "BG35_MagicItem_740e"));
        // An unknown enchantment explains nothing.
        Assert.True(Unsimulated(["DEATHRATTLE"], "BG36_UNKNOWN_e"));
        // Invulnerability ("Immune while attacking") changes the fight: flagged without its effect even with no marker,
        // simulated with it.
        Assert.True(CombatRelevance.IsUnsimulated("X", false, false, [], ["BG36_MidGameEffect_000t60e"], isOpponent: false, EffectRegistry.Empty));
        Assert.False(Unsimulated([], "BG36_MidGameEffect_000t60e"));
        // The sacrifices are simulated: their deathrattle is explained.
        Assert.False(Unsimulated(["DEATHRATTLE"], Offensive, "BG36_MidGameEffect_000te3"));
    }

    [Fact]
    public void Relevant_player_cards_without_an_effect_count_as_not_simulated()
    {
        // Rapid Reanimation (hero power) has no effect yet: the fight is an estimate. Ceremonial Sword (trinket) is simulated.
        var ours = Side(Minion(1, 1)) with { Extras = ["BG25_HERO_103p", "BG30_MagicItem_925"] };
        var odds = CombatSimulator.Run(ours, Side(Minion(1, 1)), true, new SimOptions { Runs = 10 });

        Assert.Equal(1, odds.Unsimulated);
        Assert.Equal(Origin.Player, CombatRelevance.Find("BG30_MagicItem_925")!.Origin);
    }

    [Theory]
    // a player card no table knows (newer than the card data): its effect cannot be known
    [InlineData("BG99_MagicItem_999", 1)]
    // generated from the text: Boom Controller summons in combat and has no effect yet
    [InlineData("BG30_MagicItem_440", 1)]
    // generated: a Tavern-only hero power and a trinket whose Avenge only gives a card
    [InlineData("TB_BaconShop_HP_101", 0)]
    [InlineData("BG30_MagicItem_545", 0)]
    public void An_unknown_player_card_is_not_simulated_and_a_known_one_counts_by_its_row(string extra, int unsimulated)
    {
        var odds = CombatSimulator.Run(Side(Minion(1, 1)) with { Extras = [extra] }, Side(Minion(1, 1)), true, new SimOptions { Runs = 10 });

        Assert.Equal(unsimulated, odds.Unsimulated);
    }

    [Fact]
    public void A_hand_labelled_player_card_wins_over_its_generated_row()
    {
        // Bewitched Ribbon: the text heuristic misses "+2/+2 while in combat", the hand table says it acts in combat.
        Assert.Equal(Relevance.Relevant, CombatRelevance.Find("BG35_MagicItem_923")!.Relevance);
        Assert.DoesNotContain(PlayerCardGenerated.Entries, r => PlayerCardTable.Entries.Any(h => h.CardId == r.CardId));
    }
}
