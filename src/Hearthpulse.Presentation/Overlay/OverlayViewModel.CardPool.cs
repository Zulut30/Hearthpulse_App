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

/// <summary>A tab of the pool chart: Cost, Attack or Health (<see cref="Stat"/> stays English).</summary>
public sealed record PoolChartTab(string Stat, string Label, bool Selected);

/// <summary>One column of the pool chart.</summary>
/// <param name="Index">The bar's index in its histogram (hover asks for that bar's cards).</param>
/// <param name="Label">The axis value: "3", "7+".</param>
/// <param name="Height">Bar height in overlay units.</param>
/// <param name="Hovered">The pointer is on this column: its cards are listed.</param>
public sealed record PoolChartBar(int Index, string Label, int Count, double Height, bool IsMedian, bool Hovered)
{
    public bool IsEmpty => Count == 0;
}

/// <summary>One mechanic chip of the pool: label and share.</summary>
public sealed record PoolMechanicRow(string Label, string PercentText);

/// <summary>A mechanic filter chip of the pool browser, or (Section) one of the generator's pools.</summary>
public sealed record PoolChipModel(string Key, string Label, bool Selected, bool Section = false);

/// <summary>One row of the pool browser's card grid (up to three cards).</summary>
public sealed record PoolCardRowModel(IReadOnlyList<PoolEntry> Cards);

/// <summary>
/// One option of the player's open choice with what players did when it was offered (Hearthpulse data): the share of
/// offers in which it was taken (the bar) and the share of games won after taking it. A dash when there is no number.
/// </summary>
/// <param name="FillWidth">The pick bar's filled width in overlay units (the track is a fixed width).</param>
/// <param name="Top">The most taken option, clearly ahead of the others.</param>
/// <param name="LowSample">The numbers rest on few games: the panel draws them faded.</param>
public sealed record PoolPickRowModel(string CardId, string Name, string PickText, double FillWidth, string WinText, bool Top, bool LowSample);

/// <summary>
/// The card pool: a compact panel (pool of the generator being discovered from or hovered in hand, about 300 x 350
/// units) and the pool browser, a full-height side panel with every card, opened by right-click on the compact panel or
/// by <see cref="GameSession.ShowCardPool"/>.
/// </summary>
public sealed partial class OverlayViewModel
{
    /// <summary>Height of the tallest chart bar in overlay units (a low chart: the browser has the full view).</summary>
    private const double BarMaxHeight = 34;

    /// <summary>The most common mechanics, shown as chips.</summary>
    private const int MechanicChipCount = 6;

    /// <summary>Width of a pick bar's track; an option taken every time it is offered fills it.</summary>
    private const double PickTrack = 72;

    private const int TopCardCount = 4;

    /// <summary>Cards listed for a hovered chart column (two columns, they cover mechanics and top cards).</summary>
    private const int BucketCardCount = 8;

    private static readonly string[] CardPoolStats = ["Cost", "Attack", "Health"];

    /// <summary>How long the pointer must rest on the compact panel before it holds the pool.</summary>
    private static readonly TimeSpan CardPoolHoldDelay = TimeSpan.FromMilliseconds(350);

