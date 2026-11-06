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

/// <summary>One average of the pool's line "Mana 6 · Attack 4.2 · Health 5.1": the stat's name and its value.</summary>
public sealed record PoolAverage(string Label, string Value) : IImmutableView;

/// <summary>
/// One pool card played most in meta decks, drawn like a tracker row: cost gem, the whole name over a strip of art and
/// its share of the meta-deck games of the pool's listed cards.
/// </summary>
public sealed record PoolTopRow(string CardId, string Name, int Cost, string Rarity, string ShareText) : IImmutableView;

/// <summary>One card of this game the hovered card looks back at, as a tracker row: cost gem, name, "×2" when it counts twice.</summary>
public sealed record PoolListRow(string CardId, string Name, int Cost, string Rarity, string CountText) : IImmutableView;

/// <summary>One Excavate tier after the next one, on one quiet line: "T3  Collapse!, Motherlode Drake, …".</summary>
public sealed record ExcavateTierRow(string Label, string Rarity, string Names) : IImmutableView;

/// <summary>One mechanic of the pool: label and share.</summary>
public sealed record PoolMechanicRow(string Label, string PercentText) : IImmutableView;

/// <summary>A mechanic filter chip of the pool browser, or (Section) one of the generator's pools.</summary>
public sealed record PoolChipModel(string Key, string Label, bool Selected, bool Section = false) : IImmutableView;

/// <summary>One row of the pool browser's card grid (up to three cards).</summary>
public sealed record PoolCardRowModel(IReadOnlyList<PoolEntry> Cards) : IImmutableView;

/// <summary>
/// One option of the player's open choice with what players did when it was offered (Hearthpulse data): the share of
/// offers in which it was taken (the bar) and the share of games won after taking it. A dash when there is no number.
/// </summary>
/// <param name="FillWidth">The pick bar's filled width in overlay units (the track is a fixed width).</param>
/// <param name="Top">The most taken option, clearly ahead of the others.</param>
/// <param name="LowSample">The numbers rest on few games: the panel draws them faded.</param>
public sealed record PoolPickRowModel(string CardId, string Name, string PickText, double FillWidth, string WinText, bool Top, bool LowSample) : IImmutableView;

/// <summary>
/// The card pool: a compact panel (pool of the generator being discovered from or hovered in hand, about 300 x 250
/// units) and the pool browser, a full-height side panel with every card, opened by right-click on the compact panel or
/// by <see cref="GameSession.ShowCardPool"/>.
/// </summary>
public sealed partial class OverlayViewModel
{
    /// <summary>The most common mechanics, shown on one line.</summary>
    private const int MechanicChipCount = 4;

    /// <summary>Width of a pick bar's track; an option taken every time it is offered fills it.</summary>
    private const double PickTrack = 150;

    private const int TopCardCount = 4;

    /// <summary>Rows of the "this game" list at most; the rest is "+N more".</summary>
    private const int ListRowCount = 8;

    private static readonly string[] CardPoolStats = ["Cost", "Attack", "Health"];

    /// <summary>
    /// How long the pointer must rest on the compact panel (no button down) before it holds the pool; shorter than the
    /// hovered card's linger (<see cref="CardPoolTracker.HoverLinger"/>), so moving from the card onto it keeps it.
    /// </summary>
    public static readonly TimeSpan CardPoolHoldDelay = TimeSpan.FromMilliseconds(200);

    private bool _showCardPool;
    private CardPoolView? _cardPool;
    private string _cardPoolTitle = string.Empty;
    private string _cardPoolSource = string.Empty;
    private string _cardPoolDescription = string.Empty;
    private string _cardPoolPhrase = string.Empty;
    private string _cardPoolMeta = string.Empty;
    private IReadOnlyList<PoolAverage> _cardPoolAverages = [];
    private IReadOnlyList<PoolMechanicRow> _cardPoolMechanicRows = [];
    private IReadOnlyList<PoolTopRow> _cardPoolTopCards = [];
    private IReadOnlyList<PoolListRow> _cardPoolList = [];
    private string _cardPoolListTitle = string.Empty;
    private string _cardPoolListNote = string.Empty;
    private bool _showCardPoolList;
    private string _cardPoolListKey = string.Empty;
    private CardPoolAnchor? _cardPoolAnchor;
    private PoolEntry? _cardPoolFeatured;
    private string _cardPoolFeaturedNote = string.Empty;
    private bool _cardPoolHasCards;
    private IReadOnlyList<PoolChipModel> _cardPoolSections = [];
    private bool _showCardPoolExcavate;
    private string _cardPoolExcavateNext = string.Empty;
    private IReadOnlyList<PoolListRow> _cardPoolExcavateRows = [];
    private IReadOnlyList<ExcavateTierRow> _cardPoolExcavateTiers = [];
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

