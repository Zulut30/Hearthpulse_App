using System.Globalization;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.App.Overlay;

/// <summary>One histogram of the card pool panel; <paramref name="BrushKey"/> is a theme brush (ManaGem, TierS...).</summary>
public sealed record PoolHistogramModel(string Title, string BrushKey, IReadOnlyList<PoolBarModel> Bars, string MedianText)
{
    public bool IsAttack => Title == "Attack";

    public bool IsHealth => Title == "Health";
}

/// <param name="Height">Bar height in overlay units.</param>
public sealed record PoolBarModel(string Label, int Count, double Height, bool IsMedian)
{
    public string ToolTip => $"{Label}: {Count}";
}

/// <summary>A row of the full pool list.</summary>
public sealed record PoolRowModel(string CardId, string Name, int Cost, string Stats, string RarityKey, string Meta);

/// <summary>A filter or sort chip of the full list.</summary>
public sealed record PoolChipModel(string Key, string Label, bool Selected);

/// <summary>The card pool panel: the pool of the generator being discovered from, hovered or just played.</summary>
public sealed partial class OverlayViewModel
{
    private const double BarMaxHeight = 34;

    private static readonly (string Key, string Label, int Min, int Max)[] CostFilters =
        [("all", "All", 0, 99), ("0-2", "0-2", 0, 2), ("3-4", "3-4", 3, 4), ("5-6", "5-6", 5, 6), ("7+", "7+", 7, 99)];

    private static readonly (string Key, string Label)[] Sorts = [("cost", "Cost"), ("name", "Name"), ("meta", "Played")];

    private bool _showCardPool;
    private CardPoolView? _cardPool;
    private string _cardPoolTitle = string.Empty;
    private string _cardPoolSource = string.Empty;
    private string _cardPoolDescription = string.Empty;
    private IReadOnlyList<PoolHistogramModel> _cardPoolHistograms = [];
    private IReadOnlyList<PoolMechanic> _cardPoolMechanics = [];
    private PoolEntry? _cardPoolFeatured;
    private string _cardPoolFeaturedNote = string.Empty;
    private bool _cardPoolListOpen;
    private IReadOnlyList<PoolRowModel> _cardPoolRows = [];
    private IReadOnlyList<PoolChipModel> _cardPoolFilters = [];
    private IReadOnlyList<PoolChipModel> _cardPoolSorts = [];
    private string _cardPoolFilter = "all";
    private string _cardPoolSort = "cost";
    private bool _cardPoolHeld;
    private ConstructedView? _cardPoolView;
    private AppSettings? _cardPoolSettings;
    private RelayCommand? _toggleCardPoolList;
    private RelayCommand? _setCardPoolFilter;
    private RelayCommand? _setCardPoolSort;

    public bool ShowCardPool { get => _showCardPool; private set => Set(ref _showCardPool, value); }

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

    /// <summary>The full list is open (right-click on the panel).</summary>
    public bool CardPoolListOpen { get => _cardPoolListOpen; private set => Set(ref _cardPoolListOpen, value); }

    public IReadOnlyList<PoolRowModel> CardPoolRows { get => _cardPoolRows; private set => Set(ref _cardPoolRows, value); }

    public IReadOnlyList<PoolChipModel> CardPoolFilters { get => _cardPoolFilters; private set => Set(ref _cardPoolFilters, value); }

    public IReadOnlyList<PoolChipModel> CardPoolSorts { get => _cardPoolSorts; private set => Set(ref _cardPoolSorts, value); }

    /// <summary>The mouse is over the panel: keep showing the current pool while the player reads or clicks it.</summary>
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

    public RelayCommand ToggleCardPoolList => _toggleCardPoolList ??= new RelayCommand(() =>
    {
        CardPoolListOpen = !CardPoolListOpen && _cardPool is not null;
        RebuildCardPoolList();
    });

    public RelayCommand SetCardPoolFilter => _setCardPoolFilter ??= new RelayCommand(p =>
    {
        _cardPoolFilter = p as string ?? "all";
        RebuildCardPoolList();
    });

