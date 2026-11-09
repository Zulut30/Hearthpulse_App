using Hearthpulse.Core.Battlegrounds;
using static Hearthpulse.Tests.Sim.SimTestKit;

namespace Hearthpulse.Tests.Sim;

/// <summary>
/// Plan S1 candidates that are in the current pool (docs/bg-sim-matrix.md): one minimal fight each, from the card text.
/// None of them has a golden version. Tiger Carving is out of the pool (no code); Blood Golem Sticker lacks its input.
/// </summary>
public sealed class CandidateEffectsTests
{
    private const string Invulnerability = "BG36_MidGameEffect_000t60e";
    private const string Broodmother = "BG22_HERO_305p";
    private const string Tentacular = "BG23_HERO_201p";
    private const string FangAnklet = "BG35_MagicItem_701";
    private const string BloodGolemSticker = "BG30_MagicItem_442";

    private static readonly Dictionary<string, int> Growth21 = new() { ["TAG_SCRIPT_DATA_NUM_1"] = 2, ["TAG_SCRIPT_DATA_NUM_2"] = 1 };

    private static SimSide WithExtra(SimSide side, string cardId, Dictionary<string, int>? tags = null) => side with
    {
        Extras = [cardId],
        ExtraTags = tags is null
            ? new Dictionary<string, IReadOnlyDictionary<string, int>>()
            : new Dictionary<string, IReadOnlyDictionary<string, int>> { [cardId] = tags },
    };

    // ------------------------------------------------------------------------------------------- Invulnerability

    [Fact]
    public void Invulnerability_takes_no_damage_while_attacking_but_does_when_defending()
    {
        // Our 3/3 with the gift attacks their 2/20 Poisonous: immune, so it lives; their minion then attacks and kills it.
        var ours = Minion(3, 3, cardId: "TEST_GIFTED") with { Enchantments = [Invulnerability] };
        var result = Fight(Side(ours), Side(Minion(2, 20, Keyword.Poisonous, "TEST_POISON")), oursFirst: true);

        var immune = result.IndexOf("our TEST_GIFTED 3/3 is immune");
        var counter = result.IndexOf("their TEST_POISON 2/17 Poisonous attacks our TEST_GIFTED 3/3");
        Assert.True(immune >= 0, string.Join('\n', result.Log));
        Assert.True(counter > immune, string.Join('\n', result.Log));
        Assert.Contains(result.Log, l => l.StartsWith("our TEST_GIFTED", StringComparison.Ordinal) && l.Contains(" takes 2 from their TEST_POISON", StringComparison.Ordinal));
        Assert.Equal(-1, result.Outcome);
    }

    [Fact]
    public void Invulnerability_is_simulated_on_both_sides()
    {
        var gifted = Minion(3, 3, cardId: "BG36_207") with { Enchantments = [Invulnerability] };
        Assert.Empty(CombatSimulator.Explain(Side(gifted), isOpponent: true));
        Assert.Empty(CombatSimulator.Explain(Side(gifted), isOpponent: false));
    }

    // ------------------------------------------------------------------------------------------- Broodmother

    [Fact]
    public void Broodmother_summons_a_grown_whelp_on_the_fourth_death_and_improves_it()
    {
        // Four 1/1s die to their 1/100; the fourth death summons a (1+2)/(1+1) Whelp that attacks right away.
        var ours = WithExtra(Side(Minion(1, 1, cardId: "A"), Minion(1, 1, cardId: "B"), Minion(1, 1, cardId: "C"), Minion(1, 1, cardId: "D")),
                             Broodmother, Growth21);
        var result = Fight(ours, Side(Minion(1, 100, cardId: "WALL")), oursFirst: true);

        var whelps = result.Lines("summoned our BG22_HERO_305t");
        Assert.Equal("summoned our BG22_HERO_305t 3/2 at 0", whelps[0]);
        var attack = result.IndexOf("our BG22_HERO_305t 3/2 attacks");
        Assert.True(attack > result.IndexOf("summoned our BG22_HERO_305t 3/2"), string.Join('\n', result.Log));
    }

