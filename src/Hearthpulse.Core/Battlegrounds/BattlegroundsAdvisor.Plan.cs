using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>The turn plan for the tavern: buys, sells and levelling within the live gold and button costs.</summary>
public sealed partial class BattlegroundsAdvisor
{
    /// <summary>
    /// Builds the best affordable sequence for this tavern, not just one action: core/triple buys first, a sell
    /// when the board is full, then level/buy with the remaining gold. Live button costs drive the budget.
    /// </summary>
    private static ShopPlan? PlanShop(
        BattlegroundsView view, CompAdvice? target, IReadOnlyList<ShopPick> shop, CardDb cards)
    {
        // Gold 0 is also the "unknown" value, and combat/choices cover the tavern: never guess with either.
        if (view.Gold <= 0 || view.InCombat || view.IsHeroSelection
            || view.DiscoverOptions.Count > 0 || view.TrinketOptions.Count > 0)
        {
            return null;
        }

        var best = shop.Where(p => p.Score > 0).MaxBy(p => p.Score);
        var levelCost = LevelCost(view, cards);
        var missingNextCore = target?.Cards.Any(c => c.Status == "CORE" && !c.Owned && c.TechLevel == view.TechLevel + 1) == true;
        var behind = BehindCurve(view);
        var weakShop = best is null || best.Score < StrongShopScore;
        var underTiered = BoardUnderTiered(view);
        var levelDesired = missingNextCore || behind || underTiered || weakShop;
        var safe = SafeToLevel(view);
        var boardFull = view.Board.Count >= 7;

        var gold = view.Gold;
        var sold = false;
        var steps = new List<ShopStep>();

        // A tavern spell goes to hand, so it never needs board space and costs its own price.
        static int PriceOf(ShopPick? pick) => pick is null or { Cost: <= 0 } ? MinionCost : pick.Cost;
        static bool NeedsBoardSpace(ShopPick pick) => !pick.IsSpell;

        bool CanLevelNow() => levelCost is { } cost && cost <= gold;

        // A full board can only take a minion when a minion that is not part of the plan can be sold.
        bool CanMakeRoom() => !boardFull || sold || WorstBoardMinion(view, target, cards) is not null;

        bool CanBuyNow() => best is not null
            && CanMakeRoom()
            && (gold >= PriceOf(best)
                || (NeedsBoardSpace(best) && boardFull && !sold && gold + SellRefund >= PriceOf(best)));

        // Do not recommend a roll that cannot also pay for the card it is meant to find.
        bool CanRollThenBuy() => best is not null
            && CanMakeRoom()
            && (gold >= view.RerollCost + PriceOf(best)
                || (NeedsBoardSpace(best) && boardFull && !sold && gold + SellRefund >= view.RerollCost + PriceOf(best)));

        void AddSell()
        {
            var worst = WorstBoardMinion(view, target, cards);
            if (worst is null)
            {
                return;
            }

            gold += SellRefund;
            sold = true;
            steps.Add(new ShopStep(
                ShopAction.Sell,
                Loc.F("Core.Bg.Step.Sell", worst.Value.Name),
                Loc.F("Core.Bg.Step.SellDetail", SellRefund, gold),
                -SellRefund,
                gold,
                worst.Value.CardId,
                worst.Value.Name,
                worst.Value.Slot));
        }

        void AddBuy()
        {
            if (best is null)
            {
                return;
            }

            if (boardFull && !sold && NeedsBoardSpace(best))
            {
                AddSell();
                if (!sold)
                {
                    return; // every minion on the board is worth keeping
                }
            }

            var price = PriceOf(best);
            if (gold < price)
            {
                return;
            }

            gold -= price;
            steps.Add(new ShopStep(
                ShopAction.Buy,
                Loc.F("Core.Bg.Step.Buy", best.Name),
                Loc.F("Core.Bg.Step.BuyDetail", BuyDetail(best, target), price, gold),
                price,
                gold,
                best.CardId,
                best.Name,
                best.Slot));
        }

        void AddLevel()
        {
            if (levelCost is not { } cost || gold < cost)
            {
                return;
            }

            gold -= cost;
            var spend = cost == 0 ? Loc.T("Core.Bg.Free") : Loc.F("Core.Bg.Gold", cost);
            steps.Add(new ShopStep(
                ShopAction.Level,
                Loc.F("Core.Bg.Step.Level", view.TechLevel + 1),
                Loc.F("Core.Bg.Step.LevelDetail", LevelReason(view, target, missingNextCore, behind, underTiered, weakShop), spend, gold),
                cost,
                gold));
        }

        void AddRoll()
        {
            var rollCost = view.RerollCost;
            if (gold < rollCost)
            {
                return;
            }

            gold -= rollCost;
            var lookFor = target is null ? Loc.T("Core.Bg.LookForBetterBoard") : Loc.F("Core.Bg.LookForComp", target.Name);
            steps.Add(new ShopStep(
                ShopAction.Roll,
                Loc.T("Core.Bg.Step.Roll"),
                Loc.F("Core.Bg.Step.RollDetail", lookFor, rollCost, gold),
                rollCost,
                gold));
        }

        if (best is not null && CanBuyNow() && IsPriorityPick(best, target))
        {
            AddBuy();
            if (levelDesired && safe && CanLevelNow())
            {
                AddLevel();
            }

            return BuildPlan(steps);
        }

        if (levelCost is { } cost && cost <= gold && safe && levelDesired)
        {
            AddLevel();
            if (CanBuyNow() && (!weakShop || !CanRollThenBuy()))
            {
                AddBuy();
            }
            else if (weakShop && CanRollThenBuy())
            {
                AddRoll();
            }

            return BuildPlan(steps);
        }

        if (weakShop && CanRollThenBuy() && !IsPriorityPick(best, target))
        {
            AddRoll();
            return BuildPlan(steps);
        }

        if (CanBuyNow())
        {
            AddBuy();
            return BuildPlan(steps);
        }

        if (CanLevelNow() && safe)
        {
            AddLevel();
            return BuildPlan(steps);
        }

        steps.Add(new ShopStep(
            ShopAction.Save,
            Loc.T("Core.Bg.Step.Hold"),
            HoldDetail(view, gold, levelCost, boardFull),
            0,
            gold));
        return BuildPlan(steps);
    }