    public RelayCommand SetCardPoolSort => _setCardPoolSort ??= new RelayCommand(p =>
    {
        _cardPoolSort = p as string ?? "cost";
        RebuildCardPoolList();
    });

    private void UpdateCardPool(ConstructedView? view, AppSettings settings)
    {
        _cardPoolView = view;
        _cardPoolSettings = settings;
        var pool = settings.ShowCardPool ? view?.CardPool : null;
        if (pool is null && view is not null && settings.ShowCardPool && (CardPoolHeld || CardPoolListOpen) && _cardPool is not null)
        {
            // The player is reading the panel or its list: the pool that triggered it has gone, keep it until they leave.
            return;
        }

        if (pool is null)
        {
            _cardPool = null;
            ShowCardPool = false;
            CardPoolListOpen = false;
            return;
        }

        var changed = !ReferenceEquals(pool, _cardPool) && (_cardPool is null || _cardPool.SourceCardId != pool.SourceCardId || _cardPool.Count != pool.Count);
        _cardPool = pool;
        CardPoolTitle = $"Pool of {pool.Count.ToString("N0", CultureInfo.InvariantCulture)} card{(pool.Count == 1 ? string.Empty : "s")}";
        CardPoolSource = pool.SourceName;
        CardPoolDescription = $"{pool.Phrase} · {pool.Description}";
        CardPoolHistograms = new[] { (pool.Cost, "ManaGem"), (pool.Attack, "TierS"), (pool.Health, "Negative") }
            .Where(h => h.Item1 is { Count: > 0 })
            .Select(h => new PoolHistogramModel(
                h.Item1!.Title,
                h.Item2,
                h.Item1.Bars.Select(b => new PoolBarModel(b.Label, b.Count, b.Count == 0 ? 1 : 3 + b.Height * (BarMaxHeight - 3), b.IsMedian)).ToList(),
                h.Item1.MedianText))
            .ToList();
        CardPoolMechanics = pool.Mechanics;
        CardPoolFeatured = pool.Featured;
        CardPoolFeaturedNote = pool.FeaturedReason ?? string.Empty;
        if (changed)
        {
            _cardPoolFilter = "all";
            CardPoolListOpen = false;
        }

        RebuildCardPoolList();
        ShowCardPool = true;
    }

    private void RebuildCardPoolList()
    {
        CardPoolFilters = CostFilters.Select(f => new PoolChipModel(f.Key, f.Label, f.Key == _cardPoolFilter)).ToList();
        CardPoolSorts = Sorts.Select(s => new PoolChipModel(s.Key, s.Label, s.Key == _cardPoolSort)).ToList();
        if (!CardPoolListOpen || _cardPool is null)
        {
            CardPoolRows = [];
            return;
        }

        var filter = CostFilters.FirstOrDefault(f => f.Key == _cardPoolFilter);
        var rows = _cardPool.Cards.Where(c => filter.Key is null || c.Cost >= filter.Min && c.Cost <= filter.Max);
        rows = _cardPoolSort switch
        {
            "name" => rows.OrderBy(c => c.Name, StringComparer.CurrentCulture),
            "meta" => rows.OrderByDescending(c => c.MetaGames).ThenBy(c => c.Cost),
            _ => rows.OrderBy(c => c.Cost).ThenBy(c => c.Name, StringComparer.CurrentCulture),
        };
        CardPoolRows = rows.Select(c => new PoolRowModel(
            c.CardId,
            c.Name,
            c.Cost,
            c.Type switch
            {
                "MINION" or "WEAPON" => $"{c.Attack}/{c.Health?.ToString(CultureInfo.InvariantCulture) ?? "-"}",
                _ => Title(c.Type),
            },
            c.Rarity,
            c.MetaGames > 0 ? c.MetaGames.ToString("N0", CultureInfo.InvariantCulture) : string.Empty)).ToList();

        static string Title(string type) => type.Length > 1 ? type[0] + type[1..].ToLowerInvariant() : type;
    }
}
