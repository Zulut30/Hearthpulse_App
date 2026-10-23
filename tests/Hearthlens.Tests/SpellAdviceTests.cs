using System.Text;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Tracking;
using Xunit;

namespace Hearthlens.Tests;

/// <summary>Tavern spells: scoring, best-spell pick and price-aware planning.</summary>
public sealed class SpellAdviceTests
{
    private const string SpellId = "SPELL_ECON";

    [Fact]
    public void Spell_is_scored_and_recommended_after_the_first_priority_action()
    {
        var (advisor, cards, filler) = Setup(gold: 2, boardFull: false);
        var view = View(gold: 2, shop: [filler, Spell(cost: 2)], upgradeCost: 11, turn: 5, techLevel: 3);

        var enriched = advisor.Enrich(view, null, cards);

        var pick = Assert.Single(enriched.ShopAdvice, p => p.CardId == SpellId);
        Assert.True(pick.IsSpell);
        Assert.True(pick.Score >= 25, $"spell scored {pick.Score}");
        Assert.True(pick.Best);
        Assert.Equal(2, pick.Cost);
        Assert.NotNull(enriched.BestSpell);
        Assert.Equal(SpellId, enriched.BestSpell!.CardId);
    }

    [Fact]
    public void Planner_buys_a_spell_at_its_own_price_and_does_not_sell_for_hand_space()
    {
        var (advisor, cards, filler) = Setup(gold: 2, boardFull: false);
        var view = View(gold: 2, shop: [filler, Spell(cost: 2)], upgradeCost: 11, turn: 5, techLevel: 3);

        var plan = advisor.Enrich(view, null, cards).ShopPlan;

        Assert.NotNull(plan);
        Assert.Equal(ShopAction.Buy, plan!.Action);
        var step = Assert.Single(plan.Steps);
        Assert.Equal(2, step.Cost);
        Assert.Equal(0, plan.GoldLeft);
        Assert.DoesNotContain(plan.Steps, s => s.Action == ShopAction.Sell);
    }

    [Fact]
    public void Planner_does_not_afford_a_spell_it_cannot_pay_for()
    {
        var (advisor, cards, filler) = Setup(gold: 2, boardFull: false);
        var view = View(gold: 2, shop: [filler, Spell(cost: 3)], upgradeCost: 11, turn: 5, techLevel: 3);

        var plan = advisor.Enrich(view, null, cards).ShopPlan;

        Assert.NotNull(plan);
        Assert.Equal(ShopAction.Save, plan!.Action);
    }

    [Fact]
    public void Planner_does_not_sell_for_a_spell_when_the_board_is_full()
    {
        var (advisor, cards, filler) = Setup(gold: 2, boardFull: true);
        var view = View(gold: 2, shop: [filler, Spell(cost: 2)], upgradeCost: 11, turn: 5, techLevel: 3, boardFull: true);

        var plan = advisor.Enrich(view, null, cards).ShopPlan;

        Assert.NotNull(plan);
        Assert.Equal(ShopAction.Buy, plan!.Action);
        Assert.DoesNotContain(plan.Steps, s => s.Action == ShopAction.Sell);
        Assert.Equal(0, plan.GoldLeft);
    }

    private static BgMinion Spell(int cost) => new(
        SpellId, "Easy Gold", 0, 0, Golden: false, TechLevel: 3, Keywords: Array.Empty<string>())
    {
        CardType = "BATTLEGROUND_SPELL",
        Cost = cost,
    };

    private static (BattlegroundsAdvisor Advisor, CardDb Cards, BgMinion Filler) Setup(int gold, bool boardFull)
    {
        const string json = """
            [
              { "id": "TEST_HERO", "dbfId": 1, "name": "Test Hero", "type": "HERO", "battlegroundsHero": true },
              { "id": "SPELL_ECON", "dbfId": 20, "name": "Easy Gold", "type": "SPELL", "cost": 2, "techLevel": 3,
                "isBattlegroundsPoolSpell": true, "text": "Get 2 Gold. Give a minion +2/+2." },
              { "id": "FILLER_T1", "dbfId": 21, "name": "Filler One", "type": "MINION", "techLevel": 1,
                "isBattlegroundsPoolMinion": true, "attack": 2, "health": 2 },
              { "id": "TB_BaconShopTechUp04_Button", "dbfId": 40, "name": "Tavern 4", "type": "GAME_MODE_BUTTON", "cost": 8 },
              { "id": "TB_BaconShopTechUp05_Button", "dbfId": 50, "name": "Tavern 5", "type": "GAME_MODE_BUTTON", "cost": 11 }
            ]
            """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var cards = CardDb.Parse("enUS", stream);

        var meta = new BattlegroundsMeta(
            new Dictionary<int, IReadOnlyDictionary<string, MetaHero>> { [100] = new Dictionary<string, MetaHero>() },
            [],
            [],
            new Dictionary<string, MetaTrinket>(),
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(),
            new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var advisor = new BattlegroundsAdvisor { Meta = meta, CompMode = CompMode.Manual };
        return (advisor, cards, new BgMinion("FILLER_T1", "Filler One", 2, 2, Golden: false, TechLevel: 1, Keywords: Array.Empty<string>()));
    }

    private static BattlegroundsView View(
        int gold, IReadOnlyList<BgMinion> shop, int upgradeCost, int turn, int techLevel, bool boardFull = false)
    {
        var board = boardFull
            ? Enumerable.Range(0, 7)
                .Select(i => new BgMinion("BOARD_ONLY", $"Board Only {i}", 2, 2, Golden: false, TechLevel: 1, Keywords: Array.Empty<string>()))
                .ToList()
            : [];

        return new BattlegroundsView(
            Turn: turn,
            TechLevel: techLevel,
            MaxTechLevel: 6,
            HeroCardId: "TEST_HERO",
            Health: 30,
            Armor: 0,
            Place: 2,
            IsDuos: false,
            InCombat: false,
            IsHeroSelection: false,
            HeroOptions: [],
            Races: [],
            RacesConfirmed: false,
            Opponents: [],
            Combats: [],
            TierHistory: [],
            Board: board,
            AnomalyCardId: null,
            Triples: 0,
            Shop: shop,
            Hand: [])
        {
            Gold = gold,
            TavernUpgradeCost = upgradeCost,
        };
    }
}
