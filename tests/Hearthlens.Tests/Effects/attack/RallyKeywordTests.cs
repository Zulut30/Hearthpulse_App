using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Battlegrounds.Effects.Attack;
using static Hearthlens.Tests.Sim.SimTestKit;

namespace Hearthlens.Tests.Sim.Attack;

/// <summary>
/// Rally keyword effects: Sin'dorei Straight Shot, Bile Spitter and Heroic Broodmother. Expected results are worked out by
/// hand from the card text; scripted picks are the Random.Next results in call order (then 0).
/// </summary>
public sealed class RallyKeywordTests
{
    // ------------------------------------------------------------------------------------------- Sin'dorei Straight Shot

    [Fact]
    public void Sindorei_straight_shot_removes_taunt_and_reborn_from_its_target()
    {
        // Ours: Straight Shot 3/4 Divine Shield Windfury. Theirs: T 1/7 Taunt Reborn, F 1/1. Picks: [0, 1].
        // Swing 1: must hit the Taunt T (pick 0); the Rally strips T's Taunt and Reborn; T → 1/4, our shield pops.
        // Swing 2: no Taunt left, so F can be chosen (pick 1 of 2): F dies, ours → 3/3.
        // Their T hits us: ours → 3/2, T → 1/1. Our next attack kills T, which does not come back; ours → 3/1.
        var result = Fight(
            Side(Minion(3, 4, Keyword.DivineShield | Keyword.Windfury, "BG25_016")),
            Side(Minion(1, 7, Keyword.Taunt | Keyword.Reborn, "T"), Minion(1, 1, cardId: "F")),
            true, Effects(typeof(SindoreiStraightShot)), picks: [0, 1]);

        Assert.Equal(1, result.Outcome);
        Assert.Contains("our BG25_016 3/4 Windfury attacks their F 1/1", result.Log);
        Assert.Empty(result.Lines("reborn their"));
        var shot = Assert.Single(result.Ours);
        Assert.Equal((3, 1, false), (shot.Attack, shot.Health, shot.DivineShield));
    }

    [Fact]
    public void Golden_sindorei_straight_shot_has_the_same_rally()
    {
        // Golden 6/8 Divine Shield Windfury vs T 1/14 Taunt Reborn, F 1/1; picks [0, 1].
        // Swing 1: T → 1/8 (Taunt and Reborn removed), shield pops. Swing 2: F dies, ours → 6/7.
        // T hits us: ours → 6/6, T → 1/2. Our attack kills T for good; ours → 6/5.
        var result = Fight(
            Side(Minion(6, 8, Keyword.DivineShield | Keyword.Windfury, "BG25_016", golden: true)),
            Side(Minion(1, 14, Keyword.Taunt | Keyword.Reborn, "T"), Minion(1, 1, cardId: "F")),
            true, Effects(typeof(SindoreiStraightShot)), picks: [0, 1]);

        Assert.Equal(1, result.Outcome);
        Assert.Empty(result.Lines("reborn their"));
        var shot = Assert.Single(result.Ours);
        Assert.Equal((6, 5), (shot.Attack, shot.Health));
    }

    // ------------------------------------------------------------------------------------------- Bile Spitter

    [Fact]
    public void Bile_spitter_gives_another_friendly_murloc_venomous()
    {
        // Ours: Spitter 1/10 Venomous Murloc, M 1/1 Murloc, N 1/1 (no type). Theirs: E1 0/20, E2 0/20.
        // Spitter attacks E1 (pick 0): the Rally gives M (the only other Murloc) Venomous; Spitter's venom kills E1.
        // M attacks E2 and kills it with its new Venomous. Without the Rally M would need 20 hits.
        var result = Fight(
            Side(Minion(1, 10, Keyword.Venomous, "BG33_318", Tribe.Murloc), Minion(1, 1, cardId: "M", tribes: Tribe.Murloc), Minion(1, 1, cardId: "N")),
            Side(Minion(0, 20, cardId: "E1"), Minion(0, 20, cardId: "E2")),
            true, Effects(typeof(BileSpitter)));

        Assert.Equal(1, result.Outcome);
        Assert.Contains("our M 1/1 Venomous attacks their E2 0/20", result.Log);
        Assert.Equal(2, result.Log.Count(l => l.Contains(" attacks ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Golden_bile_spitter_gives_two_other_murlocs_venomous()
    {
        // Golden Spitter 2/20 Venomous, M1 1/1 and M2 1/1 Murlocs, N 1/1 vs three 0/20s. The Rally gives both M1 and M2
        // Venomous; each of the three attacks kills an enemy.
        var result = Fight(
            Side(Minion(2, 20, Keyword.Venomous, "BG33_318", Tribe.Murloc, golden: true),
                 Minion(1, 1, cardId: "M1", tribes: Tribe.Murloc), Minion(1, 1, cardId: "M2", tribes: Tribe.Murloc), Minion(1, 1, cardId: "N")),
            Side(Minion(0, 20, cardId: "E1"), Minion(0, 20, cardId: "E2"), Minion(0, 20, cardId: "E3")),
            true, Effects(typeof(BileSpitter)));

        Assert.Equal(1, result.Outcome);
        Assert.Equal(3, result.Log.Count(l => l.Contains(" attacks ", StringComparison.Ordinal)));
        Assert.Contains("our M1 1/1 Venomous attacks their E2 0/20", result.Log);
        Assert.Contains("our M2 1/1 Venomous attacks their E3 0/20", result.Log);
    }

    // ------------------------------------------------------------------------------------------- Heroic Broodmother

    [Fact]
    public void Heroic_broodmother_gains_divine_shield_whenever_it_attacks()
    {
        // Broodmother 7/7 (no shield left after start of combat) vs 3/20.
        // It attacks: gains a shield, which absorbs the 3; 20→13. The 3/20 attacks it (no shield now): 7/4, 13→6.
        // It attacks again: new shield absorbs the 3, kills. Ends 7/4. Without the Rally it would die (7 - 9 < 0).
        var result = Fight(Side(Minion(7, 7, cardId: "BG36_849", tribes: Tribe.Dragon)), Side(Minion(3, 20, cardId: "E")), true,
                           Effects(typeof(HeroicBroodmother)));

        Assert.Equal(1, result.Outcome);
        var mother = Assert.Single(result.Ours);
        Assert.Equal((7, 4, false), (mother.Attack, mother.Health, mother.DivineShield));
    }

    [Fact]
    public void Golden_heroic_broodmother_has_the_same_rally()
    {
        // Golden 14/14 vs 7/40: attack (shield absorbs 7; 40→26), their attack (14/7; 26→12), attack (shield; kills). Ends 14/7.
        var result = Fight(Side(Minion(14, 14, cardId: "BG36_849", tribes: Tribe.Dragon, golden: true)), Side(Minion(7, 40, cardId: "E")), true,
                           Effects(typeof(HeroicBroodmother)));

        Assert.Equal(1, result.Outcome);
        var mother = Assert.Single(result.Ours);
        Assert.Equal((14, 7), (mother.Attack, mother.Health));
    }
}