    private bool _showCardPool;
    private CardPoolView? _cardPool;
    private string _cardPoolTitle = string.Empty;
    private string _cardPoolSource = string.Empty;
    private string _cardPoolDescription = string.Empty;
    private string _cardPoolPhrase = string.Empty;
    private string _cardPoolMeta = string.Empty;
    private string _cardPoolStat = "Cost";
    private string _cardPoolChartStat = "Cost";
    private int? _cardPoolBar;
    private IReadOnlyList<PoolChartTab> _cardPoolTabs = [];
    private IReadOnlyList<PoolChartBar> _cardPoolBars = [];
    private string _cardPoolMedianText = string.Empty;
    private IReadOnlyList<PoolMechanicRow> _cardPoolMechanicRows = [];
    private IReadOnlyList<PoolEntry> _cardPoolTopCards = [];
    private string _cardPoolBucketTitle = string.Empty;
    private IReadOnlyList<PoolEntry> _cardPoolBucketCards = [];
    private string _cardPoolBucketMore = string.Empty;
    private CardPoolAnchor? _cardPoolAnchor;
    private PoolEntry? _cardPoolFeatured;
    private string _cardPoolFeaturedNote = string.Empty;
    private bool _cardPoolHasCards;
    private IReadOnlyList<PoolChipModel> _cardPoolSections = [];
    private string? _cardPoolSection;
    private RelayCommand? _toggleCardPoolSection;
    private bool _cardPoolHeld;
    private bool _cardPoolPointerOver;
    private bool _cardPoolHeldByPointer;
    private bool _cardPoolRightDown = true;
    private DateTime _cardPoolPointerSince;
    private ConstructedView? _cardPoolView;
    private AppSettings? _cardPoolSettings;
    private string _cardPoolLanguage = string.Empty;
    private IReadOnlyList<PoolPickRowModel> _cardPoolPicks = [];
    private string _cardPoolPicksNote = string.Empty;
    private string _cardPoolPicksKey = string.Empty;

    private bool _cardPoolBrowserOpen;
    private bool _cardPoolFiltersOpen;
    private string _cardPoolBrowserTitle = string.Empty;
    private string _cardPoolBrowserNote = string.Empty;
    private IReadOnlyList<PoolChipModel> _cardPoolFilterChips = [];
    private IReadOnlyList<PoolCardRowModel> _cardPoolBrowserRows = [];
    private double _cardPoolBrowserWidth = AppSettings.DefaultCardPoolBrowserWidth;
    private readonly HashSet<string> _cardPoolSelected = new(StringComparer.Ordinal);
    private string _cardPoolBrowserKey = string.Empty;
    private string? _lastRequestedPool;
    private int _cardPoolBrowserTurn;
    private RelayCommand? _openCardPoolBrowser;
    private RelayCommand? _closeCardPoolBrowser;
    private RelayCommand? _toggleCardPoolFilters;
    private RelayCommand? _toggleCardPoolMechanic;

    /// <summary>The player resized the pool browser (its width is stored in the settings).</summary>
    public event Action? CardPoolBrowserResized;

    public bool ShowCardPool
    {
        get => _showCardPool;
        private set
        {
            if (Set(ref _showCardPool, value))
            {
                RaiseCardPoolShown();
            }
        }
    }

    /// <summary>
    /// The compact panel at its own (movable) spot, for a pool whose card is not on the screen (a request).
    /// Hidden while the browser (which repeats its header) is open, so they never overlap.
    /// </summary>
    public bool ShowCardPoolPanel => ShowCardPool && !CardPoolBrowserOpen && CardPoolAnchor is null;

    /// <summary>The same panel next to its card: the hovered card in hand or the Discover options.</summary>
    public bool ShowCardPoolPopup => ShowCardPool && !CardPoolBrowserOpen && CardPoolAnchor is not null;

    /// <summary>Where the pool's card sits; the overlay places <see cref="ShowCardPoolPopup"/> beside it.</summary>
    public CardPoolAnchor? CardPoolAnchor
    {
        get => _cardPoolAnchor;
        private set
        {
            if (Set(ref _cardPoolAnchor, value))
            {
                RaiseCardPoolShown();
            }
        }
    }

    /// <summary>The player's overlay scale, which the popup undoes to stand next to a card of the game.</summary>
    public double CardPoolOverlayScale => _cardPoolSettings?.OverlayScale ?? 1;

    /// <summary>"Pool of 136 cards".</summary>
    public string CardPoolTitle { get => _cardPoolTitle; private set => Set(ref _cardPoolTitle, value); }

    /// <summary>The generator's name.</summary>
    public string CardPoolSource { get => _cardPoolSource; private set => Set(ref _cardPoolSource, value); }

