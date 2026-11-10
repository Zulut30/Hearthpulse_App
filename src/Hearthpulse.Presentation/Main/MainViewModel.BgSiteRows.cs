#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Site;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The rows of the Battlegrounds tier tabs, a tab a method: heroes, trinkets, cards of a tavern tier and comps, built
/// from the site's lists into the tab's flat list, with the tab's line and empty state. The lists, the slices and the
/// pieces they share are in MainViewModel.BgSite.cs.
/// </summary>
public sealed partial class MainViewModel
{
    // ---- heroes ---------------------------------------------------------------------------------

    private void RebuildHeroRows()
    {
        var slice = _heroSlice;
        var flat = new List<object>();
        if (slice.Shown is { } list)
        {
            var personal = slice.ShownDuos ? _bgPersonalDuos : _bgPersonalSolo;
            var places = list.Entries.Where(e => e.AveragePlacement is not null).Select(e => e.AveragePlacement!.Value).ToList();
            var (best, worst) = places.Count > 0 ? (places.Min(), places.Max()) : (0d, 0d);
            var rows = list.Entries.Select((e, i) => (Entry: e, Row: new MetaHeroRow(
                    i + 1,
                    e.CardId,
                    BgName(e),
                    e.Tier,
                    Number(e.AveragePlacement, "0.00"),
                    e.AveragePlacement is { } place ? PlacementWidth(place, best, worst) : 0,
                    Number(e.Top4Rate, "P0"),
                    Number(e.FirstRate, "P0"),
                    Number(e.PickRate, "P0"),
                    personal.GetValueOrDefault(e.CardId, string.Empty))))
                .ToList();
            flat = Flat(list, rows, "Main.Bg.TierHeroes", SortHeroes);
        }

        HeroTierRows.Sync(flat);
        // The head follows the shown list, not the switch: without a Duos list the solo one is shown under "Top 4".
        HeroTopLabel = Loc.T(slice.Shown is not null && slice.ShownDuos ? "Main.Table.Top2" : "Main.Table.Top4");
        HeroTopTip = Loc.T(slice.Shown is not null && slice.ShownDuos ? "Main.Table.Top2Tip" : "Main.Table.Top4Tip");
        HeroTab = flat.Count > 0 && slice.Shown is { } shown
            ? Rows(BgSource(shown.FetchedAt, slice.Loading, Loc.T(slice.LoadingDuos ? "Main.Mode.Duos" : "Main.Bg.Solo"), Loc.T(slice.ShownDuos ? "Main.Mode.Duos" : "Main.Bg.Solo"), SliceText(shown.Mmr), RangeText(shown.TimeRange)))
            : Empty(SliceEmptyText(slice));
    }

    /// <summary>
    /// One tier's heroes in the chosen order. Without a sort column: the service's order. Heroes without the sorted
    /// number go last, in the service's order.
    /// </summary>
    private IEnumerable<(BgSiteTierEntry Entry, MetaHeroRow Row)> SortHeroes(IReadOnlyList<(BgSiteTierEntry Entry, MetaHeroRow Row)> rows)
    {
        if (_heroSort.Length == 0)
        {
            return rows;
        }

        if (_heroSort == "Name")
        {
            return _heroSortAscending
                ? rows.OrderBy(r => r.Row.Name, StringComparer.CurrentCulture)
                : rows.OrderByDescending(r => r.Row.Name, StringComparer.CurrentCulture);
        }

        Func<BgSiteTierEntry, double?> key = _heroSort switch
        {
            "Top4" => e => e.Top4Rate,
            "First" => e => e.FirstRate,
            "Pick" => e => e.PickRate,
            _ => e => e.AveragePlacement,
        };
        var known = rows.Where(r => key(r.Entry) is not null);
        return (_heroSortAscending ? known.OrderBy(r => key(r.Entry)) : known.OrderByDescending(r => key(r.Entry)))
            .Concat(rows.Where(r => key(r.Entry) is null));
    }

    // ---- trinkets -------------------------------------------------------------------------------

