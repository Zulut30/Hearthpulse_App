using System.Globalization;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App.Overlay;

/// <summary>One histogram of the card pool panel (Cost, Attack or Health; the view picks the colour).</summary>
public sealed record PoolHistogramModel(string Title, IReadOnlyList<PoolBarModel> Bars, string MedianText)
{
    /// <summary>The title in the interface language (<see cref="Title"/> stays English).</summary>
    public string Label { get; } = PoolDisplay.Stat(Title);

    public bool IsAttack => Title == "Attack";

    public bool IsHealth => Title == "Health";
}

/// <param name="Height">Bar height in overlay units.</param>
public sealed record PoolBarModel(string Label, int Count, double Height, bool IsMedian)
{
    public string ToolTip => $"{Label}: {Count}";
}

/// <summary>A mechanic filter chip of the pool browser, or (Section) one of the generator's pools.</summary>
public sealed record PoolChipModel(string Key, string Label, bool Selected, bool Section = false);

/// <summary>One row of the pool browser's card grid (up to three cards).</summary>
public sealed record PoolCardRowModel(IReadOnlyList<PoolEntry> Cards);

/// <summary>
/// The card pool: a compact panel (pool of the generator being discovered from, hovered or just played) and the pool
/// browser, a full-height side panel with every card, opened by right-click on the compact panel or by
/// <see cref="GameSession.ShowCardPool"/>.
/// </summary>
public sealed partial class OverlayViewModel
{
    private const double BarMaxHeight = 34;

    /// <summary>How long the pointer must rest on the compact panel before it holds the pool.</summary>
    private static readonly TimeSpan CardPoolHoldDelay = TimeSpan.FromMilliseconds(350);

    private bool _showCardPool;
    private CardPoolView? _cardPool;
    private string _cardPoolTitle = string.Empty;
    private string _cardPoolSource = string.Empty;
    private string _cardPoolDescription = string.Empty;
    private IReadOnlyList<PoolHistogramModel> _cardPoolHistograms = [];
    private IReadOnlyList<PoolMechanic> _cardPoolMechanics = [];
    private PoolEntry? _cardPoolFeatured;
    private string _cardPoolFeaturedNote = string.Empty;
    private bool _cardPoolHasCards;
    private string _cardPoolSourceNote = string.Empty;
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
                Raise(nameof(ShowCardPoolPanel));
            }
        }
    }

    /// <summary>The compact panel: hidden while the browser (which repeats its header) is open, so they never overlap.</summary>
    public bool ShowCardPoolPanel => ShowCardPool && !CardPoolBrowserOpen;

    /// <summary>"Pool of 136 cards".</summary>
    public string CardPoolTitle { get => _cardPoolTitle; private set => Set(ref _cardPoolTitle, value); }

    /// <summary>The generator's name.</summary>
    public string CardPoolSource { get => _cardPoolSource; private set => Set(ref _cardPoolSource, value); }

    /// <summary>What the pool is: "Discover a Battlecry minion · Shaman + Neutral · Standard".</summary>
    public string CardPoolDescription { get => _cardPoolDescription; private set => Set(ref _cardPoolDescription, value); }

    public IReadOnlyList<PoolHistogramModel> CardPoolHistograms { get => _cardPoolHistograms; private set => Set(ref _cardPoolHistograms, value); }

    public IReadOnlyList<PoolMechanic> CardPoolMechanics { get => _cardPoolMechanics; private set => Set(ref _cardPoolMechanics, value); }

    /// <summary>The highlighted pool card (most played in meta decks), or null.</summary>
    public PoolEntry? CardPoolFeatured { get => _cardPoolFeatured; private set => Set(ref _cardPoolFeatured, value); }

    public string CardPoolFeaturedNote { get => _cardPoolFeaturedNote; private set => Set(ref _cardPoolFeaturedNote, value); }

    /// <summary>"pool: Hearthstone Wiki" or "pool: card text".</summary>
    public string CardPoolSourceNote { get => _cardPoolSourceNote; private set => Set(ref _cardPoolSourceNote, value); }

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
                Raise(nameof(ShowCardPoolPanel));
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
        over &= ShowCardPoolPanel;
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
        if (pool is null && view is not null && settings.ShowCardPool && (CardPoolHeld || CardPoolBrowserOpen) && _cardPool is not null)
        {
            // The player is reading a pool panel: the pool that triggered it has gone, keep it until they leave or close.
            return;
        }

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
        CardPoolSourceNote = pool.Uncomputed is null ? Loc.F("Pool.SourceNote", PoolDisplay.Source(pool.Source)) : string.Empty;
        UpdateSectionChips();
        CardPoolHistograms = new[] { pool.Cost, pool.Attack, pool.Health }
            .Where(h => h is { Count: > 0 })
            .Select(h => new PoolHistogramModel(
                h!.Title,
                h.Bars.Select(b => new PoolBarModel(b.Label, b.Count, b.Count == 0 ? 1 : 3 + b.Height * (BarMaxHeight - 3), b.IsMedian)).ToList(),
                h.MedianText))
            .ToList();
        CardPoolMechanics = pool.Mechanics.Select(PoolDisplay.Mechanic).ToList();
        CardPoolFeatured = pool.Featured;
        CardPoolFeaturedNote = PoolDisplay.FeaturedReason(pool.FeaturedReason);
        RebuildCardPoolBrowser();
        ShowCardPool = true;
    }

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

    /// <summary>The section chips of the current pool; the selected one only while the browser is open.</summary>
    private void UpdateSectionChips() =>
        CardPoolSections = _cardPool?.Sections
            .Select(s => new PoolChipModel(s.Label, $"{s.Label} {s.Count.ToString("N0", CultureInfo.InvariantCulture)}", CardPoolBrowserOpen && s.Label == _cardPoolSection, Section: true))
            .ToList() ?? [];

    /// <summary>A card count as the panel writes it ("1,234"), for the plural texts' <c>{1}</c>.</summary>
    private static string CardCount(int count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
