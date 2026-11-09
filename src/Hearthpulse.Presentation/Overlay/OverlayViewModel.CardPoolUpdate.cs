using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

public sealed partial class OverlayViewModel
{
    private void UpdateCardPool(ConstructedView? view, AppSettings settings)
    {
        _cardPoolView = view;
        _cardPoolSettings = settings;
        if (Math.Abs(_cardPoolBrowserWidth - settings.CardPoolBrowserWidth) > 0.5)
        {
            CardPoolBrowserWidth = settings.CardPoolBrowserWidth;
        }

        // The browser covers part of the board: it closes when the turn changes.
        if (CardPoolBrowserOpen && view is not null && view.Turn != _cardPoolBrowserTurn)
        {
            CardPoolBrowserOpen = false;
            _cardPoolHeld = false;
        }

        var pool = settings.ShowCardPool ? view?.CardPool : null;
        var list = settings.ShowCardPool ? view?.MatchList : null;
        UpdateCardPoolPicks(pool is null ? null : view?.DiscoverPicks);
        if (pool is null && list is null && view is not null && settings.ShowCardPool && (CardPoolHeld || CardPoolBrowserOpen)
            && (_cardPool is not null || ShowCardPoolList))
        {
            // The player is reading a pool panel: the pool that triggered it has gone, keep it until they leave or close.
            return;
        }

        // An open browser belongs to its card until the player closes it (×, Esc, right-click, the turn): the pointer
        // crossing another hand card or the hover running out keeps it; a new "show the pool" request replaces it.
        if (CardPoolBrowserOpen && _cardPool is not null && view is not null && settings.ShowCardPool
            && (pool is null || (pool.SourceCardId != _cardPool.SourceCardId && !IsNewRequest(pool))))
        {
            return;
        }

        // The panel stands next to its card; it stays where it is while the player reads it (above).
        CardPoolAnchor = pool is null && list is null ? null : view?.CardPoolAnchor;
        UpdateMatchList(list, pool is not null);
        if (pool is null)
        {
            _cardPool = null;
            _lastRequestedPool = null;
            CardPoolBrowserOpen = false;
            UpdateExcavate(null);
            RebuildCardPoolBrowser();
            if (list is not null)
            {
                ShowListOnly(list);
            }

            ShowCardPool = list is not null;
            return;
        }

        if (_cardPool is null || _cardPool.SourceCardId != pool.SourceCardId)
        {
            _cardPoolSelected.Clear();
            _cardPoolSection = null;
            CardPoolBrowserOpen = false;
        }

        // "Show the pool for this card" (deck or hand row) opens the browser right away.
        var requested = pool.Trigger == PoolTrigger.Request && pool.Uncomputed is null ? pool.SourceCardId : null;
        if (IsNewRequest(pool))
        {
            OpenBrowser();
        }

        _lastRequestedPool = requested;
        if (ReferenceEquals(pool, _cardPool) && ShowCardPool && _cardPoolLanguage == Loc.Instance.Language)
        {
            // The engine hands back the same view while the pool stays up: nothing to rebuild (unless the language changed).
            RebuildCardPoolBrowser();
            return;
        }

        _cardPool = pool;
        _cardPoolLanguage = Loc.Instance.Language;
        CardPoolTitle = pool.Excavate is { } excavate ? ExcavateTitle(excavate)
            : pool.Uncomputed is null ? Loc.P("Pool.Title", pool.Count, CardCount(pool.Count))
            : Loc.T("Pool.NotComputed");
        CardPoolSource = pool.SourceName;
        CardPoolDescription = pool.Uncomputed is { } reason ? $"{pool.SourceName}: {PoolDisplay.Reason(reason)}" : pool.Description;
        CardPoolHasCards = pool.Uncomputed is null;
        // The generator's name in the player's language (the parsed phrase is English card text), then "classes · format".
        CardPoolPhrase = pool.Uncomputed is { } why ? $"{pool.SourceName}: {PoolDisplay.Reason(why)}" : pool.SourceName;
        CardPoolMeta = pool.Uncomputed is null ? pool.Description : string.Empty;
        UpdateSectionChips();
        UpdateExcavate(pool.Excavate);
        CardPoolAverages = pool.Uncomputed is not null || pool.Excavate is not null ? []
            : CardPoolStats
                .Select(stat => PoolStats.Average(pool.Cards, stat) is { } value ? new PoolAverage(PoolDisplay.Stat(stat), AverageText(value)) : null)
                .OfType<PoolAverage>()
                .ToList();
        CardPoolMechanicRows = pool.Excavate is not null ? [] : pool.Mechanics.Take(MechanicChipCount).Select(PoolDisplay.Mechanic)
            .Select(m => new PoolMechanicRow(m.Label, m.PercentText))
            .ToList();
        var top = PoolStats.TopCards(pool, TopCardCount);
        var metaGames = Math.Max(1, pool.Cards.Sum(c => (long)c.MetaGames));
        CardPoolTopCards = top
            .Select(c => new PoolTopRow(c.CardId, c.Name, c.Cost, c.Rarity, OverlayFormat.Percent((double)c.MetaGames / metaGames)))
            .ToList();
        CardPoolFeatured = pool.Featured;
        CardPoolFeaturedNote = PoolDisplay.FeaturedReason(pool.FeaturedReason);
        RebuildCardPoolBrowser();
        ShowCardPool = true;
    }

