using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects;
using Hearthlens.Core.Battlegrounds.Effects.Deaths;
using static Hearthlens.Tests.Sim.Deaths.DeathsTestKit;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Deaths;

/// <summary>
/// Where summons from a dead minion land (several deathrattle summons, the reborn copy, multipliers) and a seeded
/// smoke test over random boards full of deathrattle cards.
/// </summary>
public sealed class DeathOrderAndStressTests
{
    [Fact]
    public void Deathrattle_summons_and_the_reborn_copy_keep_left_to_right_order_at_the_right_edge()
    {
        // Their Killer 1/1 attacks our only minion, a 1/1 Harmless Bonehead with Reborn (e.g. from a Mummifier), and both
        // die. Nothing alive stood to the Bonehead's right, so its slot is the end of the board:
        // deathrattle 1: Skeleton, deathrattle 2: Skeleton, then the reborn copy (printed 1/1, no Reborn) last.
        var bonehead = Minion(1, 1, Keyword.Reborn, Ids.Bonehead, Tribe.Undead);
        var result = Fight(Side(bonehead), Side(Killer(1, 1), Wall), false, Only(typeof(HarmlessBonehead)));

        Assert.Equal(["BG_ICC_026t 1/1", "BG_ICC_026t 1/1", "BG28_300 1/1"], Board(result.Ours));
        Assert.True(result.IndexOf("summoned our BG_ICC_026t 1/1 at 0") < result.IndexOf("reborn our BG28_300 1/1 at 2"));
    }

    [Fact]
    public void Summons_of_a_death_in_the_middle_stay_between_its_neighbours_in_order()
    {
        // Board L, Caretaker, R. The Killer trades with the Caretaker (pick 1 of 3): three Skeletons appear in its slot,
        // L and R keep their places.
        var result = Fight(
            Side(Minion(0, 5, cardId: "L"), Minion(3, 3, cardId: Ids.Caretaker, tribes: Tribe.Undead), Minion(0, 5, cardId: "R")),
            Side(Killer(3, 3), Wall), false, Only(typeof(CadaverCaretaker)), 1);

        Assert.Equal(["L 0/5", .. Times(3, "BG_ICC_026t 1/1"), "R 0/5"], Board(result.Ours));
    }

    [Fact]
    public void Titus_makes_a_salvager_summon_its_copies_once_per_trigger()
    {
        // The Salvager (16/4) is killed by their 4/16 Killer (pick 0). Its Start of Combat destroyed A (3/3), so each of the
        // two triggers (Titus: 1 extra) summons a copy of A in the Salvager's slot, left of Titus.
        var salvager = Minion(16, 4, cardId: Ids.Salvager, tribes: Tribe.Undead) with { Remembered = [Minion(3, 3, cardId: "A")] };
        var result = Fight(Side(salvager, Minion(1, 7, cardId: Ids.Titus)), Side(Killer(4, 16), Wall), false,
                           Only(typeof(StitchedSalvager), typeof(TitusRivendare)), 0);

        Assert.Equal(["A 3/3", "A 3/3", "BG25_354 1/7"], Board(result.Ours));
    }

    [Fact]
    public void The_opponents_deathrattles_work_the_same_way()
    {
        // We attack first with a 3/3 into their only minion, a Cadaver Caretaker 3/3. Both die; their deathrattle puts
        // three Skeletons on their side (our 0-Attack Wall cannot hurt them).
        var result = Fight(Side(Minion(3, 3, cardId: "OURS"), Wall), Side(Minion(3, 3, cardId: Ids.Caretaker, tribes: Tribe.Undead)), true,
                           Only(typeof(CadaverCaretaker)), 0);

        Assert.Equal(Times(3, "BG_ICC_026t 1/1"), Board(result.Theirs));
    }

    // ------------------------------------------------------------------------------------------- deathrattles of living minions

    /// <summary>Test-only: at fight start, triggers the deathrattle of the minion to its right without it dying (like Deathstrider).</summary>
    [CardEffect("TRIGGER")]
    private sealed class TriggerRight : CombatEffect
    {
        public override void OnFightStart(Fight fight, Unit self)
        {
            if (fight.Neighbours(self).Right is { } right)
            {
                fight.TriggerDeathrattles(right, 1, withMultiplier: false);
            }
        }
    }

    [Fact]
    public void A_triggered_ghastcoiler_summons_to_its_right_in_draw_order()
    {
        // TRIGGER (0/5) fires the living Ghastcoiler's deathrattle at fight start: two draws, Bonehead then Cord Puller,
        // both to the Ghastcoiler's right, first draw first. The Ghastcoiler stays, their 0-Attack Wall hurts nobody.
        var result = Fight(Side(Minion(0, 5, cardId: "TRIGGER"), Minion(7, 7, cardId: Ids.Ghastcoiler, tribes: Tribe.Beast)), Side(Wall), true,
                           Only(typeof(Ghastcoiler), typeof(TriggerRight)), GhastcoilerPool.IndexOf(Ids.Bonehead), GhastcoilerPool.IndexOf(Ids.CordPuller));

        Assert.Equal(["TRIGGER 0/5", "BGS_008 7/7", "BG28_300 1/1", "BG29_611 1/1 DivineShield"], Board(result.Ours));
    }

