using Hearthlens.Core.Cards;
using Hearthlens.Core.Tracking;

namespace Hearthlens.Core.Battlegrounds;

/// <summary>
/// Turns community statistics plus the live lobby into recommendations: which hero to pick,
/// which composition to aim for, and which shop minion to buy next.
/// </summary>
public sealed class BattlegroundsAdvisor
{
    private static readonly Dictionary<string, int> RaceIds = Races.ById.ToDictionary(kv => kv.Value, kv => kv.Key);

    private const int MinionCost = 3;
    private const int SellRefund = 1;
    private const int StrongShopScore = 40;

    /// <summary>Score at which a tavern spell is worth recommending on its own.</summary>
    private const int StrongSpellScore = 25;

    public BattlegroundsMeta? Meta { get; set; }

    /// <summary>
    /// Who sets the target comp. <see cref="CompMode.Manual"/>: only <see cref="PinnedCompId"/>, never the advisor, so
    /// without a choice there is no target and the tavern and Discover scores get no comp bonus.
    /// <see cref="CompMode.Auto"/>: the advisor picks one, and <see cref="PinnedCompId"/> overrides it.
    /// </summary>
    public CompMode CompMode { get; set; } = CompMode.Manual;

    /// <summary>Composition the player chose from the UI; null leaves no target (Manual) or lets the advisor choose (Auto).</summary>
    public string? PinnedCompId { get; set; }

    /// <summary>The tribe the player wants to build around (race key); its comps come first when it is in the lobby.</summary>
    public string? CompFocus { get; set; }

    private const int ShownComps = 7;

    // The comp the advisor chose last time (Auto), so the suggestion does not flip on every small change.
    private string? _autoTargetId;
    private string? _autoTargetHero;
    private int _autoTargetTurn;

    public static string TierOf(double adjustedPosition) => adjustedPosition switch
    {
        < 3.75 => "S",
        < 4.05 => "A",
        < 4.35 => "B",
        < 4.65 => "C",
        _ => "D",
    };

    public BattlegroundsView Enrich(BattlegroundsView view, int? rating, CardDb cards)
    {
        var meta = Meta;
        if (meta is null)
        {
            return view;
        }

        var bracket = meta.BracketFor(rating);
        var lobbyKnown = view.Races.Count >= 4;
        var lobbyIds = view.Races.Select(r => RaceIds.GetValueOrDefault(r)).Where(id => id > 0).ToHashSet();
        var viable = meta.Comps
            .Where(c => !lobbyKnown || c.Tribes.All(view.Races.Contains))
            .Where(c => Playable(c, cards, view.IsDuos))
            .ToList();

        var options = RankHeroes(view, cards, meta, bracket, lobbyKnown, lobbyIds, viable);

        IReadOnlyList<CompAdvice> comps = Array.Empty<CompAdvice>();
        IReadOnlyList<ShopPick> shop = Array.Empty<ShopPick>();
        IReadOnlyList<ShopPick> discover = Array.Empty<ShopPick>();
        IReadOnlyList<string> pairs = Array.Empty<string>();
        ShopPlan? plan = null;
        CompAdvice? target = null;
        if (view.HeroCardId is { } hero)
        {
            var owned = OwnedCards(view, cards);
            if (hero != _autoTargetHero || view.Turn < _autoTargetTurn)
            {
                _autoTargetId = null;
            }

            _autoTargetHero = hero;
            _autoTargetTurn = view.Turn;
            comps = RankComps(view, viable, cards.BaseHeroId(hero), bracket, owned, cards, lobbyKnown);
            target = comps.FirstOrDefault(c => c.IsTarget);
            shop = ScoreOffer(view, view.Shop.Select(m => new Offer(m.CardId, m.Name, m.TechLevel, m.CardType, m.Cost)).ToList(),
                target, owned, cards, meta, bracket, dataWeight: 50);
            discover = ScoreOffer(view, view.DiscoverOptions.Select(d => new Offer(d.CardId, d.Name, d.TechLevel, d.CardType, 0)).ToList(),
                target, owned, cards, meta, bracket, dataWeight: 80);
            pairs = owned.Where(kv => kv.Value == 2).Select(kv => cards.NameOf(kv.Key)).Where(n => n.Length > 0).ToList();
            plan = PlanShop(view, target, shop, cards);
        }

        var percentile = bracket == 100 ? "all ranks" : $"top {bracket}%";
        return view with
        {
            HeroOptions = options,
            Comps = comps,
            ShopAdvice = shop,
            BestSpell = shop.Where(p => p.IsSpell && p.Score >= StrongSpellScore).OrderByDescending(p => p.Score).FirstOrDefault(),
            TrinketOptions = RankTrinkets(view.TrinketOptions, meta, bracket),
            DarkGiftAdvice = DarkGiftTip(view, shop),
            DiscoverAdvice = discover,
            FreezeAdvice = FreezeTip(view, shop, target),
            Pairs = pairs,
            ShopPlan = plan,
            MetaSource = $"{BattlegroundsMeta.Attribution} · {percentile} · {meta.Updated:dd MMM HH:mm}",
        };
    }