    /// <summary>Average mana cost, attack and health of the pool's cards; a stat no pool card has is left out.</summary>
    public IReadOnlyList<PoolAverage> CardPoolAverages { get => _cardPoolAverages; private set => Set(ref _cardPoolAverages, value); }

    /// <summary>The most common mechanics, highest share first.</summary>
    public IReadOnlyList<PoolMechanicRow> CardPoolMechanicRows { get => _cardPoolMechanicRows; private set => Set(ref _cardPoolMechanicRows, value); }

    /// <summary>The pool cards played in the most meta-deck games, as tracker rows; empty without meta data.</summary>
    public IReadOnlyList<PoolTopRow> CardPoolTopCards { get => _cardPoolTopCards; private set => Set(ref _cardPoolTopCards, value); }

    /// <summary>The hovered card's look back at this game ("Your minions that died · Deathrattle") is shown.</summary>
    public bool ShowCardPoolList { get => _showCardPoolList; private set => Set(ref _showCardPoolList, value); }

    /// <summary>The cards of this game the hovered card looks back at, by cost then name.</summary>
    public IReadOnlyList<PoolListRow> CardPoolList { get => _cardPoolList; private set => Set(ref _cardPoolList, value); }

    /// <summary>"Your minions that died · Deathrattle: 3" above the rows when the card's pool is shown too; empty when the header says it.</summary>
    public string CardPoolListTitle { get => _cardPoolListTitle; private set => Set(ref _cardPoolListTitle, value); }

    /// <summary>"None this game yet" or "+3 more"; empty otherwise.</summary>
    public string CardPoolListNote { get => _cardPoolListNote; private set => Set(ref _cardPoolListNote, value); }

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

    /// <summary>The section chips on the compact panel: not for an Excavate pool, whose tiers have rows of their own.</summary>
    public bool ShowCardPoolPanelSections => CardPoolHasSections && !ShowCardPoolExcavate;

    /// <summary>The pool is the Excavate treasures: the panel shows the next tier's rows and the other tiers compactly.</summary>
    public bool ShowCardPoolExcavate
    {
        get => _showCardPoolExcavate;
        private set
        {
            if (Set(ref _showCardPoolExcavate, value))
            {
                Raise(nameof(ShowCardPoolPanelSections));
            }
        }
    }

    /// <summary>"Next treasure · rare" over the next tier's rows; empty outside a game (no next tier known).</summary>
    public string CardPoolExcavateNext { get => _cardPoolExcavateNext; private set => Set(ref _cardPoolExcavateNext, value); }

    /// <summary>The treasures the player's next Excavate can give, as tracker rows.</summary>
    public IReadOnlyList<PoolListRow> CardPoolExcavateRows { get => _cardPoolExcavateRows; private set => Set(ref _cardPoolExcavateRows, value); }