    /// <summary>What the pool is: "Discover a Battlecry minion · Shaman + Neutral · Standard".</summary>
    public string CardPoolDescription { get => _cardPoolDescription; private set => Set(ref _cardPoolDescription, value); }

    /// <summary>The generator's words: "Discover a Battlecry minion" (or why the pool is not computed).</summary>
    public string CardPoolPhrase { get => _cardPoolPhrase; private set => Set(ref _cardPoolPhrase, value); }

    /// <summary>One line under the title: classes, format and source, "Shaman + Neutral · Standard · pool: card text".</summary>
    public string CardPoolMeta { get => _cardPoolMeta; private set => Set(ref _cardPoolMeta, value); }

    /// <summary>The chart's stat: "Cost", "Attack" or "Health" (English, for the view's colour triggers).</summary>
    public string CardPoolStat
    {
        get => _cardPoolStat;
        private set
        {
            if (Set(ref _cardPoolStat, value))
            {
                Raise(nameof(CardPoolStatIsAttack));
                Raise(nameof(CardPoolStatIsHealth));
            }
        }
    }

    public bool CardPoolStatIsAttack => _cardPoolStat == "Attack";

    public bool CardPoolStatIsHealth => _cardPoolStat == "Health";

    /// <summary>The chart's tabs; only stats some pool card has.</summary>
    public IReadOnlyList<PoolChartTab> CardPoolTabs { get => _cardPoolTabs; private set => Set(ref _cardPoolTabs, value); }

    /// <summary>The chart of <see cref="CardPoolStat"/>.</summary>
    public IReadOnlyList<PoolChartBar> CardPoolBars { get => _cardPoolBars; private set => Set(ref _cardPoolBars, value); }

    /// <summary>"median 4".</summary>
    public string CardPoolMedianText { get => _cardPoolMedianText; private set => Set(ref _cardPoolMedianText, value); }

    /// <summary>The most common mechanics as chips, highest share first.</summary>
    public IReadOnlyList<PoolMechanicRow> CardPoolMechanicRows { get => _cardPoolMechanicRows; private set => Set(ref _cardPoolMechanicRows, value); }

    /// <summary>The pool cards played in the most meta-deck games; empty without meta data.</summary>
    public IReadOnlyList<PoolEntry> CardPoolTopCards { get => _cardPoolTopCards; private set => Set(ref _cardPoolTopCards, value); }

    /// <summary>"Cost 3: 24 cards" while the pointer is on a chart column; empty otherwise.</summary>
    public string CardPoolBucketTitle { get => _cardPoolBucketTitle; private set => Set(ref _cardPoolBucketTitle, value); }

    /// <summary>The cards of the column under the pointer (the most played first).</summary>
    public IReadOnlyList<PoolEntry> CardPoolBucketCards { get => _cardPoolBucketCards; private set => Set(ref _cardPoolBucketCards, value); }

    /// <summary>"+10 more" when the column has more cards than the list shows.</summary>
    public string CardPoolBucketMore { get => _cardPoolBucketMore; private set => Set(ref _cardPoolBucketMore, value); }

    /// <summary>The highlighted pool card (most played in meta decks), or null.</summary>
    public PoolEntry? CardPoolFeatured { get => _cardPoolFeatured; private set => Set(ref _cardPoolFeatured, value); }

    public string CardPoolFeaturedNote { get => _cardPoolFeaturedNote; private set => Set(ref _cardPoolFeaturedNote, value); }

    /// <summary>The options of the player's open choice with the stats pool's picks; empty outside a choice or without the pool.</summary>
    public IReadOnlyList<PoolPickRowModel> CardPoolPicks { get => _cardPoolPicks; private set => Set(ref _cardPoolPicks, value); }

    /// <summary>"312 choices from this card · Hearthpulse stats pool, 30 days", or that the pool has too few yet.</summary>
    public string CardPoolPicksNote { get => _cardPoolPicksNote; private set => Set(ref _cardPoolPicksNote, value); }