    private void RebuildTrinketRows()
    {
        var slice = _trinketSlice;
        var flat = new List<object>();
        if (slice.Shown is { } list)
        {
            var places = list.Entries.Where(e => e.AveragePlacement is not null).Select(e => e.AveragePlacement!.Value).ToList();
            var (best, worst) = places.Count > 0 ? (places.Min(), places.Max()) : (0d, 0d);
            var rows = list.Entries.Select((e, i) => (Entry: e, Row: new MetaTrinketRow(
                    i + 1,
                    e.CardId,
                    BgName(e),
                    Joined(
                        e.TrinketSize switch
                        {
                            "SMALL" => Loc.T("Main.Bg.TrinketSmall"),
                            "LARGE" => Loc.T("Main.Bg.TrinketLarge"),
                            _ => null,
                        },
                        e.Cost is { } cost ? Loc.F("Main.Bg.TrinketCost", cost) : null),
                    e.Tier,
                    Number(e.AveragePlacement, "0.00"),
                    e.AveragePlacement is { } place ? PlacementWidth(place, best, worst) : 0,
                    // The service's lower bound of games (GamesNote) is not a sample size: it is shown nowhere.
                    Number(e.PickRate, "P1"))))
                .ToList();
            flat = Flat(list, rows, "Main.Bg.TierTrinkets");
        }

        TrinketTierRows.Sync(flat);
        TrinketTab = flat.Count > 0 && slice.Shown is { } shown
            ? Rows(BgSource(shown.FetchedAt, slice.Loading, null, SliceText(shown.Mmr), RangeText(shown.TimeRange)))
            : Empty(SliceEmptyText(slice));
    }

    // ---- cards ----------------------------------------------------------------------------------

    private void RebuildCardRows()
    {
        var spells = CardTier == 0;
        var list = spells ? _bgSite?.Spells : _bgSite?.Minions;
        var stats = spells ? _bgSite?.SpellStats : _bgSite?.MinionStats;
        var shown = list?.Entries.Where(e => spells || e.TavernTier == CardTier).ToList() ?? [];
        // One flag for the list: without numbers for any row the three stat columns go, and the name takes their room.
        var anyStats = stats is not null && shown.Exists(e => stats.ContainsKey(e.CardId));
        var flat = new List<object>();
        if (list is not null)
        {
            var rows = shown.Select((e, i) => (Entry: e, Row: CardRow(i + 1, e, stats?.GetValueOrDefault(e.CardId), spells, anyStats))).ToList();
            flat = Flat(list, rows, spells ? "Main.Bg.TierSpells" : "Main.Bg.TierMinions");
        }

        CardTierRows.Sync(flat);
        CardStatsShown = anyStats;
        if (flat.Count > 0 && list is not null)
        {
            // The minions' own list names no slice: the run of the minion numbers does (docs/bg-site-stats.md).
            var run = spells ? null : _bgSite?.Run;
            CardTab = Rows(BgSource(list.FetchedAt, null, null, SliceText(spells ? list.Mmr : run?.MmrPercentile), RangeText(spells ? list.TimeRange : run?.TimeRange)));
        }
        else
        {
            CardTab = Empty(WholeEmptyText() ?? Loc.T(spells && list is null ? "Main.Bg.SpellsEmpty" : list is null ? "Main.Bg.ListUnavailable" : "Main.Bg.NoCardData"));
        }
    }

