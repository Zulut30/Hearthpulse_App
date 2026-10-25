using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Secrets;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>The Secrets helper: the rule table, the pools per format, and narrowing a Secret down by what happens.</summary>
public sealed class SecretsTests
{
    private static readonly string[] StandardMage = ["EX1_287", "EX1_289", "BAR_812", "LOOT_101", "END_024", "JAIL_315"];

    [Fact]
    public void Every_collectible_secret_has_a_rule_entry()
    {
        var secrets = SecretGame.Cards.All.Where(c => c.Collectible && c.Mechanics.Contains("SECRET")).ToList();
        Assert.True(secrets.Count > 100);
        var missing = secrets.Where(c => !SecretRules.Knows(c.Id)).Select(c => $"{c.Id} {c.Name}: {c.Text}").ToList();
        Assert.True(missing.Count == 0, "Secrets without a rule (add them to SecretRules):\n" + string.Join('\n', missing));
    }

    [Fact]
    public void Standard_pool_is_the_class_secrets_in_standard_sets_with_one_row_per_card()
    {
        var pool = new SecretPool(SecretGame.Cards);
        var mage = pool.For("MAGE", SecretFormat.Standard);
        Assert.Equal(StandardMage.Order(), mage.Select(o => o.Key).Order());
        // The Core reprint is the printing shown in Standard.
        Assert.Equal("CORE_EX1_287", mage.Single(o => o.Key == "EX1_287").CardId);
        Assert.Empty(pool.For("WARRIOR", SecretFormat.Standard));
    }

    [Fact]
    public void Wild_folds_reprints_and_classic_uses_only_classic_cards()
    {
        var pool = new SecretPool(SecretGame.Cards);
        var hunter = pool.For("HUNTER", SecretFormat.Wild);
        // Snipe exists as EX1_609, CORE-less Wonders reprint WON_018 and the Classic VAN_EX1_609: one row in Wild.
        Assert.Single(hunter, o => o.Name == "Snipe");
        Assert.DoesNotContain(hunter, o => o.CardId.StartsWith("VAN_", StringComparison.Ordinal));
        Assert.Equal(hunter.Count, hunter.Select(o => o.Key).Distinct().Count());
        var classic = pool.For("HUNTER", SecretFormat.Classic);
        Assert.All(classic, o => Assert.StartsWith("VAN_", o.CardId));
        Assert.Equal(pool.For("HUNTER", SecretFormat.Wild).Count, pool.For("HUNTER", SecretFormat.Arena).Count);
    }

    [Fact]
    public void A_played_secret_lists_its_class_pool_with_the_mana_spent()
    {
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", paid: 3);
        var view = game.View();
        Assert.NotNull(view);
        var slot = Assert.Single(view!.Slots);
        Assert.Equal(("MAGE", 3, 1), (slot.CardClass, slot.PaidCost, slot.Turn));
        Assert.Null(slot.KnownCardId);
        Assert.Equal(StandardMage.Order(), game.Open().Order());
        Assert.Equal(6, slot.Possible);
    }