    [Fact]
    public void Broodmother_without_its_captured_value_is_missing_input_and_summons_nothing()
    {
        var ours = WithExtra(Side(Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(1, 1)), Broodmother);
        var item = Assert.Single(CombatSimulator.Explain(ours, isOpponent: false));
        Assert.Equal((Broodmother, UnsimulatedReason.MissingInput, "missing_input"), (item.CardId, item.Reason, item.Code));
        Assert.Empty(Fight(ours, Side(Minion(1, 100)), oursFirst: true).Lines("summoned our BG22_HERO_305t"));
        Assert.Equal(1, CombatSimulator.Run(ours, Side(Minion(1, 100)), true, new SimOptions { Runs = 10 }).Unsimulated);
        Assert.Empty(CombatSimulator.Explain(WithExtra(ours, Broodmother, Growth21), isOpponent: true));
    }

    // ------------------------------------------------------------------------------------------- Tentacular

    [Fact]
    public void Tentacular_owed_on_a_full_board_comes_after_the_first_death()
    {
        var board = Enumerable.Range(0, 7).Select(i => Minion(1, 1, cardId: $"M{i}")).ToArray();
        var ours = WithExtra(Side(board), Tentacular, new Dictionary<string, int> { ["TAG_SCRIPT_DATA_NUM_1"] = 1 });
        var result = Fight(ours, Side(Minion(1, 100, cardId: "WALL")), oursFirst: true);

        var tentacles = result.Lines("summoned our BG23_HERO_201pt");
        Assert.Equal("summoned our BG23_HERO_201pt 3/3 Taunt at 6", Assert.Single(tentacles));
        Assert.Empty(CombatSimulator.Explain(ours, isOpponent: false));
    }

    [Fact]
    public void Tentacular_needs_no_value_when_its_tentacle_is_already_on_the_board_or_there_is_space()
    {
        var board = Enumerable.Range(0, 6).Select(i => Minion(1, 1, cardId: $"M{i}"))
            .Append(Minion(2, 2, Keyword.Taunt, "BG23_HERO_201pt") with { CreatorCardId = Tentacular }).ToArray();
        var withTentacle = WithExtra(Side(board), Tentacular);
        Assert.Empty(CombatSimulator.Explain(withTentacle, isOpponent: true));
        Assert.Empty(Fight(withTentacle, Side(Minion(1, 100)), oursFirst: true).Lines("summoned our BG23_HERO_201pt"));

        Assert.Empty(CombatSimulator.Explain(WithExtra(Side(Minion(1, 1)), Tentacular), isOpponent: false));

        var full = WithExtra(Side(Enumerable.Range(0, 7).Select(_ => Minion(1, 1)).ToArray()), Tentacular);
        Assert.Equal(UnsimulatedReason.MissingInput, Assert.Single(CombatSimulator.Explain(full, isOpponent: true)).Reason);
    }

    // ------------------------------------------------------------------------------------------- Fang Anklet

    [Fact]
    public void Fang_anklet_buffs_beasts_summoned_in_combat_with_the_captured_value()
    {
        // Buzzing Vermin dies and summons a 2/2 Beetle: +(1+2)/+(1+1) from the anklet.
        var ours = WithExtra(Side(Minion(1, 1, Keyword.Taunt, "BG31_803", Tribe.Beast)), FangAnklet, Growth21);
        var result = Fight(ours, Side(Minion(5, 50, cardId: "WALL")), oursFirst: false);

        Assert.StartsWith("summoned our BG28_603t 5/4", Assert.Single(result.Lines("summoned our BG28_603t")), StringComparison.Ordinal);
    }

    [Fact]
    public void Fang_anklet_without_its_value_is_missing_input()
    {
        var item = Assert.Single(CombatSimulator.Explain(WithExtra(Side(Minion(1, 1)), FangAnklet), isOpponent: true));
        Assert.Equal(UnsimulatedReason.MissingInput, item.Reason);
        Assert.True(item.Extra);
    }

    // ------------------------------------------------------------------------------------------- Blood Golem Sticker

    [Fact]
    public void Blood_golem_sticker_is_missing_input_not_a_missing_effect()
    {
        var item = Assert.Single(CombatSimulator.Explain(WithExtra(Side(Minion(1, 1)), BloodGolemSticker), isOpponent: true));
        Assert.Equal(UnsimulatedReason.MissingInput, item.Reason);
    }
}