    private static List<BgHeroOption> RankHeroes(
        BattlegroundsView view, CardDb cards, BattlegroundsMeta meta, int bracket,
        bool lobbyKnown, HashSet<int> lobbyIds, List<MetaComp> viable)
    {
        var options = new List<(BgHeroOption Option, double Score)>();
        foreach (var option in view.HeroOptions)
        {
            var baseId = cards.BaseHeroId(option.CardId);
            HeroMeta? heroMeta = null;
            if (meta.Hero(baseId, bracket) is { } stats)
            {
                var adjusted = stats.AveragePosition + (lobbyKnown ? lobbyIds.Sum(t => stats.TribeImpact.GetValueOrDefault(t)) : 0);
                heroMeta = new HeroMeta(stats.AveragePosition, adjusted, stats.PickRate, stats.DataPoints, TierOf(adjusted));
            }

            var topComps = viable
                .Select(c => (c.Name, Placement: HeroCompPlacement(c, baseId, bracket)))
                .OrderBy(c => c.Placement)
                .Take(2)
                .Select(c => c.Name)
                .ToList();

            // Community data dominates; personal results nudge it once there is a sample.
            var score = heroMeta?.AdjustedPosition ?? 4.5;
            if (option.Stats is { Games: >= 5 } personal)
            {
                score = (score * 0.75) + (personal.AveragePlace * 0.25);
            }

            options.Add((option with { Meta = heroMeta, TopComps = topComps }, score));
        }

        var best = options.Where(o => o.Option.Meta is not null || o.Option.Stats is not null)
            .OrderBy(o => o.Score).Select(o => o.Option.CardId).FirstOrDefault();
        return options.Select(o => o.Option with { Recommended = o.Option.CardId == best }).ToList();
    }

    private static IReadOnlyList<BgTrinketOption> RankTrinkets(IReadOnlyList<BgTrinketOption> offer, BattlegroundsMeta meta, int bracket)
    {
        if (offer.Count == 0)
        {
            return offer;
        }

        var ranked = offer
            .Select(t => meta.Trinket(t.CardId, bracket) is { } stats
                ? t with { Meta = new TrinketMeta(stats.Placement, stats.PickRate, stats.DataPoints, TierOf(stats.Placement)) }
                : t)
            .ToList();
        var best = ranked.Where(t => t.Meta is not null).MinBy(t => t.Meta!.AveragePlacement)?.CardId;
        return ranked.Select(t => t with { Recommended = t.CardId == best }).ToList();
    }

    /// <summary>
    /// A key card is in the tavern and this turn's gold cannot pay for it: freezing keeps the shop for next turn.
    /// A triple, a core card or a second copy always qualifies, and so does a card strong enough that rerolling it
    /// away is a loss. Advice needs known gold and an open tavern — with gold unknown nothing is claimed.
    /// </summary>
    private static string FreezeTip(BattlegroundsView view, IReadOnlyList<ShopPick> shop, CompAdvice? target)
    {
        if (view.InCombat || view.IsHeroSelection || view.Gold <= 0 || view.DiscoverOptions.Count > 0
            || view.TrinketOptions.Count > 0)
        {
            return string.Empty;
        }

        var keep = shop
            .Where(p => IsWorthFreezing(p) && view.Gold < Math.Max(MinionCost, p.Cost))
            .OrderByDescending(p => p.Score)
            .ThenBy(p => p.Cost)
            .Take(2)
            .ToList();
        if (keep.Count == 0)
        {
            return string.Empty;
        }

        var first = keep[0];
        var why = first.Reason.Contains("Triple", StringComparison.Ordinal)
            ? "completes a triple"
            : first.Reason.Contains("Core", StringComparison.Ordinal)
                ? target is null ? "is a core card" : $"is core for {target.Name}"
                : first.Reason.Contains("Pair", StringComparison.Ordinal)
                    ? "gives you a pair"
                    : "is too strong to reroll away";
        var names = string.Join(" and ", keep.Select(p => p.Name));
        var missing = Math.Max(first.Cost, MinionCost) - view.Gold;
        return $"Freeze: keep {names} — {why}; {missing} more gold next turn";
    }

