using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

public sealed partial class OverlayViewModel
{
    private void UpdateBattlegrounds(BattlegroundsView view, LiveSnapshot snapshot)
    {
        Turn = view.Turn;
        IsDuos = view.IsDuos;
        Phase = view.IsHeroSelection ? Loc.T("Overlay.Phase.HeroSelection") : view.InCombat ? Loc.T("Overlay.Phase.Combat") : Loc.T("Overlay.Phase.Recruit");
        var anomaly = view.AnomalyCardId is { } a ? _cards.NameOf(a) : string.Empty;
        Anomaly = anomaly;

        var rating = snapshot.Rating is null ? 0 : view.IsDuos ? snapshot.Rating.Duos : snapshot.Rating.Solo;
        Rating = rating > 0 ? rating.ToString("N0", CultureInfo.CurrentCulture) : "—";
        var delta = view.IsDuos ? null : snapshot.Session.Delta;
        RatingDeltaValue = delta ?? 0;
        RatingDelta = delta is { } dv && dv != 0 ? (dv > 0 ? $"+{dv}" : $"−{-dv}") : string.Empty;

        Replace(nameof(Tribes), view.Races, r => r, () =>
        {
            Tribes = view.Races.Select(r => Races.Display(r, _locale)).ToList();
            RebuildTribeChips();
        });
        Combats = view.Combats.TakeLast(8)
            .Select(c => new CombatDot(c.Result, Loc.F("Overlay.Combat.Tip", c.Turn, _cards.NameOf(c.OpponentHeroCardId), ResultText(c.Result))
                + (c.DamageTaken > 0 ? Loc.F("Overlay.Combat.Took", c.DamageTaken) : c.DamageDealt > 0 ? Loc.F("Overlay.Combat.Dealt", c.DamageDealt) : string.Empty)
                + (c.Odds is { } o ? Loc.F("Overlay.Combat.Predicted", OddsPercent(o.Win, o.Runs), OddsPercent(o.Loss, o.Runs)) : string.Empty)))
            .ToList();

        if (view.CombatOdds is { } odds)
        {
            OddsWin = OddsPercent(odds.Win, odds.Runs);
            OddsTie = OddsPercent(odds.Tie, odds.Runs);
            OddsLoss = OddsPercent(odds.Loss, odds.Runs);
            SetOddsShares(odds.Win, odds.Tie, odds.Loss);
            OddsNote = odds.Approximate
                ? Loc.P("Overlay.Odds.Estimate", odds.Unsimulated)
                : Loc.T("Overlay.Odds.Simulated");
            if (odds.DamageCap > 0)
            {
                OddsNote = Loc.F("Overlay.Odds.Cap", OddsNote, odds.DamageCap);
            }

            OddsDamageWin = CombatOddsText.Damage(odds.DamageDealt, dealt: true);
            OddsDamageWinTip = CombatOddsText.DamageTip(odds.DamageDealt, dealt: true);
            OddsDamageLoss = CombatOddsText.Damage(odds.DamageTaken, dealt: false);
            OddsDamageLossTip = CombatOddsText.DamageTip(odds.DamageTaken, dealt: false);
            OddsLethal = odds.LethalTaken is > 0 and var lethal ? Loc.F("Overlay.Odds.Lethal", OddsPercent(lethal, odds.Runs)) : string.Empty;
            OddsKill = odds.LethalDealt is > 0 and var kill ? Loc.F("Overlay.Odds.Kill", OddsPercent(kill, odds.Runs)) : string.Empty;
            LethalDanger = OddsLethal.Length > 0;
        }
        RebuildTiers();

        // Only "Mark the best" singles an option out; "Stats only" keeps the advisor's numbers but every flag false.
        var highlight = ShowBestBadges;

        // Lobby-adjusted numbers only mean something once the tribes are known.
        var lobbyKnown = view.Races.Count >= 4;
        var heroTierTip = Loc.T("Overlay.Tier.Heroes");
        Replace(nameof(HeroPicks), view.HeroOptions, h => $"{heroTierTip}{lobbyKnown}{h.CardId}{h.Meta?.AdjustedPosition:0.000}{highlight && h.Recommended}{string.Join(',', h.TopComps ?? [])}", () => HeroPicks = !view.HeroOptions.Any(h => h.Meta is not null) ? [] : view.HeroOptions
            .Select(h =>
            {
                var adjusted = h.Meta is not null && lobbyKnown;
                var delta = adjusted ? h.Meta!.AdjustedPosition - h.Meta.AveragePosition : 0;
                return new HeroPickCard(
                    h.CardId,
                    h.Name,
                    h.Meta?.Tier ?? "–",
                    h.Meta is null ? "—" : (adjusted ? h.Meta.AdjustedPosition : h.Meta.AveragePosition).ToString("0.00", CultureInfo.CurrentCulture),
                    h.Meta is null ? Loc.T("Overlay.Badge.NoData") : adjusted ? Loc.T("Overlay.Badge.AvgPlaceLobby") : Loc.T("Overlay.Badge.AvgPlace"),
                    Math.Abs(delta) < 0.005 ? string.Empty : delta < 0 ? $"▲{-delta:0.00}" : $"▼{delta:0.00}",
                    -delta,
                    h.Meta is null ? string.Empty : Loc.F("Overlay.Badge.PickRate", h.Meta.PickRate.ToString("P0", CultureInfo.CurrentCulture)),
                    h.Stats is { Games: > 0 } s ? Loc.P("Overlay.Hero.Personal", s.Games, s.AveragePlace) : string.Empty,
                    string.Join(" · ", (h.TopComps ?? Array.Empty<string>()).Take(2)),
                    highlight && h.Recommended,
                    h.Meta is not null) { TierTip = heroTierTip };
            })
            .ToList());

        var trinketTierTip = Loc.T("Overlay.Tier.Heroes");
        Replace(nameof(TrinketPicks), view.TrinketOptions, t => $"{t.CardId}{t.Meta?.AveragePlacement:0.000}{highlight && t.Recommended}", () => TrinketPicks = !view.TrinketOptions.Any(t => t.Meta is not null) ? [] : view.TrinketOptions
            .Select(t => new TrinketBadge(
                t.CardId,
                t.Name,
                t.Meta?.Tier ?? "–",
                t.Meta is null ? "—" : t.Meta.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                t.Meta is null ? Loc.T("Overlay.Badge.NoData") : Loc.F("Overlay.Badge.PickRate", t.Meta.PickRate.ToString("P0", CultureInfo.CurrentCulture)),
                highlight && t.Recommended,
                t.Meta is not null) { TierTip = trinketTierTip })
            .ToList());

        var mode = $"{_pickHints}";

        // One slot per option in screen order, so each badge lands above its own card.
        Replace(nameof(DiscoverBadges), view.DiscoverAdvice, d => $"{mode}{d.Slot}{d.Score}{d.Best}{d.Reason}{d.Placement:0.000}{d.TechLevel}", () =>
        {
            var gifts = view.DiscoverOptions;
            var ranked = view.DiscoverAdvice.OrderByDescending(d => d.Score).Select(d => d.Slot).ToList();
            DiscoverBadges = view.DiscoverAdvice
                .OrderBy(d => d.Slot)
                .Select(d => new DiscoverBadge(
                    highlight ? ranked.IndexOf(d.Slot) + 1 : 0,
                    ShortReason(d.Reason, highlight),
                    d.Slot < gifts.Count ? gifts[d.Slot].DarkGift ?? string.Empty : string.Empty,
                    d.Placement is { } p ? p.ToString("0.00", CultureInfo.CurrentCulture) : "—",
                    d.Placement is null ? Loc.T("Overlay.Badge.NoDataYet") : Loc.T("Overlay.Badge.AvgPlace"),
                    highlight && d.Best,
                    d.Placement is not null,
                    d.TechLevel))
                .ToList();
        });
        var source = view.DiscoverSource ?? Loc.T("Overlay.Discover.Name");
        DiscoverTitle = view.DiscoverAdvice.Count == 0
            ? string.Empty
            : !highlight
                ? view.DiscoverSource is { } offeredBy ? Loc.F("Overlay.Discover.OfferedBy", offeredBy) : Loc.T("Overlay.Discover.CommunityStats")
                : view.DiscoverAdvice.Any(d => d.Best)
                    ? Loc.F("Overlay.Discover.BestMarked", source)
                    : Loc.F("Overlay.Discover.CloseCall", source);

        UsageNote = view.UsageNote ?? string.Empty;

        var shopSize = view.Shop.Count;
        Replace(nameof(ShopBadges), view.ShopAdvice, s => $"{mode}{shopSize}{s.Slot}{s.Score}{s.Best}{s.Reason}{s.Placement:0.000}", () => ShopBadges = view.ShopAdvice
            .Select(s => (Pick: s, Label: ShortReason(s.Reason, highlight)))
            // "Mark the best" badges the best buy and other strong ones; "Stats only" shows what is known about every minion.
            .Where(x => highlight ? x.Pick.Best || x.Pick.Score >= StrongPick : x.Label.Length > 0 || x.Pick.Placement is not null)
            .Select(x => new ShopBadge(
                ShopRowCenter + ((x.Pick.Slot - ((shopSize - 1) / 2.0)) * ShopMinionPitch) - (ShopBadgeWidth / 2),
                x.Label,
                highlight && x.Pick.Best,
                x.Pick.Placement is { } p ? Loc.F("Overlay.Badge.AvgPlaceValue", p.ToString("0.00", CultureInfo.CurrentCulture)) : string.Empty))
            .ToList());

        if (SelectedTier > 0 && _fingerprints.GetValueOrDefault("BrowserRaces") != string.Join(',', view.Races))
        {
            RebuildBrowser();
        }
    }

