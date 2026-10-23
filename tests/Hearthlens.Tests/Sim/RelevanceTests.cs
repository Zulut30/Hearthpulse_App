using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Cards;

namespace Hearthlens.Tests.Sim;

/// <summary>The "not simulated" flag: generated card table, fallback heuristic and the effect registry.</summary>
public sealed class RelevanceTests
{
    [Fact]
    public void Table_holds_the_hand_labelled_pool()
    {
        var pool = CombatRelevance.Table.Where(r => r.Origin == Origin.Pool).ToList();
        Assert.Equal(302, pool.Count);
        Assert.Equal(65, pool.Count(r => r.Relevance == Relevance.Relevant));
        Assert.All(pool, r => Assert.NotEqual(string.Empty, r.GoldenCardId));
        Assert.Equal("37.0.0.255130", CombatRelevance.Patch);
    }

    [Theory]
    [InlineData("BG31_803_G", "BG31_803", true)] // Buzzing Vermin
    [InlineData("TB_BaconUps_057", "BGS_008", true)] // Ghastcoiler
    [InlineData("BG_BOT_312t", "BG_BOT_312t", false)] // Microbot token
    [InlineData("TB_BaconUps_032t", "BG_BOT_312t", true)] // golden Microbot
    [InlineData("NOT_A_CARD", "NOT_A_CARD", false)]
    public void Golden_ids_map_to_normal_ids(string cardId, string normal, bool golden) =>
        Assert.Equal((normal, golden), CombatRelevance.Normalize(cardId));

    [Theory]
    [InlineData("BG36_854", false)] // Rescue Bot: deathrattle gives a card (flagged before)
    [InlineData("BG26_146", false)] // Lullabot: end of turn (TRIGGER_VISUAL-style false alarm)
    [InlineData("BG25_001", false)] // Risen Rider: keywords only
    [InlineData("BG24_500", false)] // Amber Guardian: start of combat, resolved at capture
    [InlineData("BG36_207", true)] // Wolf Pup: Rally (missed before)
    [InlineData("BG26_817", true)] // Blade Collector: cleave, no mechanic tag (missed before)
    [InlineData("BG25_354_G", true)] // golden Titus Rivendare
    [InlineData("BG19_010", true)] // Sewer Rat token: deathrattle
    public void Table_decides_known_cards(string cardId, bool relevant) =>
        Assert.Equal(relevant, CombatRelevance.IsCombatRelevant(cardId, card: null, "enUS"));

    [Theory]
    [InlineData("[x]<b>Deathrattle:</b> Summon two\n1/1 Skeletons.", true)]
    [InlineData("<b>Rally:</b> Give your minions +1/+1.", true)]
    [InlineData("After a friendly Beast attacks, gain +2/+2.", true)]
    [InlineData("<b>Avenge (3):</b> Summon a 3/3 Golem.", true)]
    [InlineData("Whenever this takes damage, give your Undead +1 Attack.", true)]
    [InlineData("<b>Battlecry:</b> Gain +2/+2.", false)]
    [InlineData("<b>Deathrattle:</b> Get a Blood Gem.", false)]
    [InlineData("At the end of your turn, give adjacent minions +1 Attack.", false)]
    [InlineData("<b>Rally:</b> Get a random Bounty.", false)]
    public void Unknown_english_cards_use_the_text_heuristic(string text, bool relevant)
    {
        var card = new Card { Id = "BG99_NEW", Text = text, Mechanics = ["DEATHRATTLE"] };
        Assert.Equal(relevant, CombatRelevance.IsCombatRelevant(card.Id, card, "enUS"));
    }

    [Fact]
    public void Unknown_cards_in_other_languages_fall_back_to_mechanics()
    {
        var rally = new Card { Id = "BG99_RALLY", Text = "<b>Adunata:</b> ottiene +2 Attacco.", Mechanics = ["BACON_RALLY"] };
        var battlecry = new Card { Id = "BG99_CRY", Text = "<b>Grido di battaglia:</b> ottiene +1/+1.", Mechanics = ["BATTLECRY"] };
        Assert.True(CombatRelevance.IsCombatRelevant(rally.Id, rally, "itIT"));
        Assert.False(CombatRelevance.IsCombatRelevant(battlecry.Id, battlecry, "itIT"));
    }

    [Fact]
    public void Not_simulated_means_relevant_without_an_effect()
    {
        var effects = EffectRegistry.FromTypes(typeof(FakeWolfPup), typeof(FakeGrantedDeathrattle), typeof(FakeHandMinion));
        Assert.True(CombatRelevance.IsUnsimulated("BG36_210", false, true, false, [], false, effects)); // no effect registered
        Assert.False(CombatRelevance.IsUnsimulated("BG36_207", false, true, false, [], false, effects)); // Wolf Pup implemented
        Assert.False(CombatRelevance.IsUnsimulated("BG36_207", true, true, false, [], true, effects)); // golden too
        Assert.False(CombatRelevance.IsUnsimulated("BG36_854", false, false, false, [], false, effects)); // tavern-only
        // A deathrattle granted by an enchantment counts unless the enchantment's effect is registered.
        Assert.True(CombatRelevance.IsUnsimulated("BG32_236", false, false, true, ["BG_OTHER_e"], false, effects));
        Assert.False(CombatRelevance.IsUnsimulated("BG32_236", false, false, true, ["BG_TEST_DR_e"], false, effects));
        // Effects that need a hand are simulated for the local player only.
        Assert.False(CombatRelevance.IsUnsimulated("BG34_140", false, true, false, [], false, effects));
        Assert.True(CombatRelevance.IsUnsimulated("BG34_140", false, true, false, [], true, effects));
    }

    [Fact]
    public void Registry_maps_golden_ids_and_reports_duplicates()
    {
        var effects = EffectRegistry.FromTypes(typeof(FakeWolfPup), typeof(GoldenVerminOnly), typeof(NormalVerminOnly), typeof(DuplicateWolfPup));
        Assert.IsType<GoldenVerminOnly>(effects.Get("BG31_803", golden: true));
        Assert.IsType<GoldenVerminOnly>(effects.Get("BG31_803_G", golden: false)); // a golden id implies golden
        Assert.IsType<NormalVerminOnly>(effects.Get("BG31_803", golden: false));
        Assert.Single(effects.Problems, p => p.Contains("BG36_207", StringComparison.Ordinal) && p.Contains("normal", StringComparison.Ordinal));
        Assert.Null(effects.Get("BG36_210", golden: false));
    }

    [Fact]
    public void Built_in_effects_register_cleanly()
    {
        // No card registered twice, no [CardEffect] on a class that is not a creatable CombatEffect.
        Assert.Empty(EffectRegistry.Default.Problems);
    }

    [CardEffect("BG36_207")]
    private sealed class FakeWolfPup : CombatEffect
    {
        public override void OnAttack(Fight fight, Unit self, Unit target)
        {
        }
    }

    [CardEffect("BG36_207")]
    private sealed class DuplicateWolfPup : CombatEffect
    {
    }

    [CardEffect("BG_TEST_DR_e")]
    private sealed class FakeGrantedDeathrattle : CombatEffect
    {
        public override void Deathrattle(Fight fight, Unit self)
        {
        }
    }

    [CardEffect("BG34_140")]
    private sealed class FakeHandMinion : CombatEffect
    {
        public override bool NeedsHand => true;
    }

    [CardEffect("BG31_803_G")]
    private sealed class GoldenVerminOnly : CombatEffect
    {
    }

    [CardEffect("BG31_803", Golden = GoldenFilter.NormalOnly)]
    private sealed class NormalVerminOnly : CombatEffect
    {
    }
}