    private static bool IsWorthFreezing(ShopPick pick) =>
        pick.Score >= StrongShopScore
        || pick.Reason.Contains("Triple", StringComparison.Ordinal)
        || pick.Reason.Contains("Core", StringComparison.Ordinal)
        || pick.Reason.Contains("Pair", StringComparison.Ordinal);

    /// <summary>
    /// A comp is worth showing when at least one of its cards is in this patch's pool (and, outside Duos, is not a
    /// Duos-exclusive card). Comps built only from cards the patch rotated out cannot be played.
    /// </summary>
    private static bool Playable(MetaComp comp, CardDb cards, bool duos) =>
        comp.Cards.Count == 0 || comp.Cards.Any(card => Available(card.CardId, cards, duos));

    private static bool Available(string cardId, CardDb cards, bool duos) =>
        cards.InBattlegroundsPool(cardId)
        && (duos || cards.Get(cardId)?.IsBattlegroundsDuosExclusive != true);

    /// <summary>
    /// The Dark Gift button discovers a minion carrying a Dark Gift. It beats a filler buy, but a shop
    /// card that completes the plan (core card, triple) is still the better use of the gold.
    /// </summary>
    private static string DarkGiftTip(BattlegroundsView view, IReadOnlyList<ShopPick> shop)
    {
        if (!view.DarkGiftReady || view.InCombat || view.Gold <= 0)
        {
            return string.Empty;
        }

        var cost = view.DarkGiftCost > 0 ? view.DarkGiftCost : 3;
        if (view.Gold < cost)
        {
            return string.Empty;
        }

        var best = shop.FirstOrDefault(p => p.Best);
        return best is { Score: >= 60 }
            ? $"Dark Gift: buy {best.Name} first, then discover with the rest"
            : $"Dark Gift: spend {cost} gold — a gifted minion beats this shop";
    }

    private static double HeroCompPlacement(MetaComp comp, string baseHeroId, int bracket)
    {
        var general = comp.PlacementAtMmr.TryGetValue(bracket, out var p) ? p : comp.AveragePlacement;
        if (comp.HeroPlacement.TryGetValue(baseHeroId, out var hero) && hero.DataPoints >= 40)
        {
            // Shrink small hero samples toward the comp's general placement.
            var weight = Math.Min(1.0, hero.DataPoints / 200.0);
            return (hero.Placement * weight) + (general * (1 - weight));
        }

        return general;
    }

