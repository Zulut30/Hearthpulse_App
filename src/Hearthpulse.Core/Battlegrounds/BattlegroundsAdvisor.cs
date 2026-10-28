using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>
/// Turns community statistics plus the live lobby into recommendations: which hero to pick,
/// which composition to aim for, and which shop minion to buy next.
/// </summary>
public sealed partial class BattlegroundsAdvisor
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

    /// <summary>
    /// The average placement a <see cref="TierOf"/> tier stays under ("S" -> 3.75); for D, the one it starts at (4.65).
    /// </summary>
    public static double TierBound(string tier) => tier switch
    {
        "S" => 3.75,
        "A" => 4.05,
        "B" => 4.35,
        _ => 4.65,
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
        var hasCompCards = meta.Comps.Any(c => c.Cards.Count > 0);
        var viable = meta.Comps
            .Where(c => !hasCompCards || c.Cards.Count > 0)
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

        var percentile = BattlegroundsMeta.BracketLabel(bracket);
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
            MetaSource = $"{meta.Source} · {percentile} · {meta.Updated.ToString("dd MMM HH:mm", Loc.DateCulture)}"
                + (meta.SharedGames > 0 ? " · " + Loc.P("Core.Bg.SharedGames", meta.SharedGames) : string.Empty),
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
            ? Loc.T("Core.Bg.CompletesTriple")
            : first.Reason.Contains("Core", StringComparison.Ordinal)
                ? target is null ? Loc.T("Core.Bg.Freeze.Core") : Loc.F("Core.Bg.Freeze.CoreFor", target.Name)
                : first.Reason.Contains("Pair", StringComparison.Ordinal)
                    ? Loc.T("Core.Bg.Freeze.Pair")
                    : Loc.T("Core.Bg.Freeze.Strong");
        var names = string.Join(Loc.T("Core.Bg.And"), keep.Select(p => p.Name));
        var missing = Math.Max(first.Cost, MinionCost) - view.Gold;
        return Loc.F("Core.Bg.Freeze", names, why, missing);
    }

    private static bool IsWorthFreezing(ShopPick pick) =>
        pick.Score >= StrongShopScore
        || pick.Reason.Contains("Triple", StringComparison.Ordinal)
        || pick.Reason.Contains("Core", StringComparison.Ordinal)
        || pick.Reason.Contains("Pair", StringComparison.Ordinal);

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
            ? Loc.F("Core.Bg.DarkGift.BuyFirst", best.Name)
            : Loc.F("Core.Bg.DarkGift.Spend", cost);
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
