using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Deaths;
using static Hearthlens.Tests.Sim.Deaths.DeathsTestKit;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>
/// The two hard summoners: Ghastcoiler (random Deathrattle minions from the public pool list) and Stitched Salvager
/// (exact copies of the minions its Start of Combat destroyed). Unless noted, their Killer attacks first and trades with
/// our left-most minion (pick 0).
/// </summary>
public sealed class SummonCopyTests
{
    private static int Pool(string cardId)
    {
        var index = GhastcoilerPool.IndexOf(cardId);
        Assert.True(index >= 0, cardId);
        return index;
    }

    // ------------------------------------------------------------------------------------------- Ghastcoiler

    [Fact]
    public void Ghastcoiler_summons_two_random_deathrattle_minions()
    {
        // Picks: 0 (Killer → Ghastcoiler), then one pool draw per summon: Harmless Bonehead, Cord Puller, with their printed
        // stats and keywords, left to right in the Ghastcoiler's slot.
        var result = Fight(Side(Minion(7, 7, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast)), Side(Killer(7, 7), Wall), false,
                           Only(typeof(Ghastcoiler)), 0, Pool(Ids.Bonehead), Pool(Ids.CordPuller));

        Assert.Equal(["BG28_300 1/1", "BG29_611 1/1 DivineShield"], Board(result.Ours));
    }

    [Fact]
    public void Golden_ghastcoiler_summons_four_normal_deathrattle_minions()
    {
        // Golden text: "Summon 4 random Deathrattle minions" (normal versions).
        var result = Fight(Side(Minion(14, 14, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast, golden: true)), Side(Killer(14, 14), Wall), false,
                           Only(typeof(Ghastcoiler)), 0, Pool(Ids.Bonehead), Pool(Ids.CordPuller), Pool(Ids.Vermin), Pool(Ids.Goldrinn));

        Assert.Equal(["BG28_300 1/1", "BG29_611 1/1 DivineShield", "BG31_803 1/1 Taunt", "BGS_018 7/7"], Board(result.Ours));
    }

    [Fact]
    public void Ghastcoiler_reads_its_count_from_script_data()
    {
        // The text's {0} is TAG_SCRIPT_DATA_NUM_1: a captured 3 summons three.
        var ghastcoiler = Minion(7, 7, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast) with { ScriptData1 = 3 };
        var result = Fight(Side(ghastcoiler), Side(Killer(7, 7), Wall), false, Only(typeof(Ghastcoiler)),
                           0, Pool(Ids.Bonehead), Pool(Ids.Bonehead), Pool(Ids.Caretaker));

        Assert.Equal(["BG28_300 1/1", "BG28_300 1/1", "BG30_125 3/3"], Board(result.Ours));
    }

    [Fact]
    public void Ghastcoiler_stops_drawing_once_the_board_is_full()
    {
        // Six 0/5 fillers stay: room for one summon. The second draw is not made (no "no room" line).
        var fillers = Enumerable.Range(1, 6).Select(i => Minion(0, 5, cardId: "F" + i)).ToArray();
        var result = Fight(Side([Minion(7, 7, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast), .. fillers]), Side(Killer(7, 7), Wall), false,
                           Only(typeof(Ghastcoiler)), 0, Pool(Ids.Bonehead), Pool(Ids.CordPuller));

        Assert.Equal(["BG28_300 1/1", .. fillers.Select(Describe)], Board(result.Ours));
        Assert.DoesNotContain(result.Log, l => l.StartsWith("no room", StringComparison.Ordinal));
    }

    [Fact]
    public void Ghastcoilers_summons_use_their_own_registered_deathrattles()
    {
        // 1. Their K1 7/7 → Ghastcoiler: both die; two Boneheads (draws).
        // 2. Our first Bonehead attacks K2 1/1 (pick 0): both die; its own deathrattle summons two Skeletons.
        var result = Fight(Side(Minion(7, 7, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast)), Side(Killer(7, 7), Killer(1, 1), Wall), false,
                           Only(typeof(Ghastcoiler), typeof(HarmlessBonehead)), 0, Pool(Ids.Bonehead), Pool(Ids.Bonehead), 0);

        Assert.Equal(["BG_ICC_026t 1/1", "BG_ICC_026t 1/1", "BG28_300 1/1"], Board(result.Ours));
    }

    [Fact]
    public void Ghastcoiler_pool_holds_the_public_deathrattle_minions()
    {
        var pool = GhastcoilerPool.Minions;
        Assert.Equal(GhastcoilerPool.Patch, CombatRelevance.Patch);
        Assert.Equal(45, pool.Count);
        Assert.Equal(pool.Count, pool.Select(m => m.CardId).Distinct().Count());
        Assert.DoesNotContain(pool, m => m.CardId == Ids.Ghastcoiler);
        Assert.All(pool, m =>
        {
            Assert.True((m.Mechanics & CardMechanic.Deathrattle) != 0, m.CardId);
            Assert.False(m.Golden);
            Assert.Equal((0, 0), (m.ScriptData1, m.ScriptData2));
            Assert.Equal(Origin.Pool, CombatRelevance.Find(m.CardId)?.Origin);
            Assert.DoesNotContain("DUO", m.CardId, StringComparison.Ordinal);
        });
        // Spot checks against the card data: Sewer Lord 4/6 Beast, Scarlet Skull 2/1 Undead with Reborn.
        var lord = pool[Pool(Ids.SewerLord)];
        Assert.Equal((4, 6, Tribe.Beast), (lord.Attack, lord.Health, lord.Tribes));
        Assert.Equal(Keyword.Reborn, pool[Pool(Ids.ScarletSkull)].BaseKeywords);
    }