    /// <summary>The cycle's other tiers in the order they come, one line each (every tier when the next is unknown).</summary>
    public IReadOnlyList<ExcavateTierRow> CardPoolExcavateTiers { get => _cardPoolExcavateTiers; private set => Set(ref _cardPoolExcavateTiers, value); }

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
            if (!Set(ref _cardPoolBrowserWidth, Math.Clamp(value, AppSettings.MinCardPoolBrowserWidth, AppSettings.MaxCardPoolBrowserWidth)))
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
        // The turn first: opening hides the popup, and its hide handler re-enters UpdateCardPool, which closes a
        // browser opened on another turn.
        _cardPoolBrowserTurn = _cardPoolView?.Turn ?? 0;
        CardPoolBrowserOpen = true;
    }

    /// <summary>
    /// The pointer and mouse buttons over the compact panel, which lets clicks through to the game (the host reads them
    /// globally). It holds the pool only after the pointer rests on the panel with no button down, so crossing the board
    /// or playing a card through it does not keep a stale pool up (a left press ends the hold); a right-click on it opens
    /// or closes the browser, except while the left button is down (the game uses that right-click to cancel targeting;
    /// it still gets it).
    /// </summary>
    public void UpdateCardPoolPointer(bool over, bool leftDown, bool rightDown) => UpdateCardPoolPointer(over, leftDown, rightDown, DateTime.UtcNow);

    /// <summary>The same at a given time (tests).</summary>
    /// <param name="utcNow">Now (UTC), for the hold delay.</param>
    public void UpdateCardPoolPointer(bool over, bool leftDown, bool rightDown, DateTime utcNow)
    {
        var shown = ShowCardPoolPanel || ShowCardPoolPopup;
        over &= shown;
        if (over && (!_cardPoolPointerOver || leftDown))
        {
            _cardPoolPointerSince = utcNow;
        }

        _cardPoolPointerOver = over;
        var hold = over && !leftDown && utcNow - _cardPoolPointerSince >= CardPoolHoldDelay;
        if (hold != _cardPoolHeldByPointer)
        {
            _cardPoolHeldByPointer = hold;
            CardPoolHeld = hold;
        }

        var click = rightDown && !_cardPoolRightDown && over && !leftDown;
        // A hidden panel forgets the button: the first poll after it shows never counts as a press.
        _cardPoolRightDown = rightDown || !shown;
        if (click)
        {
            OpenCardPoolBrowser.Execute(null);
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

    private void RebuildCardPoolBrowser()
    {
        var pool = CardPoolBrowserOpen ? _cardPool : null;
        var key = pool is null ? string.Empty
            : $"{Loc.Instance.Language}|{pool.SourceCardId}|{pool.Description}|{pool.Count}|{_cardPoolSection}|{string.Join(',', _cardPoolSelected.Order(StringComparer.Ordinal))}";
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
        // Excavate treasures come in the order the cycle gives them, the next tier first.
        var order = pool.Excavate?.FromNext.Select(t => t.Label).ToList();
        var cards = pool.Cards
            .Where(c => _cardPoolSection is null || c.Sections.Contains(_cardPoolSection))
            .Where(c => _cardPoolSelected.All(c.Mechanics.Contains))
            .OrderBy(c => order is null ? 0 : order.IndexOf(c.Sections.FirstOrDefault() ?? string.Empty))
            .ThenBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
        CardPoolBrowserNote = _cardPoolSelected.Count > 0 || _cardPoolSection is not null ? Loc.P("Pool.Match", cards.Count, CardCount(cards.Count)) : string.Empty;
        CardPoolBrowserRows = cards.Chunk(3).Select(row => new PoolCardRowModel(row)).ToList();
    }

    /// <summary>"Excavate: tier 2 of 4" in a game; "Excavate: 4 tiers" when the next tier is not known.</summary>
    private static string ExcavateTitle(ExcavatePool excavate) => excavate.Next is { } next
        ? Loc.F("Pool.Excavate.Title", next, excavate.Tiers.Count)
        : Loc.P("Pool.Excavate.TitleAll", excavate.Tiers.Count);

    /// <summary>The Excavate rows: the next tier's treasures, then the other tiers in the cycle's order, one line each.</summary>
    private void UpdateExcavate(ExcavatePool? excavate)
    {
        ShowCardPoolExcavate = excavate is not null;
        var next = excavate?.Next is not null ? excavate.FromNext[0] : null;
        CardPoolExcavateNext = next is null ? string.Empty : Loc.F("Pool.Excavate.Next", Loc.T("Pool.Excavate.Rarity." + next.Rarity));
        CardPoolExcavateRows = next?.Treasures.Select(t => new PoolListRow(t.CardId, t.Name, t.Cost, t.Rarity, string.Empty)).ToList() ?? [];
        CardPoolExcavateTiers = excavate?.FromNext.Where(t => t != next)
            .Select(t => new ExcavateTierRow(Loc.F("Pool.Excavate.Short", t.Tier), t.Rarity, string.Join(", ", t.Treasures.Select(x => x.Name))))
            .ToList() ?? [];
    }

    /// <summary>An average with one decimal, without a trailing ",0": "6", "4,2".</summary>
    private static string AverageText(double value) => value.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>The section chips of the current pool; the selected one only while the browser is open.</summary>
    private void UpdateSectionChips() =>
        CardPoolSections = _cardPool?.Sections
            .Select(s => new PoolChipModel(s.Label, $"{s.Label} {s.Count.ToString("N0", CultureInfo.InvariantCulture)}", CardPoolBrowserOpen && s.Label == _cardPoolSection, Section: true))
            .ToList() ?? [];

    /// <summary>A card count as the panel writes it ("1,234"), for the plural texts' <c>{1}</c>.</summary>
    private static string CardCount(int count) => count.ToString("N0", CultureInfo.InvariantCulture);
}
