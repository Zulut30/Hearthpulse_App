#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Updates;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    private void RebuildMeta()
    {
        if (_meta is not { } meta || _cards.Count == 0)
        {
            return;
        }

        // Only the brackets the source has are offered (the API: top 50/20/5/1%, no all-ranks hero slice).
        var brackets = meta.HeroBrackets;
        if (!brackets.Contains(Bracket))
        {
            Bracket = meta.WidestBracket;
        }

        BracketOptions = brackets.Select((b, i) => new BracketOption(b, b >= 100 ? Loc.T("Main.Bracket.All") : i == 0 ? Loc.F("Main.Bracket.Top", b / 100.0) : (b / 100.0).ToString("P0", CultureInfo.CurrentCulture), b == Bracket, SetBracket)).ToList();

        var personal = MyHeroes.ToDictionary(h => h.CardId);
        var heroes = meta.HeroesByBracket.GetValueOrDefault(Bracket) ?? new Dictionary<string, MetaHero>();

        var ranked = heroes.Values.Where(h => h.DataPoints == 0 || h.DataPoints >= meta.MinHeroGames).OrderBy(h => h.AveragePosition).ToList();
        var best = ranked.Count > 0 ? ranked[0].AveragePosition : 3.5;
        var worst = ranked.Count > 0 ? ranked[^1].AveragePosition : 5.5;
        HeroTiers = ranked.Select((h, i) => new MetaHeroRow(
                i + 1,
                h.HeroCardId,
                _cards.NameOf(h.HeroCardId),
                BattlegroundsAdvisor.TierOf(h.AveragePosition),
                h.AveragePosition.ToString("0.00", CultureInfo.CurrentCulture),
                h.AveragePosition,
                h.PickRate.ToString("P0", CultureInfo.CurrentCulture),
                h.DataPoints > 0 ? h.DataPoints.ToString("N0", CultureInfo.CurrentCulture) : "—",
                personal.TryGetValue(h.HeroCardId, out var p) ? $"{p.Games} · {p.AveragePlace}" : string.Empty,
                PlacementWidth(h.AveragePosition, best, worst),
                h.Top4Rate > 0 ? h.Top4Rate.ToString("P0", CultureInfo.CurrentCulture) : "—",
                h.FirstRate > 0 ? h.FirstRate.ToString("P0", CultureInfo.CurrentCulture) : "—",
                h.Top4Rate,
                h.FirstRate,
                h.PickRate,
                h.DataPoints))
            .ToList();

        var tribeRows = UseTribeRows(meta);
        if (tribeRows)
        {
            RebuildTribeRows(meta);
        }
        else
        {
            // Downloaded stats have real comps instead of tribes.
            var comps = meta.Comps
                .Select(c => (Comp: c, Placement: c.PlacementAtMmr.TryGetValue(Bracket, out var p) ? p : c.AveragePlacement))
                .OrderBy(c => c.Placement)
                .ToList();
            var cBest = comps.Count > 0 ? comps[0].Placement : 3;
            var cWorst = comps.Count > 0 ? comps[^1].Placement : 5;
            CompTiers = comps.Select((c, i) => new MetaCompRow(
                    i + 1,
                    c.Comp.Name,
                    string.Join(" · ", c.Comp.Tribes.Select(t => Races.Display(t, _cards.Locale))),
                    string.IsNullOrEmpty(c.Comp.PowerLevel) ? "–" : c.Comp.PowerLevel,
                    c.Comp.Difficulty,
                    c.Placement.ToString("0.00", CultureInfo.CurrentCulture),
                    PlacementWidth(c.Placement, cBest, cWorst),
                    c.Comp.WhenToCommit ?? string.Empty,
                    string.Join(", ", c.Comp.Cards.Where(x => x.Status == "CORE").Select(x => _cards.NameOf(x.CardId)))))
                .ToList();
        }

        OnCompRowsRebuilt(tribeRows);

        var trinkets = meta.Trinkets.Values
            .Select(t => (Trinket: t, Stats: meta.Trinket(t.CardId, Bracket)))
            .Where(t => t.Stats is not null)
            .OrderBy(t => t.Stats!.Value.Placement)
            .ToList();
        var tBest = trinkets.Count > 0 ? trinkets[0].Stats!.Value.Placement : 3;
        var tWorst = trinkets.Count > 0 ? trinkets[^1].Stats!.Value.Placement : 5;
        TrinketTiers = trinkets.Select((t, i) => new MetaTrinketRow(
                i + 1,
                t.Trinket.CardId,
                _cards.NameOf(t.Trinket.CardId) is { Length: > 0 } n ? n : t.Trinket.CardId,
                BattlegroundsAdvisor.TierOf(t.Stats!.Value.Placement),
                t.Stats.Value.Placement.ToString("0.00", CultureInfo.CurrentCulture),
                t.Stats.Value.PickRate.ToString("P0", CultureInfo.CurrentCulture),
                t.Stats.Value.DataPoints.ToString("N0", CultureInfo.CurrentCulture),
                PlacementWidth(t.Stats.Value.Placement, tBest, tWorst)))
            .ToList();

        CompEmptyText = meta.PoolPending(Loc.T("Main.Bg.PoolWhat.Comps"));
        TrinketEmptyText = meta.PoolPending(Loc.T("Main.Bg.PoolWhat.Trinkets"));

        // Cards are only comparable within one tavern tier: late, high-tier cards always look better overall.
        var cardBracket = meta.CardsByBracket.ContainsKey(Bracket) ? Bracket : 100;
        var cardStats = meta.CardsByBracket.GetValueOrDefault(cardBracket);
        var cards = (cardStats?.Values ?? Enumerable.Empty<MetaCard>())
            .Where(c => c.DataPoints >= BattlegroundsMeta.MinCardGames)
            .Select(c => (Stats: c, Card: _cards.Get(c.CardId)))
            .Where(c => c.Card is not null && (CardTier == 0
                ? c.Card.Type == "BATTLEGROUND_SPELL" && c.Card.IsBattlegroundsPoolSpell
                : c.Card.Type == "MINION" && c.Card.IsBattlegroundsPoolMinion && c.Card.TechLevel == CardTier))
            .OrderBy(c => c.Stats.AveragePlacement)
            .ToList();
        var kBest = cards.Count > 0 ? cards[0].Stats.AveragePlacement : 3;
        var kWorst = cards.Count > 0 ? cards[^1].Stats.AveragePlacement : 5;
        CardTiers = cards.Select((c, i) => new MetaCardRow(
                i + 1,
                c.Stats.CardId,
                c.Card!.Name,
                c.Card.Type == "MINION" ? string.Join(" · ", c.Card.Races.Select(r => Races.Display(r, _cards.Locale))) : Loc.T("Main.Bg.TavernSpell"),
                c.Card.TechLevel > 0 ? c.Card.TechLevel.ToString(CultureInfo.CurrentCulture) : "–",
                c.Stats.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                c.Stats.DataPoints.ToString("N0", CultureInfo.CurrentCulture),
                PlacementWidth(c.Stats.AveragePlacement, kBest, kWorst)))
            .ToList();

        // The API publishes tavern spells nowhere and has fewer minion brackets than hero brackets: both are said, not hidden.
        CardEmptyText = CardTier == 0 ? meta.PoolPending(Loc.T("Main.Bg.PoolWhat.Spells")) : Loc.T("Main.Bg.NoCardData");
        var bracketText = BattlegroundsMeta.BracketLabel(Bracket)
            + (cardBracket != Bracket && meta.CardsByBracket.Count > 0 ? Loc.F("Main.Bg.CardsBracket", BattlegroundsMeta.BracketLabel(cardBracket)) : string.Empty);
        MetaUpdated = Loc.F("Main.Bg.MetaUpdated", bracketText, meta.Updated.ToLocalTime());
    }
}
