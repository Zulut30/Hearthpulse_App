using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>Scores the cards on offer (tavern or a Discover-style choice) against the target comp, the board and community data.</summary>
public sealed partial class BattlegroundsAdvisor
{
    /// <summary>A card on offer: a tavern minion, a tavern spell, or one option of a Discover-style choice.</summary>
    private sealed record Offer(string CardId, string Name, int TechLevel, string CardType, int Cost);

    /// <summary>
    /// Scores cards on offer (the tavern, or any Discover-style choice) against the target comp and the board,
    /// then shifts each score by community data: how players who played the card around this turn placed,
    /// compared with the other cards on offer. <paramref name="dataWeight"/> is the score per placement step.
    /// </summary>
    private static IReadOnlyList<ShopPick> ScoreOffer(
        BattlegroundsView view, IReadOnlyList<Offer> offer, CompAdvice? target, Dictionary<string, int> owned, CardDb cards,
        BattlegroundsMeta meta, int bracket, double dataWeight)
    {
        if (offer.Count == 0)
        {
            return Array.Empty<ShopPick>();
        }

        var dominantTribe = view.Board
            .SelectMany(m => cards.Get(m.CardId)?.Races ?? Array.Empty<string>())
            .Where(r => r != "ALL")
            .GroupBy(r => r).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        var picks = new List<ShopPick>();
        var slot = 0;
        foreach (var item in offer)
        {
            var id = NormalId(item.CardId, cards);
            var card = cards.Get(id);
            var tier = card?.TechLevel is > 0 and var t ? t : item.TechLevel;
            var (score, reasons) = item.CardType.Contains("SPELL", StringComparison.OrdinalIgnoreCase)
                ? ScoreSpell(view, card, tier, item.Cost, target, dominantTribe)
                : ScoreMinion(view, id, card, tier, target, owned, dominantTribe);
            var data = meta.CardAtTurn(id, Math.Max(1, view.Turn), bracket);
            picks.Add(new ShopPick(item.CardId, item.Name, tier, score, string.Join(" · ", reasons), false, slot++)
            {
                Placement = data?.Placement,
                PlacementGames = data?.Games ?? 0,
                Cost = BuyCost(item, card),
                CardType = item.CardType,
            });
        }

        // Community data only ranks cards against each other, so it needs at least two cards with data.
        var withData = picks.Where(p => p.Placement is not null).ToList();
        if (withData.Count >= 2)
        {
            var mean = withData.Average(p => p.Placement!.Value);
            var cap = dataWeight / 2;
            picks = picks
                .Select(p => p.Placement is { } placement
                    ? p with { Score = p.Score + (int)Math.Round(Math.Clamp((mean - placement) * dataWeight, -cap, cap)) }
                    : p)
                .ToList();
        }

        // Without data or any comp signal every option looks the same: show no pick rather than a guess.
        var decisive = withData.Count >= 2 || picks.Select(p => p.Score).Distinct().Count() > 1;
        var bestScore = picks.Max(p => p.Score);
        return picks
            .OrderByDescending(p => p.Score)
            .Select(p => p with { Best = decisive && p.Score == bestScore })
            .ToList();
    }

    private static (int Score, List<string> Reasons) ScoreMinion(
        BattlegroundsView view, string id, Card? card, int tier, CompAdvice? target, Dictionary<string, int> owned, string? dominantTribe)
    {
        var score = 0;
        var reasons = new List<string>();
        if (owned.GetValueOrDefault(id) >= 2)
        {
            score += 70;
            reasons.Add("Triple");
        }
        else if (owned.GetValueOrDefault(id) == 1)
        {
            score += 12;
            reasons.Add("Pair");
        }

        var compCard = target?.Cards.FirstOrDefault(c => c.CardId == id);
        if (compCard is not null)
        {
            var (points, label) = compCard.Status switch
            {
                "CORE" => (60, "Core"),
                "ADDON" => (30, "Add-on"),
                _ => (15, "Cycle"),
            };
            score += points;
            reasons.Add($"{label} · {target!.Name}");
        }

        var races = card?.Races ?? Array.Empty<string>();
        if (target is not null && target.Tribes.Count > 0 && races.Any(r => r == "ALL" || target.Tribes.Contains(r)))
        {
            score += 15;
            if (compCard is null) reasons.Add("Fits tribe");
        }
        else if (dominantTribe is not null && races.Any(r => r == "ALL" || r == dominantTribe))
        {
            score += 8;
            reasons.Add("Board synergy");
        }

        score += tier * 3;
        if (reasons.Count == 0)
        {
            reasons.Add(tier >= view.TechLevel ? "Best stats available" : "Filler");
        }

        return (score, reasons);
    }

    /// <summary>
    /// Scores a tavern spell by what its text does: economy early, stats when there is a board to buff, removal,
    /// scaling and value, cheaper spells first, plus tribe/comp fit.
    /// </summary>
    private static (int Score, List<string> Reasons) ScoreSpell(
        BattlegroundsView view, Card? card, int tier, int cost, CompAdvice? target, string? dominantTribe)
    {
        var score = tier * 3;
        var reasons = new List<string>();
        var text = (card?.Text ?? string.Empty).ToLowerInvariant();
        var board = view.Board.Count;

        if (cost is > 0 and < MinionCost)
        {
            score += (MinionCost - cost) * 2;
        }

        if (text.Contains("gold", StringComparison.Ordinal) || text.Contains("refresh", StringComparison.Ordinal))
        {
            score += view.Turn <= 8 ? 14 : 6;
            reasons.Add("Spell · economy");
        }

        if (text.Contains("stats", StringComparison.Ordinal)
            || (text.Contains('+') && (text.Contains("attack", StringComparison.Ordinal) || text.Contains("health", StringComparison.Ordinal))))
        {
            score += board >= 4 ? 12 : 8;
            reasons.Add(board >= 4 ? "Spell · buffs your board" : "Spell · stats");
        }

        if (ContainsAny(text, "permanent", "each turn", "every turn", "this game", "for the rest of the game"))
        {
            score += 10;
            reasons.Add("Spell · scaling");
        }

        if (text.Contains("discover", StringComparison.Ordinal))
        {
            score += 8;
            reasons.Add("Spell · discover");
        }

        if (ContainsAny(text, "destroy", "poisonous", "venomous", "deal ", "damage"))
        {
            score += 8;
            reasons.Add("Spell · removal");
        }

        if (ContainsAny(text, "taunt", "divine shield", "shield"))
        {
            score += 6;
        }

        if (ContainsAny(text, "summon", "get a", "get two"))
        {
            score += 6;
        }

        var tribes = target?.Tribes ?? Array.Empty<string>();
        if (tribes.Count > 0 && tribes.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            score += 8;
            reasons.Add($"Spell · fits {target!.Name}");
        }
        else if (dominantTribe is not null && text.Contains(dominantTribe, StringComparison.OrdinalIgnoreCase))
        {
            score += 6;
            reasons.Add("Spell · board synergy");
        }

        if (reasons.Count == 0)
        {
            reasons.Add($"Spell · T{tier}");
        }

        return (score, reasons);
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.Ordinal));

    /// <summary>Gold to buy an offer: live shop cost first, then the card's base cost, else the 3-gold default.</summary>
    private static int BuyCost(Offer offer, Card? card) =>
        offer.Cost > 0 ? offer.Cost : card?.Cost is > 0 and var cost ? cost : MinionCost;
}