    /// <summary>Why the planner keeps the gold: the next upgrade, a full board worth keeping, or bad odds to level.</summary>
    private static string HoldDetail(BattlegroundsView view, int gold, int? levelCost, bool boardFull)
    {
        if (levelCost is { } cost && gold < cost)
        {
            return Loc.F("Core.Bg.Hold.Save", gold, view.TechLevel + 1, cost);
        }

        if (boardFull)
        {
            return Loc.T("Core.Bg.Hold.BoardFull");
        }

        return SafeToLevel(view)
            ? Loc.T("Core.Bg.Hold.NothingWorthIt")
            : Loc.F("Core.Bg.Hold.TooRisky", view.Health + view.Armor);
    }

    /// <summary>
    /// Cards the plan buys before it considers leveling or rerolling: a triple or a core card at any time, and a
    /// pair, because a triple it completes discovers a higher-tier minion.
    /// </summary>
    private static bool IsPriorityPick(ShopPick? pick, CompAdvice? target) =>
        pick is not null
        && (pick.Reason.Contains("Triple", StringComparison.Ordinal)
            || pick.Reason.Contains("Core", StringComparison.Ordinal)
            || pick.Reason.Contains("Pair", StringComparison.Ordinal)
            || target?.Cards.Any(c => c.CardId == pick.CardId && c.Status == "CORE") == true);

    private static string BuyDetail(ShopPick pick, CompAdvice? target) =>
        pick.IsSpell
            ? Loc.T("Core.Bg.Buy.BestSpell")
            : pick.Reason.Contains("Triple", StringComparison.Ordinal)
            ? Loc.T("Core.Bg.CompletesTriple")
            : pick.Reason.Contains("Core", StringComparison.Ordinal)
                ? Loc.F("Core.Bg.Buy.Core", ForComp(target))
                : pick.Reason.Contains("Pair", StringComparison.Ordinal)
                    ? Loc.T("Core.Bg.Buy.Pair")
                    : Loc.T("Core.Bg.Buy.BestCard");

    private static string LevelReason(
        BattlegroundsView view, CompAdvice? target,
        bool missingNextCore, bool behind, bool underTiered, bool weakShop)
    {
        var next = view.TechLevel + 1;
        var reasons = new List<string>();
        if (missingNextCore && target is not null)
        {
            reasons.Add(Loc.F("Core.Bg.Level.Unlocks", target.Name, next));
        }
        else if (target is not null)
        {
            reasons.Add(Loc.F("Core.Bg.Level.Toward", target.Name));
        }

        if (behind)
        {
            reasons.Add(Loc.F("Core.Bg.Level.Behind", view.TechLevel, view.Turn));
        }

        if (underTiered)
        {
            reasons.Add(Loc.T("Core.Bg.Level.UnderTiered"));
        }

        if (weakShop)
        {
            reasons.Add(Loc.T("Core.Bg.Level.WeakShop"));
        }

        if (reasons.Count == 0)
        {
            reasons.Add(Loc.T("Core.Bg.Level.BestUse"));
        }

        return string.Join(" · ", reasons);
    }