    [Fact]
    public void Nothing_shows_without_a_secret_and_quests_or_objectives_are_not_secrets()
    {
        var game = new SecretGame();
        game.Turn(2);
        Assert.Null(game.View());
        // An objective or a quest sits in the Secret zone without SECRET=1.
        game.Feed(
            "DebugPrintPower() - FULL_ENTITY - Creating ID=70 CardID=BE_085",
            "DebugPrintPower() -     tag=ZONE value=SECRET",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL");
        Assert.Null(game.View());
    }

    [Fact]
    public void A_minion_attacking_the_hero_rules_out_hero_and_attack_secrets()
    {
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.Turn(1);
        var yeti = game.LocalCard("CS2_182", "MINION", "PLAY");
        game.Attack(yeti, SecretGame.OpponentHero, damage: 4);
        // Ice Barrier (hero attacked) and Mystic Misdirection (enemy minion attacks) would have fired.
        Assert.Equal(new[] { "BAR_812", "END_024", "EX1_287", "LOOT_101" }, game.Open().Order());
        var iceBarrier = game.View()!.Rows.Single(r => r.Key == "EX1_289");
        Assert.Equal(0, iceBarrier.Possible);
        Assert.False(iceBarrier.Crossed);
    }

    [Fact]
    public void Attacking_a_minion_rules_out_oasis_ally_unless_their_board_is_full()
    {
        var game = new SecretGame();
        game.Turn(2);
        var target = game.OpponentMinion();
        game.OpponentSecret(50, "MAGE", 3);
        game.Turn(1);
        var yeti = game.LocalCard("CS2_182", "MINION", "PLAY");
        game.Attack(yeti, target);
        Assert.DoesNotContain("BAR_812", game.Open());
        Assert.Contains("EX1_289", game.Open());

        var full = new SecretGame();
        full.Turn(2);
        var first = full.OpponentMinion();
        for (var i = 0; i < 6; i++)
        {
            full.OpponentMinion();
        }

        full.OpponentSecret(50, "MAGE", 3);
        full.Turn(1);
        var axe = full.LocalCard("CS2_182", "MINION", "PLAY");
        full.Attack(axe, first);
        // Oasis Ally summons: with seven minions it could not have fired.
        Assert.Contains("BAR_812", full.Open());
        Assert.DoesNotContain("JAIL_315", full.Open());
    }

    [Fact]
    public void A_spell_rules_out_counterspell_and_the_coin_counts()
    {
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.Turn(1);
        var coin = game.LocalCard("GAME_005", "SPELL");
        game.Play(coin, "GAME_005", cost: 0);
        Assert.DoesNotContain("EX1_287", game.Open());
        Assert.Contains("LOOT_101", game.Open());
    }

    [Fact]
    public void Playing_a_minion_rules_out_explosive_runes_but_not_for_an_elusive_minion()
    {
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.Turn(1);
        var elusive = game.LocalCard("CS2_182", "MINION", "HAND", "tag=CANT_BE_TARGETED_BY_SPELLS value=1");
        game.Play(elusive, "CS2_182", 4);
        Assert.Contains("LOOT_101", game.Open());
        var yeti = game.LocalCard("CS2_182", "MINION");
        game.Play(yeti, "CS2_182", 4);
        Assert.DoesNotContain("LOOT_101", game.Open());
    }

    [Fact]
    public void A_secret_that_fires_in_the_same_action_leaves_the_others_untouched()
    {
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.OpponentSecret(51, "MAGE", 3);
        game.Turn(1);
        var yeti = game.LocalCard("CS2_182", "MINION", "PLAY");
        // The first Secret was Ice Barrier: it fires on the attack, so the attack tells nothing about the second.
        game.Attack(yeti, SecretGame.OpponentHero, 4, game.Fires(50, "CORE_EX1_289"));
        var view = game.View()!;
        var slot = Assert.Single(view.Slots);
        Assert.Equal(51, slot.EntityId);
        Assert.Contains("JAIL_315", game.Open());
        Assert.Contains("EX1_289", game.Open());
    }

    [Fact]
    public void Two_secrets_narrow_together_and_show_how_many_can_be_each()
    {
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.OpponentSecret(51, "MAGE", 3);
        var view = game.View()!;
        Assert.Equal(2, view.Slots.Count);
        Assert.All(view.Rows, r => Assert.Equal(2, r.Possible));
        game.Turn(1);
        var spell = game.LocalCard("CS2_029", "SPELL");
        game.Play(spell, "CS2_029", 4, target: SecretGame.OpponentHero);
        Assert.Equal(0, game.View()!.Rows.Single(r => r.Key == "EX1_287").Possible);
    }

    [Fact]
    public void Turn_end_rules_out_flames_of_infinity_only_when_you_have_a_minion()
    {
        var empty = new SecretGame();
        empty.Turn(2);
        empty.OpponentSecret(50, "MAGE", 3);
        empty.Turn(1);
        empty.EndTurn();
        Assert.Contains("END_024", empty.Open());

        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.Turn(1);
        game.LocalCard("CS2_182", "MINION", "PLAY");
        game.EndTurn();
        Assert.DoesNotContain("END_024", game.Open());
    }

    [Fact]
    public void Events_on_the_opponents_own_turn_rule_nothing_out()
    {
        var game = new SecretGame("HERO_04", "FT_WILD");
        game.Turn(2);
        var minion = game.OpponentMinion();
        game.OpponentMinion();
        game.OpponentSecret(50, "PALADIN", 1);
        // Their minion dies on their own turn: Redemption and the like do not fire then.
        game.Dies(minion);
        Assert.Contains("EX1_136", game.Open());
    }

    [Fact]
    public void A_death_on_your_turn_rules_out_death_secrets_with_their_conditions()
    {
        var game = new SecretGame("HERO_04", "FT_WILD");
        game.Turn(2);
        var dies = game.OpponentMinion();
        game.OpponentSecret(50, "PALADIN", 1);
        game.Turn(1);
        game.Dies(dies);
        var open = game.Open();
        Assert.DoesNotContain("EX1_136", open);   // Redemption
        Assert.DoesNotContain("CFM_800", open);   // Getaway Kodo (their hand has room)
        Assert.Contains("FP1_020", open);         // Avenge needs another minion of theirs
    }

    [Fact]
    public void Untimely_death_needs_the_minion_to_die_the_turn_after_it_was_played()
    {
        var game = new SecretGame("HERO_05");
        game.Turn(2);
        game.OpponentSecret(50, "HUNTER", 2);
        // They play a minion from hand on their turn...
        game.Feed(
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(51, "DECK", 2) + " tag=ZONE value=HAND ",
            "DebugPrintPower() - BLOCK_START BlockType=PLAY Entity=" + SecretGame.Ref(51, "HAND", 2) + " EffectCardId=System.Collections.Generic.List`1[System.String] EffectIndex=0 Target=0 SubOption=-1 ",
            "DebugPrintPower() -     SHOW_ENTITY - Updating Entity=" + SecretGame.Ref(51, "HAND", 2) + " CardID=CS2_182",
            "DebugPrintPower() -         tag=CARDTYPE value=MINION",
            "DebugPrintPower() -     TAG_CHANGE Entity=" + SecretGame.Ref(51, "HAND", 2, "CS2_182") + " tag=ZONE value=PLAY ",
            "DebugPrintPower() - BLOCK_END");
        Assert.Contains("TIME_620", game.Open());
        // ...and it dies on your next turn.
        game.Turn(1);
        game.Dies(51);
        Assert.DoesNotContain("TIME_620", game.Open());
    }

    [Fact]
    public void Three_cards_in_a_turn_rule_out_rat_trap_when_their_board_has_room()
    {
        var game = new SecretGame("HERO_05");
        game.Turn(2);
        game.OpponentSecret(50, "HUNTER", 2);
        game.Turn(1);
        for (var i = 0; i < 2; i++)
        {
            var coin = game.LocalCard("GAME_005", "SPELL");
            game.Play(coin, "GAME_005", 0);
            Assert.Contains("GIL_577", game.Open());
        }

        var third = game.LocalCard("CS2_106", "WEAPON");
        game.Play(third, "CS2_106", 3);
        Assert.DoesNotContain("GIL_577", game.Open());
        // Pressure Plate destroys one of your minions: with none on the board it could not have fired.
        Assert.Contains("ULD_152", game.Open());
    }

    [Fact]
    public void Their_turn_start_rules_out_competitive_spirit_when_they_have_a_minion()
    {
        var game = new SecretGame("HERO_04", "FT_WILD");
        game.Turn(2);
        game.OpponentSecret(50, "PALADIN", 1);
        game.Turn(1);
        game.EndTurn();
        Assert.Contains("AT_073", game.Open());   // no minion at their turn start
        game.OpponentMinion();
        game.Turn(2);
        Assert.DoesNotContain("AT_073", game.Open());
    }

    [Fact]
    public void Crossing_out_by_hand_moves_the_row_down_and_can_be_undone()
    {
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.Tracker.Toggle("EX1_287");
        var view = game.View()!;
        var row = view.Rows.Single(r => r.Key == "EX1_287");
        Assert.True(row.Crossed);
        Assert.False(row.IsOpen);
        Assert.Equal(5, view.Slots[0].Possible);
        Assert.True(view.Rows.TakeWhile(r => r.IsOpen).Count() == 5);
        game.Tracker.Toggle("EX1_287");
        Assert.Contains("EX1_287", game.Open());
    }

    [Fact]
    public void A_new_game_forgets_crossed_secrets()
    {
        var game = new SecretGame();
        game.Tracker.Toggle("EX1_287");
        game.Tracker.OnGameStarted(game.Game);
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        Assert.Contains("EX1_287", game.Open());
    }

    [Fact]
    public void A_secret_seen_in_hand_is_known_and_cannot_be_the_other_one()
    {
        var game = new SecretGame();
        game.Turn(2);
        // A Counterspell they got face up (created into their hand by your card), then played.
        game.Feed(
            "DebugPrintPower() - FULL_ENTITY - Creating ID=80 CardID=CORE_EX1_287",
            "DebugPrintPower() -     tag=ZONE value=HAND",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(80, "HAND", 2, "CORE_EX1_287") + " tag=SECRET value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(80, "HAND", 2, "CORE_EX1_287") + " tag=ZONE value=SECRET ");
        game.OpponentSecret(50, "MAGE", 3);
        var view = game.View()!;
        Assert.Equal("CORE_EX1_287", view.Slots.Single(s => s.EntityId == 80).KnownCardId);
        var counterspell = view.Rows.Single(r => r.Key == "EX1_287");
        Assert.True(counterspell.Known);
        Assert.Equal(0, counterspell.Possible);
        Assert.Equal(5, view.Slots.Single(s => s.EntityId == 50).Possible);
    }

    [Fact]
    public void A_drawn_card_stays_unknown_even_when_the_log_carries_its_id()
    {
        // The log can keep an id for a card back (shuffled in by you, revealed earlier): the player cannot see it.
        var game = new SecretGame();
        game.Turn(2);
        game.Feed(
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(52, "DECK", 2, "CORE_EX1_287") + " tag=ZONE value=HAND ",
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(52, "HAND", 2, "CORE_EX1_287") + " tag=CLASS value=MAGE ",
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(52, "HAND", 2, "CORE_EX1_287") + " tag=SECRET value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(52, "HAND", 2, "CORE_EX1_287") + " tag=ZONE value=SECRET ");
        var view = game.View()!;
        Assert.Null(view.Slots.Single().KnownCardId);
        Assert.DoesNotContain(view.Rows, r => r.Known);
        Assert.Equal(6, view.Slots.Single().Possible);
    }

    [Fact]
    public void Two_shown_copies_from_the_deck_rule_the_card_out_of_a_deck_secret_but_not_of_a_created_one()
    {
        var game = new SecretGame();
        game.Turn(2);
        foreach (var id in new[] { 53, 54 })
        {
            game.OpponentSecret(id, "MAGE", 3);
            game.Feed(game.Fires(id, "CORE_EX1_287"));
        }

        game.OpponentSecret(55, "MAGE", 3);
        // Made mid-game by a card: copies from the deck say nothing about it.
        game.Feed(
            "DebugPrintPower() - FULL_ENTITY - Creating ID=90 CardID=",
            "DebugPrintPower() -     tag=ZONE value=SECRET",
            "DebugPrintPower() -     tag=CONTROLLER value=2",
            "DebugPrintPower() -     tag=CLASS value=MAGE",
            "DebugPrintPower() -     tag=SECRET value=1");
        var view = game.View()!;
        Assert.Equal(5, view.Slots.Single(s => s.EntityId == 55).Possible);
        Assert.Equal(6, view.Slots.Single(s => s.EntityId == 90).Possible);
        Assert.Equal(1, view.Rows.Single(r => r.Key == "EX1_287").Possible);
    }

    [Fact]
    public void Arena_has_no_copy_limit_and_the_wide_pool()
    {
        var game = new SecretGame(gameType: "GT_ARENA");
        game.Turn(2);
        foreach (var id in new[] { 53, 54 })
        {
            game.OpponentSecret(id, "MAGE", 3);
            game.Feed(game.Fires(id, "CORE_EX1_287"));
        }

        game.OpponentSecret(55, "MAGE", 3);
        var view = game.View()!;
        Assert.Contains("EX1_287", game.Open());
        Assert.Equal(new SecretPool(SecretGame.Cards).For("MAGE", SecretFormat.Wild).Count, view.Slots.Single().Possible);
    }

    [Fact]
    public void Mana_paid_drops_cheaper_secrets_unless_that_would_leave_none()
    {
        var cards = CardDb.Parse("enUS", new MemoryStream("""
            [
              {"id":"HERO_08","dbfId":637,"name":"Jaina","type":"HERO","cardClass":"MAGE"},
              {"id":"TST_001","dbfId":1,"name":"Cheap Secret","type":"SPELL","cardClass":"MAGE","set":"CORE","cost":1,"collectible":true,"mechanics":["SECRET"]},
              {"id":"TST_002","dbfId":2,"name":"Dear Secret","type":"SPELL","cardClass":"MAGE","set":"CORE","cost":3,"collectible":true,"mechanics":["SECRET"]}
            ]
            """u8.ToArray()));
        var game = new SecretGame();
        game.Turn(2);
        game.OpponentSecret(50, "MAGE", 3);
        game.OpponentSecret(51, "MAGE", 5);
        game.View();
        var view = game.Tracker.BuildView(game.Game, cards)!;
        Assert.Equal(1, view.Slots.Single(s => s.EntityId == 50).Possible);
        Assert.Equal(1, view.Rows.Single(r => r.Key == "TST_001").Possible);
        // Paid 5 for a pool that costs at most 3: a cost increase, so the paid Mana says nothing.
        Assert.Equal(2, view.Slots.Single(s => s.EntityId == 51).Possible);
    }

    [Fact]
    public void Class_comes_from_the_secret_icon_then_from_the_hero()
    {
        var game = new SecretGame("HERO_03", "FT_WILD");
        game.Turn(2);
        // Rogue playing a Mage Secret (Perjury, a Discover from another class).
        game.OpponentSecret(50, "MAGE", 0);
        game.Feed(
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(51, "DECK", 2) + " tag=ZONE value=HAND ",
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(51, "HAND", 2) + " tag=SECRET value=1 ",
            "DebugPrintPower() - TAG_CHANGE Entity=" + SecretGame.Ref(51, "HAND", 2) + " tag=ZONE value=SECRET ");
        var view = game.View()!;
        Assert.Equal("MAGE", view.Slots.Single(s => s.EntityId == 50).CardClass);
        Assert.Equal("ROGUE", view.Slots.Single(s => s.EntityId == 51).CardClass);
        Assert.Contains(view.Rows, r => r.CardClass == "MAGE");
        Assert.Contains(view.Rows, r => r.CardClass == "ROGUE");
    }

    [Fact]
    public void The_render_scene_runs_the_real_tracker()
    {
        var view = SecretsScene.Synthetic(SecretGame.Cards)!.Secrets!;
        Assert.Equal(2, view.Slots.Count);
        Assert.All(view.Slots, s => Assert.Equal((3, 3), (s.PaidCost!.Value, s.Turn)));
        Assert.EndsWith("Standard", view.Pool);
        Assert.Equal(0, view.Rows.Single(r => r.Key == "EX1_289").Possible);
        Assert.True(view.Rows.Single(r => r.Key == "LOOT_101").Crossed);
        Assert.Equal(["EX1_287", "END_024", "BAR_812"], view.Rows.Where(r => r.IsOpen).Select(r => r.Key).ToArray());
    }

    [Theory]
    [InlineData(SecretTrigger.HeroAttacked, SecretNeeds.BoardSpace, true)]
    [InlineData(SecretTrigger.HeroAttacked, SecretNeeds.None, false)]
    [InlineData(SecretTrigger.MinionAttacked, SecretNeeds.BoardSpace, false)]
    public void A_rule_needs_its_trigger_and_every_condition(SecretTrigger trigger, SecretNeeds held, bool expected)
    {
        var bearTrap = SecretRules.For("AT_060")!;
        Assert.Equal(expected, SecretTracker.RulesOut(bearTrap, trigger, held));
    }
}