    // ------------------------------------------------------------------------------------------- Stitched Salvager

    private static SimMinion Salvager(bool golden, params SimMinion[] remembered) =>
        (golden ? Minion(32, 8, cardId: Ids.Salvager, tribes: Tribe.Undead, golden: true) : Minion(16, 4, cardId: Ids.Salvager, tribes: Tribe.Undead))
        with { Remembered = remembered };

    [Fact]
    public void Salvager_summons_an_exact_copy_of_the_minion_it_destroyed()
    {
        // Start of Combat destroyed a buffed Risen Rider (printed 2/1 Taunt Reborn, now 7/3). Killer 4/16 trades with the
        // Salvager; the deathrattle summons the Rider as it was: 7/3 Taunt Reborn.
        var rider = Minion(2, 1, Keyword.Taunt | Keyword.Reborn, "BG25_001", Tribe.Undead) with { Attack = 7, Health = 3, MaxHealth = 3, EntityId = 30 };
        var result = Fight(Side(Salvager(false, rider)), Side(Killer(4, 16), Wall), false, Only(typeof(StitchedSalvager)));

        Assert.Equal(["BG25_001 7/3 Taunt, Reborn"], Board(result.Ours));
        Assert.Equal(0, result.Ours[0].EntityId);
    }

    [Fact]
    public void Golden_salvager_summons_exact_copies_of_both_adjacent_minions()
    {
        // Golden text: "Destroy adjacent minions. Deathrattle: Summon exact copies of them." Both, in order.
        var result = Fight(Side(Salvager(true, Minion(3, 3, cardId: "A"), Minion(5, 2, Keyword.DivineShield, "B"))), Side(Killer(8, 32), Wall), false,
                           Only(typeof(StitchedSalvager)));

        Assert.Equal(["A 3/3", "B 5/2 DivineShield"], Board(result.Ours));
    }

    [Fact]
    public void Salvager_never_copies_a_stitched_salvager()
    {
        // "(Except Stitched Salvager.)": a golden Salvager that destroyed another Salvager and A copies only A.
        var result = Fight(Side(Salvager(true, Salvager(false), Minion(3, 3, cardId: "A"))), Side(Killer(8, 32), Wall), false,
                           Only(typeof(StitchedSalvager)));

        Assert.Equal(["A 3/3"], Board(result.Ours));
    }

    [Fact]
    public void Normal_salvager_copies_one_minion_at_most()
    {
        // The normal card destroys only the minion to its left: at most one copy even if more were remembered.
        var result = Fight(Side(Salvager(false, Minion(3, 3, cardId: "A"), Minion(5, 2, cardId: "B"))), Side(Killer(4, 16), Wall), false,
                           Only(typeof(StitchedSalvager)));

        Assert.Equal(["A 3/3"], Board(result.Ours));
    }

    [Fact]
    public void Salvager_without_remembered_minions_summons_nothing()
    {
        // A Salvager whose Start of Combat found nothing (or one made in combat): an empty deathrattle.
        var result = Fight(Side(Salvager(false)), Side(Killer(4, 16), Wall), false, Only(typeof(StitchedSalvager)));

        Assert.Empty(result.Ours);
        Assert.Equal(-1, result.Outcome);
    }

    [Fact]
    public void Salvager_copy_keeps_its_card_effect()
    {
        // The copy of a buffed 3/3 Harmless Bonehead is a Bonehead: when it dies (attacking their K2 3/3, pick 0) it
        // summons two Skeletons.
        var bonehead = Minion(1, 1, cardId: Ids.Bonehead, tribes: Tribe.Undead, mechanics: CardMechanic.Deathrattle) with { Attack = 3, Health = 3, MaxHealth = 3 };
        var result = Fight(Side(Salvager(false, bonehead)), Side(Killer(4, 16), Killer(3, 3), Wall), false,
                           Only(typeof(StitchedSalvager), typeof(HarmlessBonehead)));

        Assert.Contains("summoned our BG28_300 3/3 at 0", result.Log);
        Assert.Equal(Times(2, "BG_ICC_026t 1/1"), Board(result.Ours));
    }

    [Fact]
    public void Salvager_copy_of_a_minion_remembered_without_health_comes_back_at_full_health()
    {
        // Defensive: a remembered minion with Health 0 (maximum 4) is copied at 4 Health.
        var result = Fight(Side(Salvager(false, Minion(3, 4, cardId: "A") with { Health = 0 })), Side(Killer(4, 16), Wall), false,
                           Only(typeof(StitchedSalvager)));

        Assert.Equal(["A 3/4"], Board(result.Ours));
    }
}
