using System.Text;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Tracking;

namespace Hearthlens.Tests;

/// <summary>
/// Level-or-buy advice: core cards and triples come before the upgrade, the live button cost is used when
/// available, and the advice is withheld when the tavern is not actionable. Offline, hand-made card/meta data.
/// </summary>
public sealed class ShopPlanTests
{
    private const string Hero = "TEST_HERO";
    private static readonly string[] FullLobby = ["BEAST", "MURLOC", "DEMON", "PIRATE", "MECHANICAL"];

    [Fact]
    public void Core_card_in_the_tavern_is_bought_before_upgrading()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(
                gold: 9,
                board: [Minion("BEAST_T2", 2)],
                shop: [Minion("BEAST_T2", 2), Minion("FILLER_T4", 4)],
                upgradeCost: 9),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Buy, plan.Action);
        Assert.Equal("BEAST_T2", plan.CardId);
        Assert.Equal("BEAST_T2", plan.CardName);
        Assert.Equal(0, plan.Slot);
        Assert.Equal(3, plan.Cost);
        Assert.Equal(6, plan.GoldLeft);
        Assert.Contains("core card", plan.Reason);
    }

    [Fact]
    public void Triple_in_the_tavern_is_bought_even_without_a_comp_target()
    {
        var (advisor, cards) = Advisor(pin: null);

        var view = advisor.Enrich(
            View(
                gold: 3,
                board: [Minion("BEAST_T2", 2), Minion("BEAST_T2", 2)],
                shop: [Minion("BEAST_T2", 2)],
                upgradeCost: 8),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Buy, plan.Action);
        Assert.Equal("BEAST_T2", plan.CardId);
        Assert.Equal(0, plan.GoldLeft);
        Assert.Contains("triple", plan.Reason);
    }

    [Fact]
    public void Missing_core_at_the_next_tier_makes_the_live_upgrade_cost_the_best_spend()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(gold: 7, shop: [Minion("FILLER_T4", 4)], upgradeCost: 7),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Level, plan.Action);
        Assert.Equal(7, plan.Cost);
        Assert.Equal(0, plan.GoldLeft);
        Assert.Equal("Level to tier 5", plan.Title);
        Assert.Contains("BEAST_T5", string.Join(",", view.Comps.Single(c => c.IsTarget).Cards.Select(c => c.CardId)));
        Assert.Contains("unlocks Beasts core at T5", plan.Reason);
    }

    [Fact]
    public void Upgrade_cost_falls_back_to_the_card_database_base_cost()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(gold: 11, shop: [Minion("FILLER_T4", 4)], upgradeCost: null),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Level, plan.Action);
        Assert.Equal(11, plan.Cost);
        Assert.Equal(0, plan.GoldLeft);
    }

    [Fact]
    public void Weak_shop_rolls_when_rolling_still_leaves_gold_for_a_buy()
    {
        var (advisor, cards) = Advisor(pin: null);

        var view = advisor.Enrich(
            View(
                turn: 9,
                techLevel: 6,
                maxTechLevel: 6,
                gold: 4,
                board: [],
                shop: [Minion("FILLER_T4", 4)],
                upgradeCost: null),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Roll, plan.Action);
        Assert.Equal(1, plan.Cost);
        Assert.Equal(3, plan.GoldLeft);
        Assert.Contains("No strong buy", plan.Reason);
    }

    [Fact]
    public void Low_health_avoids_the_upgrade_even_when_a_next_tier_core_is_missing()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(gold: 9, health: 5, place: 7, turn: 10, shop: [Minion("FILLER_T4", 4)], upgradeCost: 9),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.NotEqual(ShopAction.Level, plan.Action);
        Assert.Equal(ShopAction.Roll, plan.Action);
    }

    [Fact]
    public void Level_then_buy_uses_the_remaining_gold()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(gold: 12, shop: [Minion("BEAST_ADDON", 4)], upgradeCost: 9),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Level, plan.Action);
        Assert.Equal("PLAN", plan.ActionLabel);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(ShopAction.Level, plan.Steps[0].Action);
        Assert.Equal(ShopAction.Buy, plan.Steps[1].Action);
        Assert.Equal("BEAST_ADDON", plan.Steps[1].CardId);
        Assert.Equal(12, plan.Cost);
        Assert.Equal(0, plan.GoldLeft);
        Assert.Contains("Level to tier 5", plan.Title);
        Assert.Contains("BEAST_ADDON", plan.Title);
    }

    [Fact]
    public void Core_buy_then_levels_with_leftover_gold()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(
                gold: 12,
                board: [Minion("BEAST_T2", 2)],
                shop: [Minion("BEAST_T2", 2)],
                upgradeCost: 9),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Buy, plan.Action);
        Assert.Equal("PLAN", plan.ActionLabel);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(ShopAction.Buy, plan.Steps[0].Action);
        Assert.Equal(ShopAction.Level, plan.Steps[1].Action);
        Assert.Equal(12, plan.Cost);
        Assert.Equal(0, plan.GoldLeft);
        Assert.Contains("Buy BEAST_T2", plan.Title);
        Assert.Contains("Level to tier 5", plan.Title);
    }

    [Fact]
    public void Full_board_sells_a_filler_before_buying_a_core()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(
                gold: 2,
                board:
                [
                    Minion("BEAST_T2", 2),
                    Minion("FILLER_T4", 4),
                    Minion("FILLER_T4", 4),
                    Minion("FILLER_T4", 4),
                    Minion("FILLER_T4", 4),
                    Minion("FILLER_T4", 4),
                    Minion("FILLER_T4", 4),
                ],
                shop: [Minion("BEAST_T2", 2)],
                upgradeCost: 9),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Buy, plan.Action);
        Assert.Equal("PLAN", plan.ActionLabel);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal(ShopAction.Sell, plan.Steps[0].Action);
        Assert.Equal(-1, plan.Steps[0].Cost);
        Assert.Equal(3, plan.Steps[0].GoldLeft);
        Assert.Equal(ShopAction.Buy, plan.Steps[1].Action);
        Assert.Equal(2, plan.Cost);
        Assert.Equal(0, plan.GoldLeft);
        Assert.Contains("Sell Filler Four", plan.Title);
        Assert.Contains("Buy BEAST_T2", plan.Title);
    }

    [Fact]
    public void Live_reroll_cost_is_charged()
    {
        var (advisor, cards) = Advisor(pin: null);

        var view = advisor.Enrich(
            View(
                turn: 9,
                techLevel: 6,
                maxTechLevel: 6,
                gold: 5,
                board: [],
                shop: [Minion("FILLER_T4", 4)],
                upgradeCost: null,
                buttonCosts: new BattlegroundsButtonCosts(null, 2, null, null)),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Roll, plan.Action);
        Assert.Equal(2, plan.Cost);
        Assert.Equal(3, plan.GoldLeft);
        Assert.Equal(2, Assert.Single(plan.Steps).Cost);
    }

    [Fact]
    public void No_advice_while_in_combat_or_before_gold_is_known()
    {        var (advisor, cards) = Advisor();

        var combat = advisor.Enrich(
            View(gold: 9, inCombat: true, shop: [Minion("FILLER_T4", 4)], upgradeCost: 9),
            null,
            cards);
        var noGold = advisor.Enrich(
            View(gold: 0, shop: [Minion("FILLER_T4", 4)], upgradeCost: 9),
            null,
            cards);

        Assert.Null(combat.ShopPlan);
        Assert.Null(noGold.ShopPlan);
    }

    [Fact]
    public void Freeze_keeps_a_triple_the_player_cannot_afford_yet()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(
                gold: 1,
                board: [Minion("BEAST_T2", 2), Minion("BEAST_T2", 2)],
                shop: [Minion("BEAST_T2", 2)],
                upgradeCost: 8),
            null,
            cards);

        Assert.Contains("triple", view.FreezeAdvice);
        Assert.Contains("BEAST_T2", view.FreezeAdvice);
    }

    [Fact]
    public void No_freeze_advice_when_the_card_can_be_bought_or_the_gold_is_unknown()
    {
        var (advisor, cards) = Advisor();

        var affordable = advisor.Enrich(
            View(
                gold: 9,
                board: [Minion("BEAST_T2", 2), Minion("BEAST_T2", 2)],
                shop: [Minion("BEAST_T2", 2)],
                upgradeCost: 8),
            null,
            cards);
        var unknown = advisor.Enrich(
            View(
                gold: 0,
                board: [Minion("BEAST_T2", 2), Minion("BEAST_T2", 2)],
                shop: [Minion("BEAST_T2", 2)],
                upgradeCost: 8),
            null,
            cards);

        Assert.Equal(string.Empty, affordable.FreezeAdvice);
        Assert.Equal(string.Empty, unknown.FreezeAdvice);
    }

    [Fact]
    public void A_pair_is_bought_instead_of_rolling_the_tavern()
    {
        var (advisor, cards) = Advisor(pin: null);

        var view = advisor.Enrich(
            View(
                turn: 6,
                techLevel: 3,
                gold: 4,
                board: [Minion("FILLER_T4", 4)],
                shop: [Minion("FILLER_T4", 4)],
                upgradeCost: 8),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Buy, plan.Action);
        Assert.Contains("pair", plan.Reason);
    }

    [Fact]
    public void A_core_card_is_never_sold_to_make_room_for_a_filler()
    {
        var (advisor, cards) = Advisor();

        var view = advisor.Enrich(
            View(
                gold: 3,
                board:
                [
                    Minion("BEAST_T2", 2), Minion("BEAST_T2", 2), Minion("BEAST_T2", 2), Minion("BEAST_T2", 2),
                    Minion("BEAST_T2", 2), Minion("BEAST_T2", 2), Minion("BEAST_T2", 2),
                ],
                shop: [Minion("FILLER_T4", 4)],
                upgradeCost: 11),
            null,
            cards);

        var plan = Assert.IsType<ShopPlan>(view.ShopPlan);
        Assert.Equal(ShopAction.Save, plan.Action);
        Assert.DoesNotContain(plan.Steps, step => step.Action == ShopAction.Sell);
    }

    [Fact]
    public void Comp_cards_that_are_not_in_the_current_pool_are_hidden()
    {
        var (advisor, cards) = Advisor("beasts", ("GHOST_T3", "CORE"));

        var view = advisor.Enrich(
            View(gold: 9, shop: [Minion("BEAST_T2", 2)], upgradeCost: 9),
            null,
            cards);

        var comp = view.Comps.Single(c => c.IsTarget);
        Assert.DoesNotContain(comp.Cards, card => card.CardId == "GHOST_T3");
        Assert.Contains(comp.Cards, card => card.CardId == "BEAST_T2");
        Assert.DoesNotContain("GHOST_T3", comp.LookFor);
    }

    private static (BattlegroundsAdvisor Advisor, CardDb Cards) Advisor(string? pin = "beasts", params (string CardId, string Status)[] extraCompCards)
    {
        const string json = """
            [
              { "id": "TEST_HERO", "dbfId": 1, "name": "Test Hero", "type": "HERO", "battlegroundsHero": true },
              { "id": "BEAST_T2", "dbfId": 10, "name": "Beast Two", "type": "MINION", "races": ["BEAST"], "techLevel": 2, "isBattlegroundsPoolMinion": true },
              { "id": "BEAST_T5", "dbfId": 11, "name": "Beast Five", "type": "MINION", "races": ["BEAST"], "techLevel": 5, "isBattlegroundsPoolMinion": true },
              { "id": "BEAST_ADDON", "dbfId": 13, "name": "Beast Addon", "type": "MINION", "races": ["BEAST"], "techLevel": 4, "isBattlegroundsPoolMinion": true },
              { "id": "FILLER_T4", "dbfId": 12, "name": "Filler Four", "type": "MINION", "techLevel": 4, "isBattlegroundsPoolMinion": true },
              { "id": "TB_BaconShopTechUp04_Button", "dbfId": 40, "name": "Tavern 4", "type": "GAME_MODE_BUTTON", "cost": 8 },
              { "id": "TB_BaconShopTechUp05_Button", "dbfId": 50, "name": "Tavern 5", "type": "GAME_MODE_BUTTON", "cost": 11 },
              { "id": "TB_BaconShopTechUp06_Button", "dbfId": 60, "name": "Tavern 6", "type": "GAME_MODE_BUTTON", "cost": 11 }
            ]
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var cards = CardDb.Parse("enUS", stream);

        var meta = new BattlegroundsMeta(
            new Dictionary<int, IReadOnlyDictionary<string, MetaHero>> { [100] = new Dictionary<string, MetaHero>() },
            [],
            [Comp("beasts", "Beasts", "BEAST", 4.0, [("BEAST_T2", "CORE"), ("BEAST_T5", "CORE"), ("BEAST_ADDON", "ADDON")], extraCompCards)],
            new Dictionary<string, MetaTrinket>(),
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(),
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var advisor = new BattlegroundsAdvisor { Meta = meta, CompMode = CompMode.Manual, PinnedCompId = pin };
        return (advisor, cards);
    }

    private static MetaComp Comp(
        string id, string name, string tribe, double placement, params (string CardId, string Status)[] cards) => Comp(id, name, tribe, placement, cards, []);

    private static MetaComp Comp(
        string id, string name, string tribe, double placement, (string CardId, string Status)[] cards, (string CardId, string Status)[] extra) => new(
        id,
        name,
        "A",
        "Easy",
        [tribe],
        cards.Concat(extra).Select(c => new MetaCompCard(c.CardId, c.Status)).ToList(),
        null,
        null,
        placement,
        new Dictionary<int, double>(),
        new Dictionary<string, (int DataPoints, double Placement)>());

    private static BattlegroundsView View(
        int turn = 9,
        int techLevel = 4,
        int maxTechLevel = 6,
        int gold = 9,
        int health = 30,
        int place = 2,
        IReadOnlyList<BgMinion>? board = null,
        IReadOnlyList<BgMinion>? shop = null,
        int? upgradeCost = null,
        BattlegroundsButtonCosts? buttonCosts = null,
        bool inCombat = false) => new(
            Turn: turn,
            TechLevel: techLevel,
            MaxTechLevel: maxTechLevel,
            HeroCardId: Hero,
            Health: health,
            Armor: 0,
            Place: place,
            IsDuos: false,
            InCombat: inCombat,
            IsHeroSelection: false,
            HeroOptions: [],
            Races: FullLobby,
            RacesConfirmed: true,
            Opponents: [],
            Combats: [],
            TierHistory: [],
            Board: board ?? [Minion("BEAST_T2", 2)],
            AnomalyCardId: null,
            Triples: 0,
            Shop: shop ?? [Minion("FILLER_T4", 4)],
            Hand: [])
        {
            Gold = gold,
            TavernUpgradeCost = upgradeCost,
            ButtonCosts = buttonCosts,
        };

    private static BgMinion Minion(string cardId, int tier) =>
        new(CardId: cardId, Name: cardId, Attack: 2, Health: 2, Golden: false, TechLevel: tier, Keywords: []);
}