    /// <summary>The generator's separate pools ("Blood Rune 29"); in the browser the selected one filters the grid.</summary>
    public IReadOnlyList<PoolChipModel> CardPoolSections
    {
        get => _cardPoolSections;
        private set
        {
            if (Set(ref _cardPoolSections, value))
            {
                Raise(nameof(CardPoolHasSections));
            }
        }
    }

    public bool CardPoolHasSections => _cardPoolSections.Count > 0;

    /// <summary>Click a section in the browser: show only its cards (again: all of them).</summary>
    public RelayCommand ToggleCardPoolSection => _toggleCardPoolSection ??= new RelayCommand(p =>
    {
        _cardPoolSection = p is string label && label != _cardPoolSection ? label : null;
        RebuildCardPoolBrowser();
    });

    /// <summary>False for a "Pool not computed" panel: no stats, no browser (the right-click hint is hidden).</summary>
    public bool CardPoolHasCards { get => _cardPoolHasCards; private set => Set(ref _cardPoolHasCards, value); }

    /// <summary>The mouse is over a pool panel: keep showing the current pool while the player reads or clicks it.</summary>
    public bool CardPoolHeld
    {
        get => _cardPoolHeld;
        set
        {
            if (Set(ref _cardPoolHeld, value) && !value && _cardPoolSettings is not null)
            {
                // The mouse left: drop a pool that was only kept for reading.
                UpdateCardPool(_cardPoolView, _cardPoolSettings);
            }
        }
    }

    /// <summary>The pool browser (full-height side panel) is open.</summary>
    public bool CardPoolBrowserOpen
    {
        get => _cardPoolBrowserOpen;
        private set
        {
            if (Set(ref _cardPoolBrowserOpen, value))
            {
                RaiseCardPoolShown();
            }
        }
    }

    /// <summary>"Thief's Tools Pool: 47 cards".</summary>
    public string CardPoolBrowserTitle { get => _cardPoolBrowserTitle; private set => Set(ref _cardPoolBrowserTitle, value); }

    /// <summary>"12 cards match" while filters are on; empty otherwise.</summary>
    public string CardPoolBrowserNote { get => _cardPoolBrowserNote; private set => Set(ref _cardPoolBrowserNote, value); }

    /// <summary>The FILTERS block is expanded.</summary>
    public bool CardPoolFiltersOpen { get => _cardPoolFiltersOpen; private set => Set(ref _cardPoolFiltersOpen, value); }

    /// <summary>At least one mechanic filter is on (the dot next to FILTERS).</summary>
    public bool CardPoolFilterActive => _cardPoolSelected.Count > 0;

    /// <summary>Mechanics found in this pool; selected ones must all be on a card (AND).</summary>
    public IReadOnlyList<PoolChipModel> CardPoolFilterChips { get => _cardPoolFilterChips; private set => Set(ref _cardPoolFilterChips, value); }

    /// <summary>The card grid, three per row, by cost then name.</summary>
    public IReadOnlyList<PoolCardRowModel> CardPoolBrowserRows { get => _cardPoolBrowserRows; private set => Set(ref _cardPoolBrowserRows, value); }

    /// <summary>Width of the pool browser in overlay units (the player drags its left edge).</summary>
    public double CardPoolBrowserWidth
    {
        get => _cardPoolBrowserWidth;
        set
        {
            if (!Set(ref _cardPoolBrowserWidth, Math.Clamp(value, 280, 640)))
            {
                return;
            }

            Raise(nameof(CardPoolCardHeight));
            if (_cardPoolSettings is not null)
            {
                _cardPoolSettings.CardPoolBrowserWidth = _cardPoolBrowserWidth;
            }
        }
    }

    /// <summary>Height of a card in the grid: three card renders (215x303 without margins) side by side fill the width.</summary>
    public double CardPoolCardHeight => Math.Floor((CardPoolBrowserWidth - 40) / 3 * 303 / 215);

