using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Battlegrounds.Effects.Player;
using static Hearthpulse.Tests.Sim.SimTestKit;

namespace Hearthpulse.Tests.Sim.Player;

/// <summary>Trinkets and hero powers that act in combat (Effects/player): one behaviour per card, from its text.</summary>
public sealed class PlayerEffectsTests
{
    private static SimSide With(SimSide side, params string[] extras) => side with { Extras = extras };

    private static SimMinion Survivor(FightResult result) => Assert.Single(result.Ours);

    [Fact]
    public void Every_relevant_player_card_with_an_effect_class_is_registered_cleanly()
    {
        Assert.Empty(EffectRegistry.Default.Problems);
        foreach (var id in new[] { "BG30_MagicItem_925", "BG30_MagicItem_880t", "BG32_MagicItem_860t", "TB_BaconShop_HP_107", "BG22_HERO_002p" })
        {
            Assert.True(EffectRegistry.Default.Implements(id, golden: false), id);
        }
    }

    [Fact]
    public void Player_card_table_has_one_row_per_card_and_none_shadows_the_minion_table()
    {
        var ids = PlayerCardTable.Entries.Select(r => r.CardId).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(CombatRelevance.Table, r => ids.Contains(r.CardId, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ceremonial_sword_gives_each_attacker_four_attack()
    {
        // 1/10 attacks twice into a 0/10: +4 each time, 5 then 9 damage.
        var result = Fight(With(Side(Minion(1, 10)), "BG30_MagicItem_925"), Side(Minion(0, 10)), true, Effects(typeof(CeremonialSword)));

        Assert.Equal(9, Survivor(result).Attack);
    }

    [Fact]
    public void Lionfish_portrait_buffs_attacking_beasts_only()
    {
        var beast = Fight(With(Side(Minion(1, 10, tribes: Tribe.Beast)), "BG36_MagicItem_201"), Side(Minion(0, 10)), true, Effects(typeof(LionfishPortrait)));
        var other = Fight(With(Side(Minion(5, 10)), "BG36_MagicItem_201"), Side(Minion(0, 10)), true, Effects(typeof(LionfishPortrait)));

        Assert.Equal((9, 14), (Survivor(beast).Attack, Survivor(beast).Health));
        Assert.Equal((5, 10), (Survivor(other).Attack, Survivor(other).Health));
    }

    [Fact]
    public void Faerie_dragon_scale_shields_an_attacking_dragon()
    {
        var result = Fight(With(Side(Minion(1, 10, tribes: Tribe.Dragon)), "BG32_MagicItem_363"), Side(Minion(1, 3)), true, Effects(typeof(FaerieDragonScale)));

        Assert.Contains(result.Log, l => l.StartsWith("our minion 1/10 loses Divine Shield", StringComparison.Ordinal));
    }

    [Fact]
    public void Feral_talisman_reaches_minions_summoned_in_combat()
    {
        // The reborn copy (printed 1/1) comes back 3/2 with the aura; golden 9/6.
        var normal = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "R")), "BG30_MagicItem_880"), Side(Minion(1, 10)), false, Effects(typeof(FeralTalisman)));
        var golden = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "R")), "BG30_MagicItem_880t"), Side(Minion(1, 20)), false, Effects(typeof(FeralTalisman)));

        Assert.Contains(normal.Log, l => l.StartsWith("our R 3/2 attacks", StringComparison.Ordinal));
        Assert.Contains(golden.Log, l => l.StartsWith("our R 9/6 attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Reinforced_shield_shields_summoned_minions()
    {
        var result = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "R")), "BG30_MagicItem_886"), Side(Minion(1, 10)), false, Effects(typeof(ReinforcedShield)));

        Assert.Contains(result.Log, l => l.StartsWith("our R 1/1 DivineShield attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Slamma_sticker_doubles_a_summoned_beasts_attack()
    {
        var result = Fight(With(Side(Minion(3, 1, Keyword.Reborn, "R", Tribe.Beast)), "BG30_MagicItem_540"), Side(Minion(1, 20)), false, Effects(typeof(SlammaSticker)));

        Assert.Contains(result.Log, l => l.StartsWith("our R 6/1 attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Bassgill_portrait_shields_a_summoned_murloc()
    {
        var result = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "R", Tribe.Murloc)), "BG32_MagicItem_301"), Side(Minion(1, 10)), false, Effects(typeof(BassgillPortrait)));

        Assert.Contains(result.Log, l => l.StartsWith("our R 1/1 DivineShield attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Blingtrons_sunglasses_shield_a_mech_after_a_mech_is_summoned()
    {
        var result = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "R", Tribe.Mech)), "BG30_MagicItem_978"), Side(Minion(1, 10)), false, Effects(typeof(BlingtronsSunglasses)));

        Assert.Contains(result.Log, l => l.StartsWith("our R 1/1 DivineShield attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Mechagon_adapter_gives_a_mech_its_shield_back_three_times()
    {
        var result = Fight(With(Side(Minion(0, 1, Keyword.DivineShield, tribes: Tribe.Mech)), "BG30_MagicItem_910"), Side(Minion(1, 10)), false, Effects(typeof(MechagonAdapter)));

        Assert.Equal(4, result.Log.Count(l => l.Contains("loses Divine Shield", StringComparison.Ordinal)));
    }

    [Fact]
    public void Alliance_keychain_passes_the_first_dead_minions_stats_on_once()
    {
        // The 4/1 dies first (their 1/1 attacks it): the 0/10 gets +4/+1. The second death passes nothing.
        var result = Fight(With(Side(Minion(4, 1, cardId: "A"), Minion(0, 10, cardId: "B")), "BG30_MagicItem_433"), Side(Minion(1, 1)), false,
            Effects(typeof(AllianceKeychain)));

        Assert.Equal((4, 11), (Survivor(result).Attack, Survivor(result).Health));
    }

    [Fact]
    public void Deathwhisper_sticker_buffs_the_board_after_a_reborn()
    {
        var result = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "R"), Minion(0, 10, cardId: "B")), "BG36_MagicItem_205"), Side(Minion(1, 1)), false,
            Effects(typeof(DeathwhisperSticker)));

        Assert.Contains(result.Ours, m => m.CardId == "B" && m.Attack == 2 && m.Health == 12);
    }

    [Fact]
    public void Deathtouch_apple_gives_a_reborn_undead_reborn_again()
    {
        var result = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "U", Tribe.Undead)), "BG35_MagicItem_731"), Side(Minion(1, 50)), false, Effects(typeof(DeathtouchApple)));

        // Captured once plus three apples: four reborn copies.
        Assert.Equal(4, result.Log.Count(l => l.StartsWith("reborn our U", StringComparison.Ordinal)));
    }

    [Fact]
    public void Unholy_sanctum_buffs_the_right_most_minion_after_a_deathrattle()
    {
        // Their 1/1 trades with Harmless Bonehead; its Skeletons land in its slot, so the 0/10 stays right-most and gets +2/+2.
        var rattle = Minion(1, 1, cardId: "BG28_300", mechanics: CardMechanic.Deathrattle);
        var result = Fight(With(Side(rattle, Minion(0, 10, cardId: "B")), "BG32_MagicItem_862"), Side(Minion(1, 1)), false,
            Effects(typeof(UnholySanctum), typeof(Hearthpulse.Core.Battlegrounds.Effects.Deaths.HarmlessBonehead)));

        Assert.Contains(result.Ours, m => m.CardId == "B" && (m.Attack, m.Health) == (2, 12));
    }

    [Fact]
    public void Deathly_phylactery_triggers_the_first_deathrattle_twice()
    {
        var rattle = Minion(1, 1, cardId: "BG28_300", mechanics: CardMechanic.Deathrattle);
        var result = Fight(With(Side(rattle), "BG30_MagicItem_700"), Side(Minion(1, 50)), false, Effects(typeof(DeathlyPhylactery), typeof(Hearthpulse.Core.Battlegrounds.Effects.Deaths.HarmlessBonehead)));

        // Two Skeletons per trigger: four with the extra trigger.
        Assert.Equal(4, result.Log.Count(l => l.StartsWith("summoned our BG_ICC_026t", StringComparison.Ordinal)));
    }

    [Fact]
    public void Sthara_sticker_brings_back_the_first_demon_after_the_last_minion_dies()
    {
        var demon = Minion(2, 1, cardId: "D", tribes: Tribe.Demon) with { Attack = 6, MaxHealth = 5, Health = 1 };
        var result = Fight(With(Side(demon), "BG32_MagicItem_907"), Side(Minion(1, 50)), false, Effects(typeof(STharaSticker)));

        Assert.Contains(result.Log, l => l.StartsWith("summoned our D 6/5", StringComparison.Ordinal));
        Assert.Single(result.Log, l => l.StartsWith("summoned our D", StringComparison.Ordinal));
    }

    [Fact]
    public void Bird_feeder_buffs_the_board_every_two_deaths()
    {
        var result = Fight(With(Side(Minion(1, 1, cardId: "A"), Minion(1, 1, cardId: "B"), Minion(0, 10, cardId: "C")), "BG32_MagicItem_864t"),
            Side(Minion(1, 50)), false, Effects(typeof(BirdFeeder)));

        Assert.Contains(result.Log, l => l.Contains("our C 4/14", StringComparison.Ordinal));
    }

    [Fact]
    public void Gilnean_thorned_rose_buffs_then_deals_one_damage_after_three_deaths()
    {
        var result = Fight(With(Side(Minion(1, 1, cardId: "A"), Minion(1, 1, cardId: "B"), Minion(1, 1, cardId: "C"), Minion(0, 10, cardId: "D")), "BG30_MagicItem_864"),
            Side(Minion(1, 50)), false, Effects(typeof(GilneanThornedRose)));

        Assert.Contains(result.Log, l => l.Contains("our D 4/14 takes 1", StringComparison.Ordinal) || l.Contains("our D 4/15 takes 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Staff_of_the_scourge_gives_an_undead_reborn_after_five_deaths()
    {
        var side = Side(Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(0, 1, cardId: "U", tribes: Tribe.Undead));
        var result = Fight(With(side, "BG30_MagicItem_437"), Side(Minion(1, 50)), false, Effects(typeof(StaffOfTheScourge)));

        Assert.Contains(result.Log, l => l.StartsWith("reborn our U", StringComparison.Ordinal));
    }

    [Fact]
    public void Beetle_band_summons_taunt_beetles()
    {
        var side = Side(Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(0, 30));
        var normal = Fight(With(side, "BG32_MagicItem_860"), Side(Minion(1, 200)), false, Effects(typeof(BeetleBand)));

        Assert.Contains(normal.Log, l => l.StartsWith("summoned our BG28_603t", StringComparison.Ordinal));
        Assert.Contains(normal.Log, l => l.StartsWith("our BG28_603t 2/2 Taunt attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Cloud_serpent_horn_gives_the_right_most_attack_to_another_dragon()
    {
        var side = Side(Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(0, 10, cardId: "D", tribes: Tribe.Dragon), Minion(7, 50, cardId: "R"));
        var result = Fight(With(side, "BG35_MagicItem_849"), Side(Minion(1, 500, Keyword.Taunt)), false, Effects(typeof(CloudSerpentHorn)));

        Assert.Contains(result.Log, l => l.Contains("our D 7/", StringComparison.Ordinal));
    }

    [Fact]
    public void Sprout_it_out_gives_summoned_minions_stats_and_taunt()
    {
        var result = Fight(With(Side(Minion(1, 1, Keyword.Reborn, "R")), "TB_BaconShop_HP_107"), Side(Minion(1, 10)), false, Effects(typeof(SproutItOut)));

        Assert.Contains(result.Log, l => l.StartsWith("our R 2/3 Taunt attacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Glory_of_combat_gives_a_killer_one_attack_once_per_kill()
    {
        var result = Fight(With(Side(Minion(3, 10)), "BG20_HERO_100p"), Side(Minion(0, 1), Minion(0, 1)), true, Effects(typeof(GloryOfCombat)));

        Assert.Equal(5, Survivor(result).Attack);
    }

    [Fact]
    public void Space_copy_hero_powers_copy_the_best_minion_once_room_opens_from_turn_seven()
    {
        SimSide Full(int turn) => With(Side(Minion(1, 1, cardId: "A"), Minion(2, 9, cardId: "H"), Minion(8, 2, cardId: "K"),
            Minion(1, 1), Minion(1, 1), Minion(1, 1), Minion(1, 1)) with { Turn = turn }, "BG22_HERO_003p");

        var unlocked = Fight(Full(7), Side(Minion(1, 1)), false, Effects(typeof(SpaceCopyHeroPower)));
        var locked = Fight(Full(6), Side(Minion(1, 1)), false, Effects(typeof(SpaceCopyHeroPower)));
        var attack = Fight(Full(9) with { Extras = ["BG22_HERO_002p"] }, Side(Minion(1, 1)), false, Effects(typeof(SpaceCopyHeroPower)));

        Assert.Single(unlocked.Log, l => l.StartsWith("summoned our H 2/9", StringComparison.Ordinal));
        Assert.DoesNotContain(locked.Log, l => l.StartsWith("summoned", StringComparison.Ordinal));
        Assert.Single(attack.Log, l => l.StartsWith("summoned our K 8/2", StringComparison.Ordinal));
    }
}