    [Fact]
    public void A_triggered_golden_salvager_summons_its_copies_to_its_right_in_order()
    {
        // Same, for the golden Salvager that destroyed A (3/3) on its left and B (5/2 Divine Shield) on its right.
        var salvager = Minion(32, 8, cardId: Ids.Salvager, tribes: Tribe.Undead, golden: true)
                       with { Remembered = [Minion(3, 3, cardId: "A"), Minion(5, 2, Keyword.DivineShield, "B")] };
        var result = Fight(Side(Minion(0, 5, cardId: "TRIGGER"), salvager), Side(Wall), true, Only(typeof(StitchedSalvager), typeof(TriggerRight)));

        Assert.Equal(["TRIGGER 0/5", "BG31_999(G) 32/8", "A 3/3", "B 5/2 DivineShield"], Board(result.Ours));
    }

    // ------------------------------------------------------------------------------------------- smoke test

    /// <summary>Printed card, with its tribe, keywords and mechanics, for random boards.</summary>
    private static readonly SimMinion[] Cards = BuildCards();

    private static SimMinion[] BuildCards()
    {
        var cards = new List<SimMinion>(GhastcoilerPool.Minions)
        {
            Minion(1, 7, cardId: Ids.Titus),
            Minion(3, 2, cardId: Ids.SewerRat, tribes: Tribe.Beast, mechanics: CardMechanic.Deathrattle),
            Minion(2, 2, cardId: Ids.Beetle, tribes: Tribe.Beast),
            Minion(1, 1, cardId: Ids.Skeleton, tribes: Tribe.Undead),
            Minion(3, 3, cardId: "PLAIN"),
            Minion(2, 5, Keyword.Taunt | Keyword.DivineShield, "SHIELD", Tribe.Dragon),
            Minion(4, 1, Keyword.Poisonous | Keyword.Reborn, "POISON", Tribe.Undead),
            Minion(1, 1, Keyword.Venomous | Keyword.Windfury, "VENOM", Tribe.Naga),
        };
        return cards.ToArray();
    }

    private static SimMinion RandomMinion(Random random)
    {
        var card = Cards[random.Next(Cards.Length)];
        var golden = random.Next(4) == 0;
        var factor = golden ? 2 : 1;
        var buff = random.Next(3) == 0 ? random.Next(1, 6) : 0;
        var health = card.Health * factor + buff;
        var minion = card with
        {
            Attack = card.Attack * factor + buff,
            Health = health,
            MaxHealth = health,
            RebornAttack = card.RebornAttack * factor,
            Golden = golden,
        };

        // Some captured minions carry script data (what the card text shows) and the Salvager remembers a destroyed minion.
        if (random.Next(3) == 0)
        {
            minion = minion with { ScriptData1 = random.Next(1, 8), ScriptData2 = random.Next(1, 8) };
        }

        if (minion.CardId == Ids.Salvager)
        {
            minion = minion with { Remembered = [Cards[random.Next(Cards.Length)]] };
        }

        return minion;
    }

    private static SimSide RandomSide(Random random)
    {
        var count = random.Next(1, 8);
        return Side(Enumerable.Range(0, count).Select(_ => RandomMinion(random)).ToArray());
    }

    [Fact]
    public void Random_boards_of_deathrattle_cards_simulate_cleanly()
    {
        // 60 seeded random boards (up to 7 minions a side, normal and golden, buffed, with script data) go through the
        // default registry. Every fight must finish with at most 7 minions a side and live survivors, and the odds
        // must add up. The seed is fixed, so a failure reproduces.
        var random = new Random(20260417);
        for (var board = 0; board < 60; board++)
        {
            var ours = RandomSide(random);
            var theirs = RandomSide(random);

            var odds = CombatSimulator.Run(ours, theirs, null, new SimOptions { Runs = 40 });
            Assert.Equal(40, odds.Runs);
            Assert.Equal(1.0, odds.Win + odds.Tie + odds.Loss, 6);

            for (var run = 0; run < 3; run++)
            {
                var result = CombatSimulator.RunSingle(ours, theirs, null, new Random(board * 31 + run), trace: false);
                Assert.InRange(result.Ours.Count, 0, 7);
                Assert.InRange(result.Theirs.Count, 0, 7);
                Assert.All(result.Ours.Concat(result.Theirs), m => Assert.True(m.Health > 0 && m.Attack >= 0, $"{Describe(m)} on board {board}"));
            }
        }
    }
}