    private IReadOnlyList<CompAdvice> RankComps(
        BattlegroundsView view, List<MetaComp> viable, string baseHero, int bracket, Dictionary<string, int> owned, CardDb cards, bool lobbyKnown)
    {
        var focus = CompFocus is { Length: > 0 } wanted && view.Races.Contains(wanted) ? wanted : null;
        var ranked = viable
            .Select(c =>
            {
                var core = c.Cards.Count(card => card.Status == "CORE");
                var ownedCore = c.Cards.Count(card => card.Status == "CORE" && owned.ContainsKey(card.CardId));
                var ownedAddon = c.Cards.Count(card => card.Status != "CORE" && owned.ContainsKey(card.CardId));
                var placement = HeroCompPlacement(c, baseHero, bracket);
                // Most comps sit within a few tenths of a place of each other, so what you already own has to weigh more
                // than that gap: half of a comp's core moves it past almost any other, and the list follows your board.
                var fit = core == 0 ? 0 : ownedCore / (double)core;
                var focused = focus is null ? 0 : c.Tribes.Contains(focus) ? -1.0 : 0.5;
                return (Comp: c, Placement: placement, OwnedCore: ownedCore, Weight: (ownedCore * 2) + ownedAddon,
                    Score: placement - (0.35 * ownedCore) - (0.1 * ownedAddon) - (0.6 * fit) + focused);
            })
            .OrderBy(x => x.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return Array.Empty<CompAdvice>();
        }

        // The player's choice comes first. In Manual mode it is the only possible target: a comp this lobby cannot
        // play (or no choice at all) leaves no target, and the choice itself is kept for later games.
        var target = ranked.FirstOrDefault(x => x.Comp.Id == PinnedCompId);
        if (CompMode == CompMode.Manual)
        {
            _autoTargetId = null;
        }
        else
        {
            if (target.Comp is null)
            {
                target = ranked.Where(x => x.Weight >= 2 && x.Comp.Cards.Count > 0).OrderByDescending(x => x.Weight).ThenBy(x => x.Score).FirstOrDefault();
            }

            if (target.Comp is null)
            {
                target = ranked.FirstOrDefault(x => x.Comp.Cards.Count > 0);
            }

            // Stay on the previous suggestion unless the new one is ahead by at least one core card.
            var previous = ranked.FirstOrDefault(x => x.Comp.Id == _autoTargetId);
            if (PinnedCompId is null && previous.Comp is not null && target.Comp is not null && previous.Comp.Id != target.Comp.Id
                && target.Weight - previous.Weight < 2)
            {
                target = previous;
            }

            if (PinnedCompId is null)
            {
                _autoTargetId = target.Comp?.Id;
            }
        }

        // At most two comps of the same tribes unless you already own part of them, so the list offers real choices
        // instead of five versions of the strongest tribe.
        var shown = ranked.Take(0).ToList();
        var perTribes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var x in ranked)
        {
            var key = string.Join('+', x.Comp.Tribes.Order(StringComparer.Ordinal));
            perTribes.TryGetValue(key, out var count);
            if (count >= 2 && x.Weight == 0)
            {
                continue;
            }

            perTribes[key] = count + 1;
            shown.Add(x);
            if (shown.Count == ShownComps)
            {
                break;
            }
        }

        if (target.Comp is not null && !shown.Any(x => x.Comp.Id == target.Comp.Id))
        {
            shown[^1] = target;
        }