    private MetaCardRow CardRow(int rank, BgSiteTierEntry entry, BgSiteCardStat? stat, bool spell, bool anyStats)
    {
        var card = _cards.Get(entry.CardId);
        var detail = spell
            ? entry.TavernTier > 0 ? Loc.F("Main.Bg.TavernLevel", entry.TavernTier) : string.Empty
            : Joined(
                card is { Races.Count: > 0 } ? string.Join(" · ", card.Races.Select(r => Races.Display(r, _cards.Locale))) : null,
                stat?.GamesWith is { } games ? Loc.P("Main.Bg.TribeGames", games) : null);
        // A thin sample keeps its row and its number; only the sign's colour goes.
        var thin = stat?.GamesWith is { } with && with < BattlegroundsMeta.MinCardGames;
        var impact = stat?.Impact is { } value ? Math.Round(value, 2) : (double?)null;
        return new MetaCardRow(
            rank,
            entry.CardId,
            BgName(entry),
            detail,
            impact switch
            {
                null => "—",
                > 0 => "+" + impact.Value.ToString("0.00", CultureInfo.CurrentCulture),
                < 0 => "−" + (-impact.Value).ToString("0.00", CultureInfo.CurrentCulture),
                _ => impact.Value.ToString("0.00", CultureInfo.CurrentCulture),
            },
            impact is null || thin ? 0 : Math.Sign(impact.Value),
            Number(stat?.CombatWinRate, "P1"),
            stat is { AvgPlacementWith: null, AvgPlacementWithout: null } or null
                ? "—"
                : Number(stat.AvgPlacementWith, "0.00") + " · " + Number(stat.AvgPlacementWithout, "0.00"),
            // The tier list's own popularity (another run of the server) stands in when the numbers have none.
            Number(stat?.Popularity ?? entry.PickRate, "P1"),
            anyStats);
    }

    // ---- comps ----------------------------------------------------------------------------------

    private void RebuildCompRows()
    {
        var comps = _bgSite?.Comps ?? [];
        // Without comps of the service, the pool's tribes stand in (when a pool gave any), in both UIs - once the
        // service's load is in: before it the tab says "loading", so it does not open as "Tribes" and turn into "Comps".
        var tribeRows = _bgSiteLoaded && comps.Count == 0 && _meta is { Tribes.Count: > 0 };
        var flat = new List<object>();
        if (tribeRows)
        {
            flat.AddRange(_meta!.Tribes.OrderBy(t => t.AveragePlacement).Select(t => new MetaCompRow(
                t.Tribe == "MIXED" ? Loc.T("Main.Bg.Mixed") : Races.Display(t.Tribe, _cards.Locale),
                Loc.F("Main.Bg.TribeSide", t.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture), Loc.P("Main.Bg.TribeGames", t.Games)),
                string.Empty,
                Loc.F("Main.Bg.TribeRates", t.Top4Rate, t.FirstRate))));
        }
        else
        {
            foreach (var tier in new[] { "S", "A", "B", "C", "D", null })
            {
                var rows = comps.Where(c => tier is null ? c.Tier is not ("S" or "A" or "B" or "C" or "D") : c.Tier == tier).Select(CompRow).ToList();
                if (rows.Count > 0)
                {
                    flat.Add(tier is null
                        ? new TierHeadRow("–", Loc.T("Main.Bg.CompsNoTier"), Loc.P("Main.Bg.TierComps", rows.Count), IsEmpty: false)
                        : Head(tier, Loc.P("Main.Bg.TierComps", rows.Count), empty: false));
                    flat.AddRange(rows);
                }
            }
        }

        CompTiers.Sync(flat);
        if (_tribeRows != tribeRows)
        {
            _tribeRows = tribeRows;
            Raise(nameof(CompTabLabel));
            Raise(nameof(CompCaption));
        }

        // The service names no slice for comps; the pool's tribes have no date of the service.
        CompTab = flat.Count > 0
            ? Rows(BgSource(tribeRows ? null : _bgSite?.CompsFetchedAt, null, null))
            : Empty(WholeEmptyText() ?? Loc.T("Main.Bg.ListUnavailable"));
    }

    private MetaCompRow CompRow(BgSiteComp comp)
    {
        var core = comp.CoreCardIds.Select(id => _cards.Get(id)?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList();
        return new MetaCompRow(
            First(comp.NameRu, comp.NameEn),
            comp.Difficulty switch
            {
                "easy" => Loc.T("Main.Bg.CompDifficulty.Easy"),
                "medium" => Loc.T("Main.Bg.CompDifficulty.Medium"),
                "hard" => Loc.T("Main.Bg.CompDifficulty.Hard"),
                _ => string.Empty,
            },
            core.Count > 0 ? Loc.T("Main.Bg.Core") + string.Join(", ", core) : string.Empty,
            _showCompTips ? comp.Tip ?? string.Empty : string.Empty);
    }
}