    private void UpdateLobbyAndSession(BattlegroundsView view, LiveSnapshot snapshot)
    {
        var rows = view.Opponents
            .Select(o => WithMainTribe(new LobbyRow(
                o.Place,
                o.HeroCardId,
                o.HeroName,
                o.Health + o.Armor,
                0,
                o.TechLevel,
                o.Triples,
                o.LastResult switch { "Win" => Loc.F("Overlay.Lobby.Won", o.LastDamage), "Loss" => Loc.F("Overlay.Lobby.Lost", o.LastDamage), "Tie" => Loc.T("Overlay.Lobby.Tie"), _ => string.Empty },
                o.LastResult ?? string.Empty,
                o.IsNextOpponent,
                o.Dead,
                false), o.LastBoard, view.Races))
            .ToList();
        if (view.HeroCardId is { } hero)
        {
            rows.Add(WithMainTribe(new LobbyRow(view.Place, hero, _cards.NameOf(hero), view.Health + view.Armor, 0, view.TechLevel, view.Triples,
                string.Empty, string.Empty, false, view.Health <= 0, true), view.Board, view.Races));
        }

        // Empty during hero selection: no opponents seen and no hero picked yet.
        var top = Math.Max(1, rows.Select(r => r.Health).DefaultIfEmpty(0).Max());
        // In place: a fight changes one or two rows, and the others keep their portraits.
        Replace(nameof(LobbyRows), rows, r => $"{r.Place}{r.HeroCardId}{r.Health}{r.Tier}{r.Triples}{r.Last}{r.Next}{r.Dead}{r.Tribe?.Key}{r.TribeCount}", () => Sync(ref _lobbyRows, nameof(LobbyRows), rows
            .Select(r => r with { HealthFraction = r.Dead ? 0 : Math.Clamp(r.Health / (double)top, 0.04, 1) })
            .OrderBy(r => Places.SortKey(r.Place))
            .ToList()));

        var session = snapshot.Session;
        SessionRating = session.StartRating is { } start && session.CurrentRating is { } now
            ? $"{start:N0} → {now:N0}"
            : Rating;
        var cut = view.IsDuos ? 2 : 4;
        Replace(nameof(SessionPlaces), session.Placements, p => p.ToString(CultureInfo.InvariantCulture), () =>
            SessionPlaces = session.Placements.TakeLast(12).Select(p => new PlaceChip(p, p <= cut)).ToList());
    }