        return shown.Select(x => new CompAdvice(
                x.Comp.Id,
                x.Comp.Name,
                x.Comp.PowerLevel,
                x.Comp.Difficulty,
                x.Comp.PlacementAtMmr.TryGetValue(bracket, out var p) ? p : x.Comp.AveragePlacement,
                x.Comp.HeroPlacement.TryGetValue(baseHero, out var h) && h.DataPoints >= 40 ? h.Placement : null,
                x.Comp.Tribes,
                x.Comp.Cards
                    .Where(card => Available(card.CardId, cards, view.IsDuos))
                    .Select(card => new CompCard(card.CardId, cards.NameOf(card.CardId), card.Status, cards.Get(card.CardId)?.TechLevel ?? 0, owned.ContainsKey(card.CardId)))
                    .OrderBy(card => card.Status switch { "CORE" => 0, "ADDON" => 1, _ => 2 }).ThenBy(card => card.TechLevel)
                    .ToList(),
                x.Comp.WhenToCommit,
                x.Comp.Tip,
                x.OwnedCore,
                target.Comp is not null && x.Comp.Id == target.Comp.Id,
                x.Comp.Id == PinnedCompId)
            {
                Why = WhyComp(x.Comp, x.OwnedCore, x.Comp.Id == PinnedCompId, CompMode, lobbyKnown, owned, baseHero),
                LookFor = x.Comp.Cards
                    .Where(card => card.Status == "CORE" && !owned.ContainsKey(card.CardId))
                    .Where(card => Available(card.CardId, cards, view.IsDuos))
                    .Select(card => (Name: cards.NameOf(card.CardId), Tier: cards.Get(card.CardId)?.TechLevel ?? 0))
                    .Where(card => card.Name.Length > 0 && card.Tier > 0 && card.Tier <= Math.Max(view.TechLevel, 1) + 1)
                    .OrderBy(card => card.Tier)
                    .Take(3)
                    .Select(card => $"{card.Name} (T{card.Tier})")
                    .ToList(),
            })
            .ToList();
    }

    private static string WhyComp(MetaComp comp, int ownedCore, bool pinned, CompMode mode, bool lobbyKnown, Dictionary<string, int> owned, string baseHero)
    {
        if (pinned)
        {
            return mode == CompMode.Manual
                ? "Your choice: tavern and Discover hints count its cards"
                : "Locked by you: the advisor will not switch away from it";
        }

        var core = comp.Cards.Count(card => card.Status == "CORE");
        var addons = comp.Cards.Count(card => card.Status != "CORE" && owned.ContainsKey(card.CardId));
        if (ownedCore > 0)
        {
            return $"You already have {ownedCore} of {core} core cards" + (addons > 0 ? $" and {addons} support card{(addons == 1 ? string.Empty : "s")}" : string.Empty);
        }

        if (addons > 0)
        {
            return $"You have {addons} of its support cards";
        }

        var hero = comp.HeroPlacement.TryGetValue(baseHero, out var h) && h.DataPoints >= 40 ? " with your hero" : string.Empty;
        return lobbyKnown
            ? $"Best average placement{hero} with this lobby's tribes"
            : $"Best average placement{hero} (lobby tribes not known yet)";
    }

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
                $"Sell {worst.Value.Name}",
                $"Board full · +{SellRefund} gold, {gold} left",
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
                $"Buy {best.Name}",
                $"{BuyDetail(best, target)} · {price} gold, {gold} left",
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
            var spend = cost == 0 ? "free" : $"{cost} gold";
            steps.Add(new ShopStep(
                ShopAction.Level,
                $"Level to tier {view.TechLevel + 1}",
                $"{LevelReason(view, target, missingNextCore, behind, underTiered, weakShop)} · {spend}, {gold} left",
                cost,
                gold));
        }

        void AddRoll()
        {
            var cost = view.RerollCost;
            if (gold < cost)
            {
                return;
            }

            gold -= cost;
            var lookFor = target is null ? "a better board" : $"{target.Name} cards";
            steps.Add(new ShopStep(
                ShopAction.Roll,
                "Roll the tavern",
                $"No strong buy · look for {lookFor} · {cost} gold, {gold} left",
                cost,
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
            "Hold this turn",
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
            return $"Save your {gold} gold — tier {view.TechLevel + 1} costs {cost}";
        }

        if (boardFull)
        {
            return "Board is full and every minion is worth keeping — hold or roll";
        }

        return SafeToLevel(view)
            ? "No card is worth its gold this turn"
            : $"Too risky to level on {view.Health + view.Armor} health — keep the gold for next turn";
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
            ? "is the best tavern spell"
            : pick.Reason.Contains("Triple", StringComparison.Ordinal)
            ? "completes a triple"
            : pick.Reason.Contains("Core", StringComparison.Ordinal)
                ? $"is a core card{ForComp(target)}"
                : pick.Reason.Contains("Pair", StringComparison.Ordinal)
                    ? "adds a pair"
                    : "is the best card in the tavern";

    private static string LevelReason(
        BattlegroundsView view, CompAdvice? target,
        bool missingNextCore, bool behind, bool underTiered, bool weakShop)
    {
        var next = view.TechLevel + 1;
        var reasons = new List<string>();
        if (missingNextCore && target is not null)
        {
            reasons.Add($"unlocks {target.Name} core at T{next}");
        }
        else if (target is not null)
        {
            reasons.Add($"moves toward {target.Name}");
        }

        if (behind)
        {
            reasons.Add($"behind the curve (T{view.TechLevel} on turn {view.Turn})");
        }

        if (underTiered)
        {
            reasons.Add("board is under-tiered");
        }

        if (weakShop)
        {
            reasons.Add("shop has no strong buy");
        }

        if (reasons.Count == 0)
        {
            reasons.Add("best use of this turn's gold");
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
            : $"{steps.Count} steps · {total} gold net · {steps[^1].GoldLeft} left";
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

    private static string ForComp(CompAdvice? target) => target is null ? string.Empty : $" for {target.Name}";

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

    private static Dictionary<string, int> OwnedCards(BattlegroundsView view, CardDb cards)
    {
        var owned = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in view.Board.Select(m => m.CardId).Concat(view.Hand))
        {
            var normal = NormalId(id, cards);
            // A golden copy already consumed its triple: record it (comp "owned" checks) without adding a copy
            // (pair/triple checks count normal copies only).
            owned[normal] = owned.GetValueOrDefault(normal) + (normal == id ? 1 : 0);
        }

        return owned;
    }

    private static string NormalId(string cardId, CardDb cards) =>
        cards.Get(cardId) is { BattlegroundsNormalDbfId: > 0 } golden && cards.Get(golden.BattlegroundsNormalDbfId) is { } normal
            ? normal.Id
            : cardId;
}