    /// <summary>Right-click on the compact panel: open the browser (or close it when open).</summary>
    public RelayCommand OpenCardPoolBrowser => _openCardPoolBrowser ??= new RelayCommand(() =>
    {
        if (CardPoolBrowserOpen)
        {
            CloseCardPoolBrowserCore();
        }
        else if (_cardPool is { Uncomputed: null })
        {
            OpenBrowser();
            RebuildCardPoolBrowser();
        }
    });

    /// <summary>The × button or Esc.</summary>
    public RelayCommand CloseCardPoolBrowser => _closeCardPoolBrowser ??= new RelayCommand(CloseCardPoolBrowserCore);

    public RelayCommand ToggleCardPoolFilters => _toggleCardPoolFilters ??= new RelayCommand(() => CardPoolFiltersOpen = !CardPoolFiltersOpen);

    /// <summary>Turns a mechanic filter (tag) on or off.</summary>
    public RelayCommand ToggleCardPoolMechanic => _toggleCardPoolMechanic ??= new RelayCommand(p =>
    {
        if (p is string tag && !_cardPoolSelected.Remove(tag))
        {
            _cardPoolSelected.Add(tag);
        }

        RebuildCardPoolBrowser();
    });

    private void OpenBrowser()
    {
        CardPoolBrowserOpen = true;
        _cardPoolBrowserTurn = _cardPoolView?.Turn ?? 0;
    }

    /// <summary>
    /// The pointer and mouse buttons over the compact panel, which lets clicks through to the game (the host reads them
    /// globally). It holds the pool only after the pointer rests on the panel, so crossing the board does not keep a
    /// stale pool up; a right-click on it opens or closes the browser, except while the left button is down (the game
    /// uses that right-click to cancel targeting; it still gets it).
    /// </summary>
    public void UpdateCardPoolPointer(bool over, bool leftDown, bool rightDown)
    {
        var now = DateTime.UtcNow;
        over &= ShowCardPoolPanel || ShowCardPoolPopup;
        if (over && !_cardPoolPointerOver)
        {
            _cardPoolPointerSince = now;
        }

        _cardPoolPointerOver = over;
        var hold = over && now - _cardPoolPointerSince >= CardPoolHoldDelay;
        if (hold != _cardPoolHeldByPointer)
        {
            _cardPoolHeldByPointer = hold;
            CardPoolHeld = hold;
        }

        if (rightDown && !_cardPoolRightDown && over && !leftDown)
        {
            OpenCardPoolBrowser.Execute(null);
        }

        _cardPoolRightDown = rightDown;
    }

    /// <summary>
    /// The pointer on the compact panel's chart (the panel lets clicks through, so this is hover only): a tab switches the
    /// chart to its stat, a column lists its cards; null and null when the pointer is elsewhere.
    /// </summary>
    public void HoverCardPoolChart(string? stat, int? bar)
    {
        if (_cardPool is null)
        {
            return;
        }

        if (stat is not null)
        {
            if (stat != _cardPoolChartStat && CardPoolTabs.Any(t => t.Stat == stat))
            {
                _cardPoolChartStat = stat;
                _cardPoolBar = null;
                BuildCardPoolChart();
            }

            return;
        }

        if (bar != _cardPoolBar)
        {
            _cardPoolBar = bar;
            BuildCardPoolChart();
        }
    }

    /// <summary>Called when the drag on the browser's edge ends: saves the width.</summary>
    public void CommitCardPoolBrowserWidth() => CardPoolBrowserResized?.Invoke();

    private void CloseCardPoolBrowserCore()
    {
        CardPoolBrowserOpen = false;
        // The browser disappears under the mouse: no "mouse left" event follows, so the hold ends here.
        _cardPoolHeld = false;
        if (_cardPoolSettings is not null)
        {
            UpdateCardPool(_cardPoolView, _cardPoolSettings);
        }

        RebuildCardPoolBrowser();
    }