    // Tavern row geometry in 1080-unit overlay space (HDT: minion 0.12H plus a 0.0039H margin each side).
    public const double ShopRowWidth = 7 * ShopMinionPitch;
    public const double ShopBadgeWidth = 126;
    private const double ShopMinionPitch = 138;
    private const double ShopRowCenter = ShopRowWidth / 2;
    private const int StrongPick = 40;

    private static readonly string[] ReasonTags = ["Triple", "Pair", "Core", "Add-on", "Cycle", "Fits tribe", "Board synergy"];

    /// <summary>String keys of <see cref="ReasonTags"/> (and "Best stats"), for the badge text.</summary>
    private static readonly Dictionary<string, string> ReasonTagKeys = new(StringComparer.Ordinal)
    {
        ["Triple"] = "Overlay.Tag.Triple",
        ["Pair"] = "Overlay.Tag.Pair",
        ["Core"] = "Overlay.Tag.Core",
        ["Add-on"] = "Overlay.Tag.AddOn",
        ["Cycle"] = "Overlay.Tag.Cycle",
        ["Fits tribe"] = "Overlay.Tag.FitsTribe",
        ["Board synergy"] = "Overlay.Tag.BoardSynergy",
        ["Best stats"] = "Overlay.Tag.BestStats",
    };
    // Tags about a target comp: the overlay shows no comp since the game plan panel went, so they would point at nothing.
    private static readonly string[] CompReasonTags = ["Core", "Add-on", "Cycle", "Fits tribe"];

    /// <summary>
    /// Badge text from the advisor's reason: its tags only, without the comp name or the tags about a comp.
    /// <paramref name="bestStats"/> false drops the "Best stats" fallback.
    /// </summary>
    private static string ShortReason(string reason, bool bestStats)
    {
        var parts = reason.Split(" · ");
        var tags = parts.Where(p => ReasonTags.Contains(p) && !CompReasonTags.Contains(p)).Take(2).ToList();
        if (tags.Count == 0 && bestStats && parts.Contains("Best stats available"))
        {
            tags.Add("Best stats");
        }

        return string.Join(" · ", tags.Select(t => Loc.T(ReasonTagKeys[t])));
    }
}