    /// <summary>"Show the pool for this card" for a card other than the last request (it opens the browser).</summary>
    private bool IsNewRequest(CardPoolView pool) =>
        pool.Trigger == PoolTrigger.Request && pool.Uncomputed is null && pool.SourceCardId != _lastRequestedPool;

    /// <summary>
    /// The pick hint rows: rebuilt only when a number, the options or the language changed, since the session hands a
    /// fresh view on every publish.
    /// </summary>
    private void UpdateCardPoolPicks(DiscoverPicksView? picks)
    {
        var key = picks is null ? string.Empty
            : string.Join('|', picks.Rows.Select(r => $"{r.CardId}:{r.PickRate}:{r.WinRate}:{r.LowSample}:{r.Top}")) + "|" + picks.Rank + "|" + Loc.Instance.Language;
        if (key == _cardPoolPicksKey)
        {
            return;
        }

        _cardPoolPicksKey = key;
        if (picks is null)
        {
            CardPoolPicks = [];
            CardPoolPicksNote = string.Empty;
            return;
        }

        CardPoolPicks = picks.Rows
            .Select(r => new PoolPickRowModel(
                r.CardId,
                r.Name,
                r.PickRate is { } p ? PickPercent(p) : "—",
                r.PickRate is { } f ? Math.Max(2, PickTrack * f) : 0,
                r.WinRate is { } w ? Loc.F("Pool.Picks.Win", PickPercent(w)) : string.Empty,
                r.Top,
                r.LowSample && r.PickRate is not null))
            .ToList();
        CardPoolPicksNote = !picks.HasNumbers ? Loc.T("Pool.Picks.Few")
            : picks.HasLowSample ? Loc.T("Pool.Picks.LowSample")
            : string.Empty;
    }

    private static string PickPercent(double share) => OverlayFormat.Percent(share);

    /// <summary>The "this game" rows: rebuilt only when a card, a count or the language changed (a fresh view comes on every publish).</summary>
    private void UpdateMatchList(MatchListView? list, bool withPool)
    {
        var key = list is null ? string.Empty
            : $"{list.SourceCardId}|{list.Title}|{list.Total}|{withPool}|{string.Join(',', list.Rows.Select(r => $"{r.CardId}:{r.Count}"))}|{Loc.Instance.Language}";
        if (key == _cardPoolListKey)
        {
            return;
        }

        _cardPoolListKey = key;
        ShowCardPoolList = list is not null;
        CardPoolList = list?.Rows.Take(ListRowCount)
            .Select(r => new PoolListRow(r.CardId, r.Name, r.Cost, r.Rarity, r.Count > 1 ? "×" + r.Count.ToString(CultureInfo.CurrentCulture) : string.Empty))
            .ToList() ?? [];
        CardPoolListTitle = list is not null && withPool ? ListHeadline(list) : string.Empty;
        CardPoolListNote = list is null ? string.Empty
            : list.Rows.Count == 0 ? Loc.T("Pool.List.Empty")
            : list.Rows.Count > ListRowCount ? Loc.F("Pool.List.More", list.Rows.Count - ListRowCount)
            : string.Empty;
    }

    /// <summary>"Your minions that died · Deathrattle: 3".</summary>
    private static string ListHeadline(MatchListView list) => $"{list.Title}: {list.Total.ToString(CultureInfo.CurrentCulture)}";

    /// <summary>A card with a "this game" list and no pool: the header names the list and the card, the pool's parts stay empty.</summary>
    private void ShowListOnly(MatchListView list)
    {
        CardPoolTitle = ListHeadline(list);
        CardPoolSource = list.SourceName;
        CardPoolPhrase = list.SourceName;
        CardPoolDescription = string.Empty;
        CardPoolMeta = string.Empty;
        CardPoolHasCards = false;
        CardPoolSections = [];
        UpdateExcavate(null);
        CardPoolAverages = [];
        CardPoolMechanicRows = [];
        CardPoolTopCards = [];
        CardPoolFeatured = null;
        CardPoolFeaturedNote = string.Empty;
    }
}