    private static ShopPlan BuildPlan(List<ShopStep> steps)
    {
        if (steps.Count == 0)
        {
            throw new InvalidOperationException("A shop plan needs at least one step.");
        }

        // A sell is setup, not the headline action; keep the badge and title on the buy/level/roll it enables.
        var primary = steps.FirstOrDefault(s => s.Action != ShopAction.Sell) ?? steps[0];
        var total = steps.Sum(s => s.Cost);
        var title = steps.Count == 1
            ? primary.Title
            : string.Join(" → ", steps.Select(s => s.Title));
        var reason = steps.Count == 1
            ? primary.Detail
            : Loc.F("Core.Bg.PlanSummary", steps.Count, total, steps[^1].GoldLeft);
        return new ShopPlan(
            primary.Action,
            title,
            reason,
            total,
            steps[^1].GoldLeft,
            primary.CardId,
            primary.CardName,
            primary.Slot)
        {
            Steps = steps,
        };
    }

    /// <summary>
    /// Weakest board minion to sell when a buy needs space. Core and add-on cards of the target comp, goldens and
    /// pairs are never sold — the planner must not cannibalize its own comp to make room for a marginal card.
    /// Returns null when every minion is worth keeping.
    /// </summary>
    private static (string CardId, string Name, int Slot)? WorstBoardMinion(
        BattlegroundsView view, CompAdvice? target, CardDb cards)
    {
        if (view.Board.Count == 0)
        {
            return null;
        }

        var owned = OwnedCards(view, cards);
        var worstIndex = -1;
        var worstScore = int.MaxValue;
        for (var i = 0; i < view.Board.Count; i++)
        {
            var minion = view.Board[i];
            var normal = NormalId(minion.CardId, cards);
            var comp = target?.Cards.FirstOrDefault(c => c.CardId == normal);
            // Only the last copy of a pair is protected: with three or more copies one can be sold and the pair
            // still stands.
            if (comp is { Status: "CORE" or "ADDON" } || minion.Golden || owned.GetValueOrDefault(normal) == 2)
            {
                continue;
            }

            var score = minion.Attack + minion.Health + (minion.TechLevel * 3) + (minion.Keywords.Count * 5);
            if (score < worstScore)
            {
                worstScore = score;
                worstIndex = i;
            }
        }

        if (worstIndex < 0)
        {
            return null;
        }

        var chosen = view.Board[worstIndex];
        var name = cards.NameOf(chosen.CardId);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = chosen.Name;
        }

        return (chosen.CardId, name, worstIndex);
    }

    private static string ForComp(CompAdvice? target) => target is null ? string.Empty : Loc.F("Core.Bg.ForComp", target.Name);

    /// <summary>Live button cost first, then cards.json's base cost for the next tier; null at max tier.</summary>
    private static int? LevelCost(BattlegroundsView view, CardDb cards)
    {
        if (view.TechLevel >= view.MaxTechLevel)
        {
            return null;
        }

        if (view.TavernUpgradeCost is { } live)
        {
            return Math.Max(0, live);
        }

        if (view.ButtonCosts?.Upgrade is { } buttonCost)
        {
            return Math.Max(0, buttonCost);
        }

        var next = view.TechLevel + 1;
        var button = cards.Get($"TB_BaconShopTechUp0{next}_Button");
        if (button?.Cost > 0)
        {
            return button.Cost;
        }

        // Base costs behind the game-mode button cards; used only when neither memory nor cards.json has them.
        return next switch
        {
            2 => 5,
            3 => 7,
            4 => 8,
            5 => 11,
            6 => 11,
            7 => 10,
            _ => null,
        };
    }

    /// <summary>Standard tavern curve: tier 2 by turn 2, 3 by 4, 4 by 7, 5 by 10, 6 after.</summary>
    private static bool BehindCurve(BattlegroundsView view)
    {
        var expected = view.Turn switch
        {
            <= 2 => 2,
            <= 4 => 3,
            <= 7 => 4,
            <= 10 => 5,
            _ => 6,
        };
        return view.TechLevel < Math.Min(expected, view.MaxTechLevel);
    }

    private static bool BoardUnderTiered(BattlegroundsView view)
    {
        var known = view.Board.Where(m => m.TechLevel > 0).ToList();
        return known.Count >= 3 && known.Average(m => m.TechLevel) <= view.TechLevel - 1.5;
    }

    /// <summary>Leveling costs tempo: only safe while healthy, early, or already in the top half.</summary>
    private static bool SafeToLevel(BattlegroundsView view)
    {
        var durability = view.Health + view.Armor;
        if (durability <= 8)
        {
            return false;
        }

        return view.Turn <= 5 || durability >= 25 || (durability >= 15 && view.Place is > 0 and <= 4);
    }
}