    private void RaiseCardPoolShown()
    {
        Raise(nameof(ShowCardPoolPanel));
        Raise(nameof(ShowCardPoolPopup));
    }

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
        UpdateCardPoolPicks(pool is null ? null : view?.DiscoverPicks);
        if (pool is null && view is not null && settings.ShowCardPool && (CardPoolHeld || CardPoolBrowserOpen) && _cardPool is not null)
        {
            // The player is reading a pool panel: the pool that triggered it has gone, keep it until they leave or close.
            return;
        }

        // The panel stands next to its card; it stays where it is while the player reads it (above).
        CardPoolAnchor = pool is null ? null : view?.CardPoolAnchor;
        if (pool is null)
        {
            _cardPool = null;
            _lastRequestedPool = null;
            ShowCardPool = false;
            CardPoolBrowserOpen = false;
            RebuildCardPoolBrowser();
            return;
        }

        if (_cardPool is null || _cardPool.SourceCardId != pool.SourceCardId || _cardPool.Count != pool.Count)
        {
            _cardPoolSelected.Clear();
            _cardPoolSection = null;
            CardPoolBrowserOpen = false;
        }

        // "Show the pool for this card" (deck or hand row) opens the browser right away.
        var requested = pool.Trigger == PoolTrigger.Request && pool.Uncomputed is null ? pool.SourceCardId : null;
        if (requested is not null && requested != _lastRequestedPool)
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
        CardPoolTitle = pool.Uncomputed is null ? Loc.P("Pool.Title", pool.Count, CardCount(pool.Count)) : Loc.T("Pool.NotComputed");
        CardPoolSource = pool.SourceName;
        CardPoolDescription = pool.Uncomputed is { } reason ? $"{pool.SourceName}: {PoolDisplay.Reason(reason)}" : $"{pool.Phrase} · {pool.Description}";
        CardPoolHasCards = pool.Uncomputed is null;
        CardPoolPhrase = pool.Uncomputed is { } why ? $"{pool.SourceName}: {PoolDisplay.Reason(why)}" : pool.Phrase;
        // Description is "classes · format"; the source follows it.
        CardPoolMeta = pool.Uncomputed is null ? $"{pool.Description} · {Loc.F("Pool.SourceNote", PoolDisplay.Source(pool.Source))}" : string.Empty;
        UpdateSectionChips();
        _cardPoolChartStat = "Cost";
        _cardPoolBar = null;
        CardPoolTabs = CardPoolStats
            .Where(s => HistogramOf(pool, s) is { Count: > 0 })
            .Select(s => new PoolChartTab(s, PoolDisplay.Stat(s), false))
            .ToList();
        BuildCardPoolChart();
        CardPoolMechanicRows = pool.Mechanics.Take(MechanicChipCount).Select(PoolDisplay.Mechanic)
            .Select(m => new PoolMechanicRow(m.Label, m.PercentText))
            .ToList();
        CardPoolTopCards = PoolStats.TopCards(pool, TopCardCount);
        CardPoolFeatured = pool.Featured;
        CardPoolFeaturedNote = PoolDisplay.FeaturedReason(pool.FeaturedReason);
        RebuildCardPoolBrowser();
        ShowCardPool = true;
    }

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
            : picks.HasLowSample ? Loc.T("Pool.Picks.Note") + " · " + Loc.T("Pool.Picks.LowSample")
            : Loc.T("Pool.Picks.Note");
    }

    private static string PickPercent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private void RebuildCardPoolBrowser()
    {
        var pool = CardPoolBrowserOpen ? _cardPool : null;
        var key = pool is null ? string.Empty
            : $"{Loc.Instance.Language}|{pool.SourceCardId}|{pool.Description}|{pool.Count}|{_cardPoolSection}|{string.Join(",", _cardPoolSelected.Order(StringComparer.Ordinal))}";
        if (key == _cardPoolBrowserKey)
        {
            return;
        }

        // Only a new pool or other filters rebuild the grid, so its scroll position survives overlay updates.
        _cardPoolBrowserKey = key;
        Raise(nameof(CardPoolFilterActive));
        UpdateSectionChips();
        if (pool is null)
        {
            CardPoolBrowserRows = [];
            CardPoolFilterChips = [];
            return;
        }

        _cardPoolSelected.IntersectWith(pool.Filters.Select(f => f.Tag));
        CardPoolBrowserTitle = Loc.P("Pool.BrowserTitle", pool.Count, CardCount(pool.Count), pool.SourceName);
        CardPoolFilterChips = pool.Filters.Select(f => new PoolChipModel(f.Tag, PoolDisplay.Mechanic(f).Label, _cardPoolSelected.Contains(f.Tag))).ToList();
        var cards = pool.Cards
            .Where(c => _cardPoolSection is null || c.Sections.Contains(_cardPoolSection))
            .Where(c => _cardPoolSelected.All(c.Mechanics.Contains))
            .OrderBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
        CardPoolBrowserNote = _cardPoolSelected.Count > 0 || _cardPoolSection is not null ? Loc.P("Pool.Match", cards.Count, CardCount(cards.Count)) : string.Empty;
        CardPoolBrowserRows = cards.Chunk(3).Select(row => new PoolCardRowModel(row)).ToList();
    }

    private static PoolHistogram? HistogramOf(CardPoolView pool, string stat) => stat switch
    {
        "Attack" => pool.Attack,
        "Health" => pool.Health,
        _ => pool.Cost,
    };

    /// <summary>The chart of the selected stat and the cards of the column under the pointer; built only when one of them changes.</summary>
    private void BuildCardPoolChart()
    {
        if (_cardPool is not { Uncomputed: null } pool || HistogramOf(pool, _cardPoolChartStat) is not { Count: > 0 } histogram)
        {
            CardPoolBars = [];
            CardPoolBucketCards = [];
            CardPoolMedianText = CardPoolBucketTitle = CardPoolBucketMore = string.Empty;
            return;
        }

        CardPoolStat = _cardPoolChartStat;
        CardPoolTabs = CardPoolTabs.Select(t => t with { Selected = t.Stat == _cardPoolChartStat }).ToList();
        CardPoolBars = histogram.Bars
            .Select((b, i) => new PoolChartBar(
                i,
                b.Label,
                b.Count,
                b.Count == 0 ? 2 : 4 + (b.Height * (BarMaxHeight - 4)),
                b.IsMedian,
                i == _cardPoolBar))
            .ToList();
        CardPoolMedianText = Loc.F("Pool.MedianValue", histogram.MedianText);

        var cards = _cardPoolBar is { } bar && bar < histogram.Bars.Count ? PoolStats.CardsIn(pool, _cardPoolChartStat, bar) : [];
        CardPoolBucketTitle = cards.Count == 0 ? string.Empty
            : Loc.P("Pool.Bucket", cards.Count, CardCount(cards.Count), PoolDisplay.Stat(_cardPoolChartStat), histogram.Bars[_cardPoolBar!.Value].Label);
        CardPoolBucketCards = cards.Take(BucketCardCount).ToList();
        CardPoolBucketMore = cards.Count > BucketCardCount ? Loc.F("Pool.More", cards.Count - BucketCardCount) : string.Empty;
    }

    /// <summary>The section chips of the current pool; the selected one only while the browser is open.</summary>
    private void UpdateSectionChips() =>
        CardPoolSections = _cardPool?.Sections
            .Select(s => new PoolChipModel(s.Label, $"{s.Label} {s.Count.ToString("N0", CultureInfo.InvariantCulture)}", CardPoolBrowserOpen && s.Label == _cardPoolSection, Section: true))
            .ToList() ?? [];

    /// <summary>A card count as the panel writes it ("1,234"), for the plural texts' <c>{1}</c>.</summary>
    private static string CardCount(int count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
