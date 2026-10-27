#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.ComponentModel;
using System.Globalization;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed record MetaChoice(string Key, string Label, bool Selected);

/// <summary>
/// One archetype of the Standard/Wild tier list, as hearthpulse.net shows it: tier, class portrait, name (the English
/// one under a translated name), win rate on a colour scale with its 95 % interval, popularity bar and games.
/// </summary>
public sealed record MetaArchetypeRow(
    int Id,
    int Rank,
    string Name,
    string EnglishName,
    string HeroId,
    string Detail,
    string Tier,
    string WinRate,
    string WinRateTip,
    double Edge,
    int WinSign,
    double BarFraction,
    string Popularity,
    double PopularityFraction,
    string Games,
    string First,
    string Coin) : INotifyPropertyChanged
{
    private bool _isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Whether the source split this archetype's games by who went first.</summary>
    public bool HasPlaySplit => First != "—" || Coin != "—";

    /// <summary>The archetype whose mulligan guide and lists the side card shows.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}

/// <summary>One tier of the tier list: "Tier S", its bound ("from 53 % · 3 archetypes") and its rows (none is a valid tier).</summary>
public sealed record MetaTierGroup(string Tier, string Title, string Caption, IReadOnlyList<MetaArchetypeRow> Rows)
{
    public bool IsEmpty => Rows.Count == 0;
}

/// <summary>The sort arrow of each sortable column ("▼", "▲" or empty), so a header shows how the table is ordered.</summary>
public sealed record MetaSortArrows(string Tier, string Name, string WinRate, string Popularity, string Games);

public sealed record MetaDeckRow(string Name, string Class, string HeroId, string WinRate, double Edge, string Games, string Dust, string Code);

/// <summary>One card of the mulligan guide; <see cref="Seen"/> is its tooltip ("Seen in 1,234 opening hands").</summary>
public sealed record MulliganRow(string CardId, string Name, string WinRate, double Edge, string KeepRate, string Seen);

/// <summary>A column of the matchup heat map: the opponent archetype's portrait, short name and full name as tooltip.</summary>
public sealed record MatchupHead(string Name, string HeroId, string Tip);

/// <summary>
/// One cell of the heat map: the win rate as a number, <see cref="Edge"/> (win rate − 50 %) for the colour and
/// <see cref="Heat"/> for the tint's opacity (up to 0.6, so the number stays readable); no games leaves it blank.
/// </summary>
public sealed record MatchupCellView(string Text, double Edge, double Heat, string Tip);

/// <summary>One archetype of the heat map against every column.</summary>
public sealed record MatchupMatrixRow(string Name, string HeroId, string WinRate, double Edge, IReadOnlyList<MatchupCellView> Cells);

public sealed partial class MainViewModel
{
    /// <summary>Games an archetype needs before the tier list shows it (hearthpulse.net's "Minimum games" choices).</summary>
    public static IReadOnlyList<int> MetaMinGameChoices { get; } = [100, 250, 500, 1000, 2500, 5000];

    /// <summary>Community stats by format key ("standard", "wild"), as the host loaded them.</summary>
    private readonly Dictionary<string, ConstructedMeta> _constructedMetas = new(StringComparer.Ordinal);
    private IReadOnlyList<MetaArchetypeRow> _metaArchetypes = [];
    private IReadOnlyList<MetaTierGroup> _metaTierGroups = [];
    private IReadOnlyList<MetaDeckRow> _metaDecks = [];
    private IReadOnlyList<MetaDeckRow> _archetypeDecks = [];
    private IReadOnlyList<MulliganRow> _mulligan = [];
    private IReadOnlyList<MetaChoice> _metaClasses = [];
    private IReadOnlyList<MatchupHead> _matchupHeads = [];
    private IReadOnlyList<MatchupMatrixRow> _matchupMatrix = [];
    private MetaArchetypeRow? _selectedArchetype;
    private string _metaClass = string.Empty;
    private string _metaSort = "WinRate";
    private bool _metaSortAscending;
    private bool _metaTable;
    private int _metaMinGames = MetaMinGameChoices[0];
    private bool _metaLoading = true;
    private Func<string> _constructedMetaStatus = () => Loc.T("Main.Meta.Loading");
    private string _archetypeSummary = string.Empty;
    private Func<string> _copyNote = () => string.Empty;
    private RelayCommand? _setMetaSort;
    private RelayCommand? _setMetaView;
    private RelayCommand? _setMetaRank;
    private RelayCommand? _setMetaPeriod;
    private RelayCommand? _setMetaClass;
    private RelayCommand? _setMetaMinGames;
    private RelayCommand? _selectArchetype;
    private RelayCommand? _copyDeck;

    /// <summary>
    /// Raised when the page needs stats: a format key ("standard", "wild") the page shows and has none for, or null when
    /// the player changed a meta filter (every format in use loads again). The host calls <see cref="SetConstructedMeta"/>.
    /// </summary>
    public event Action<string?>? ConstructedMetaRequested;

    public IReadOnlyList<MetaChoice> MetaRanks => Choices(ConstructedMeta.Ranks, Settings.MetaRank);

    public IReadOnlyList<MetaChoice> MetaPeriods => Choices(ConstructedMeta.Periods, Settings.MetaPeriod);

    public IReadOnlyList<MetaChoice> MetaMinGames => MetaMinGameChoices
        .Select(g => new MetaChoice(g.ToString(CultureInfo.InvariantCulture), g.ToString("N0", CultureInfo.CurrentCulture), g == _metaMinGames))
        .ToList();

    /// <summary>"All classes" plus every class with an archetype in the current data.</summary>
    public IReadOnlyList<MetaChoice> MetaClasses { get => _metaClasses; private set => Set(ref _metaClasses, value); }

    /// <summary>The archetypes in table order (the "Table" view), numbered.</summary>
    public IReadOnlyList<MetaArchetypeRow> MetaArchetypes
    {
        get => _metaArchetypes;
        private set
        {
            if (Set(ref _metaArchetypes, value))
            {
                Raise(nameof(MetaHasPlaySplit));
                Raise(nameof(HasMetaArchetypes));
            }
        }
    }

    /// <summary>The same archetypes by tier, S to D (the "Tiers" view); an empty tier says so.</summary>
    public IReadOnlyList<MetaTierGroup> MetaTierGroups { get => _metaTierGroups; private set => Set(ref _metaTierGroups, value); }

    public bool HasMetaArchetypes => _metaArchetypes.Count > 0;

    /// <summary>Whether the tier list shows one flat sortable table instead of tier groups.</summary>
    public bool MetaTableView { get => _metaTable; private set { if (Set(ref _metaTable, value)) Raise(nameof(MetaTierView)); } }

    public bool MetaTierView => !_metaTable;

    public MetaSortArrows MetaArrows => new(Arrow("Tier"), Arrow("Name"), Arrow("WinRate"), Arrow("Popularity"), Arrow("Games"));

    /// <summary>The page's stats are being downloaded (nothing to show yet).</summary>
    public bool MetaLoading { get => _metaLoading; private set { if (Set(ref _metaLoading, value)) Raise(nameof(MetaEmpty)); } }

    /// <summary>Stats are in but nothing passes the filters: the empty state shows.</summary>
    public bool MetaEmpty => !_metaLoading && _metaArchetypes.Count == 0;

    /// <summary>The "1st"/"Coin" columns: shown only when the source has those numbers, not as a column of dashes.</summary>
    public bool MetaHasPlaySplit => _metaArchetypes.Any(r => r.HasPlaySplit);

    /// <summary>Most played decklists, best first among those with enough games.</summary>
    public IReadOnlyList<MetaDeckRow> MetaDecks { get => _metaDecks; private set => Set(ref _metaDecks, value); }

    public IReadOnlyList<MetaDeckRow> ArchetypeDecks { get => _archetypeDecks; private set => Set(ref _archetypeDecks, value); }

    public IReadOnlyList<MulliganRow> Mulligan { get => _mulligan; private set => Set(ref _mulligan, value); }

    /// <summary>The heat map's columns: the most played archetypes.</summary>
    public IReadOnlyList<MatchupHead> MatchupHeads { get => _matchupHeads; private set => Set(ref _matchupHeads, value); }

    /// <summary>The heat map's rows, one per column archetype, in the same order.</summary>
    public IReadOnlyList<MatchupMatrixRow> MatchupMatrix
    {
        get => _matchupMatrix;
        private set
        {
            if (Set(ref _matchupMatrix, value))
            {
                Raise(nameof(HasMatchupMatrix));
            }
        }
    }

    public bool HasMatchupMatrix => _matchupMatrix.Count > 0;

    /// <summary>Where the meta numbers come from, or why there are none; kept as a text maker so it follows a language change.</summary>
    public string ConstructedMetaStatus => _constructedMetaStatus();

    public string ArchetypeSummary { get => _archetypeSummary; private set => Set(ref _archetypeSummary, value); }

    public string CopyNote => _copyNote();

    /// <summary>The selected archetype's name over its detail, or a nudge to pick one.</summary>
    public string SelectedArchetypeTitle => _selectedArchetype?.Name ?? Loc.T("Main.Meta.PickArchetype");

    public MetaArchetypeRow? SelectedArchetype
    {
        get => _selectedArchetype;
        set
        {
            var old = _selectedArchetype;
            if (Set(ref _selectedArchetype, value))
            {
                old?.IsSelected = false;
                value?.IsSelected = true;
                Raise(nameof(SelectedArchetypeTitle));
                RebuildArchetypeDetail();
            }
        }
    }

    public RelayCommand SetMetaRank => _setMetaRank ??= new RelayCommand(p => ChangeMetaFilter(() => Settings.MetaRank = ConstructedMeta.Key(ConstructedMeta.Ranks, p as string)));

    public RelayCommand SetMetaPeriod => _setMetaPeriod ??= new RelayCommand(p => ChangeMetaFilter(() => Settings.MetaPeriod = ConstructedMeta.Key(ConstructedMeta.Periods, p as string)));

    public RelayCommand SetMetaClass => _setMetaClass ??= new RelayCommand(p =>
    {
        _metaClass = p as string ?? string.Empty;
        RebuildConstructedMeta();
    });

    /// <summary>A minimum games choice ("100" ... "5000"): archetypes with fewer games leave the list (no new download).</summary>
    public RelayCommand SetMetaMinGames => _setMetaMinGames ??= new RelayCommand(p =>
    {
        if (int.TryParse(p as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var games) && MetaMinGameChoices.Contains(games))
        {
            _metaMinGames = games;
            Raise(nameof(MetaMinGames));
            RebuildConstructedMeta();
        }
    });

    /// <summary>
    /// Sorts by a column ("Tier", "Name", "WinRate", "Popularity", "Games"); the sorted column again turns the order
    /// round. "Popularity" alone is the old "most played" choice.
    /// </summary>
    public RelayCommand SetMetaSort => _setMetaSort ??= new RelayCommand(p =>
    {
        var column = p as string is "Tier" or "Name" or "WinRate" or "Popularity" or "Games" ? (string)p : "WinRate";
        _metaSortAscending = column == _metaSort ? !_metaSortAscending : column == "Name";
        _metaSort = column;
        Raise(nameof(MetaArrows));
        RebuildConstructedMeta();
    });

    /// <summary>"Tiers" (groups S to D) or "Table" (one sortable list).</summary>
    public RelayCommand SetMetaView => _setMetaView ??= new RelayCommand(p => MetaTableView = p as string == "Table");

    public RelayCommand SelectArchetype => _selectArchetype ??= new RelayCommand(p =>
    {
        if (p is MetaArchetypeRow row)
        {
            SelectedArchetype = row;
        }
    });

    public RelayCommand CopyDeck => _copyDeck ??= new RelayCommand(p =>
    {
        if (p is MetaDeckRow deck)
        {
            CopyDeckCode(deck);
        }
    });

    /// <summary>Stats of one format from the host; null when the page's format could not be loaded.</summary>
    public void SetConstructedMeta(ConstructedMeta? meta)
    {
        if (meta is null)
        {
            MetaLoading = false;
            ShowMetaStatus(() => Loc.T("Main.Meta.Unavailable"));
            return;
        }

        _constructedMetas[meta.Format] = meta;
        RebuildConstructedMeta();
        RebuildConstructedStats();

        // Your own decks show the archetype they belong to, and the deck page compares with it.
        RebuildDecks();
    }

    /// <summary>The host copied (or failed to copy) a deck code; the note under the lists says which.</summary>
    public void DeckCodeCopied(string deckName, bool copied) =>
        ShowCopyNote(copied ? () => Loc.F("Main.Meta.Copied", deckName) : () => Loc.T("Main.Meta.ClipboardBusy"));

    /// <summary>The class's default hero, for its portrait.</summary>
    internal static string ClassHero(string heroClass) => heroClass.ToUpperInvariant() switch
    {
        "WARRIOR" => "HERO_01",
        "SHAMAN" => "HERO_02",
        "ROGUE" => "HERO_03",
        "PALADIN" => "HERO_04",
        "HUNTER" => "HERO_05",
        "DRUID" => "HERO_06",
        "WARLOCK" => "HERO_07",
        "MAGE" => "HERO_08",
        "PRIEST" => "HERO_09",
        "DEMONHUNTER" => "HERO_10",
        "DEATHKNIGHT" => "HERO_11",
        _ => string.Empty,
    };

    private static string ConstructedNote(ConstructedMeta? meta) => meta is null
        ? Loc.T("Stats.Ct.NoteNoMeta")
        : Loc.F("Stats.Ct.NoteSource", meta.Source, ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank));

    partial void OnConstructedFormatChanged() => RebuildConstructedMeta();

    // The tier list and top decks follow the page; the overlay keeps the game's format.
    partial void OnConstructedFormatShown() => RequestPageMeta();

    /// <summary>The platform copies the deck code and reports through <see cref="DeckCodeCopied"/>.</summary>
    partial void CopyDeckCode(MetaDeckRow deck);

    private void ShowMetaStatus(Func<string> text)
    {
        _constructedMetaStatus = text;
        Raise(nameof(ConstructedMetaStatus));
    }

    private void ShowCopyNote(Func<string> text)
    {
        _copyNote = text;
        Raise(nameof(CopyNote));
    }

    private void ChangeMetaFilter(Action apply)
    {
        apply();
        Raise(nameof(MetaRanks));
        Raise(nameof(MetaPeriods));
        MetaLoading = true;
        ShowMetaStatus(() => Loc.T("Main.Meta.LoadingShort"));
        ConstructedMetaRequested?.Invoke(null);
    }

    /// <summary>Asks the host for the page's format stats while it has none (the tier list waits with "Loading").</summary>
    private void RequestPageMeta()
    {
        if (PageMeta is null && ConstructedMeta.FormatKeyOf(_ctFormat) is { } key)
        {
            MetaLoading = true;
            ShowMetaStatus(() => Loc.T("Main.Meta.LoadingShort"));
            ConstructedMetaRequested?.Invoke(key);
        }
    }

    /// <summary>The stats of the format the page shows.</summary>
    private ConstructedMeta? PageMeta => ConstructedMetaFor(_ctFormat);

    private string Arrow(string column) => column != _metaSort ? string.Empty : _metaSortAscending ? "▲" : "▼";

    private static IReadOnlyList<MetaChoice> Choices(IReadOnlyList<MetaOption> options, string? selected)
    {
        var key = ConstructedMeta.Key(options, selected);
        return options.Select(o => new MetaChoice(o.Key, o.Label, o.Key == key)).ToList();
    }

    /// <summary>"Quest Priest · 58% in the meta" for one of your decks (its format's stats), or empty when it matches no archetype.</summary>
    private string DeckMeta(string? deckCode, string? heroClass, string format) =>
        ConstructedStats.ArchetypeOf(deckCode, heroClass, ConstructedMetaFor(format), _cards) is { } archetype
            ? Loc.F("Main.Meta.DeckInMeta", archetype.Name, archetype.WinRate.ToString("P0", CultureInfo.CurrentCulture))
            : string.Empty;

    /// <summary>Win rates within a point of 50 % stay plain; the rest go green or red (hearthpulse.net's colour scale).</summary>
    internal static int WinSign(double winRate) => winRate >= 0.51 ? 1 : winRate <= 0.49 ? -1 : 0;

    private IEnumerable<MetaArchetype> SortArchetypes(IEnumerable<MetaArchetype> archetypes)
    {
        Func<MetaArchetype, IComparable> key = _metaSort switch
        {
            "Name" => a => a.Name,
            "Popularity" => a => a.Share > 0 ? a.Share : a.Games,
            "Games" => a => a.Games,
            _ => a => a.WinRate,
        };
        if (_metaSort == "Name")
        {
            return _metaSortAscending
                ? archetypes.OrderBy(a => a.Name, StringComparer.CurrentCulture)
                : archetypes.OrderByDescending(a => a.Name, StringComparer.CurrentCulture);
        }

        // "Tier" orders by win rate too: the tiers are win-rate bands, and within one the better archetype comes first.
        var ordered = _metaSortAscending ? archetypes.OrderBy(key) : archetypes.OrderByDescending(key);
        return ordered.ThenByDescending(a => a.Games);
    }

    private void RebuildConstructedMeta()
    {
        // Another format's stats never stand in: the tier list stays empty until the page's own are in.
        if (PageMeta is not { } meta)
        {
            MetaClasses = [];
            MetaArchetypes = [];
            MetaTierGroups = [];
            MetaDecks = [];
            MatchupHeads = [];
            MatchupMatrix = [];
            SelectedArchetype = null;
            RebuildArchetypeDetail();
            Raise(nameof(MetaEmpty));
            return;
        }

        MetaLoading = false;
        MetaClasses = new[] { new MetaChoice(string.Empty, Loc.T("Main.Meta.AllClasses"), _metaClass.Length == 0) }
            .Concat(meta.Archetypes
                .Select(a => a.Class)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(ConstructedMeta.ClassName, StringComparer.CurrentCulture)
                .Select(c => new MetaChoice(c, ConstructedMeta.ClassName(c), string.Equals(c, _metaClass, StringComparison.OrdinalIgnoreCase))))
            .ToList();

        var archetypes = SortArchetypes(meta.Archetypes
                .Where(a => _metaClass.Length == 0 || string.Equals(a.Class, _metaClass, StringComparison.OrdinalIgnoreCase))
                .Where(a => a.Games >= _metaMinGames))
            .ToList();
        var topShare = archetypes.Count == 0 ? 0 : archetypes.Max(a => a.Share > 0 ? a.Share : 0);
        var topGames = archetypes.Count == 0 ? 1 : Math.Max(1, archetypes.Max(a => a.Games));
        var selectedId = _selectedArchetype?.Id;
        var rows = archetypes.Select((a, i) => ArchetypeRow(a, i + 1, topShare, topGames)).ToList();
        MetaArchetypes = rows;
        MetaTierGroups = MetaTiers.Letters
            .Select(t => (Tier: t, Rows: (IReadOnlyList<MetaArchetypeRow>)rows.Where(r => r.Tier == t).ToList()))
            .Select(g => new MetaTierGroup(
                g.Tier,
                Loc.F("Main.Meta.TierTitle", g.Tier),
                Loc.P(MetaTiers.IsLast(g.Tier) ? "Main.Meta.TierBelow" : "Main.Meta.TierFrom", g.Rows.Count,
                    MetaTiers.Floor(g.Tier).ToString("P0", CultureInfo.CurrentCulture)),
                g.Rows))
            .ToList();
        Raise(nameof(MetaEmpty));

        // Prefer lists with 200+ games; a thin slice (high rank, few days) falls back to every list.
        var minGames = meta.Decks.Count(d => d.Games >= 200) >= 20 ? 200 : 0;
        MetaDecks = meta.Decks
            .Where(d => _metaClass.Length == 0 || string.Equals(d.Class, _metaClass, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Games >= minGames)
            .OrderByDescending(d => d.WinRate)
            .Take(60)
            .Select(DeckRowOf)
            .ToList();

        RebuildMatchupMatrix(meta.Matchups);

        // The rows are new: the choice moves to the same archetype's new row (or the first one), and an emptied list clears it.
        _selectedArchetype = null;
        SelectedArchetype = rows.FirstOrDefault(r => r.Id == selectedId) ?? rows.FirstOrDefault();
        Raise(nameof(SelectedArchetype));
        Raise(nameof(SelectedArchetypeTitle));
        RebuildArchetypeDetail();

        var updated = meta.Updated == DateTime.MinValue ? string.Empty : Loc.F("Main.Meta.Updated", meta.Updated.ToLocalTime());
        var status = meta.Archetypes.Count == 0
            ? Loc.T("Main.Meta.NoStats")
            : Loc.P("Main.Meta.Status", meta.Games, meta.Source, ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank), ConstructedMeta.Label(ConstructedMeta.Periods, meta.Period), updated);
        ShowMetaStatus(() => status);
    }

    private static MetaArchetypeRow ArchetypeRow(MetaArchetype a, int rank, double topShare, int topGames)
    {
        var interval = MetaTiers.Interval(a.WinRate, a.Games);
        var english = string.Equals(a.Key, a.Name, StringComparison.OrdinalIgnoreCase) ? string.Empty : a.Key;
        return new MetaArchetypeRow(
            a.Id,
            rank,
            a.Name,
            english,
            ClassHero(a.Class),
            // The English name stays beside a translated one, as on hearthpulse.net, so a name heard on stream still finds it.
            (english.Length > 0 ? english + " · " : string.Empty) + Loc.P("Main.Meta.ArchetypeDetail", a.Games, ConstructedMeta.ClassName(a.Class)),
            MetaTiers.Of(a.WinRate),
            a.WinRate.ToString("P1", CultureInfo.CurrentCulture),
            interval is { } ci
                ? Loc.F("Main.Meta.Interval", ci.Low.ToString("P1", CultureInfo.CurrentCulture), ci.High.ToString("P1", CultureInfo.CurrentCulture))
                : string.Empty,
            a.WinRate - 0.5,
            WinSign(a.WinRate),
            Math.Clamp((a.WinRate - 0.40) / 0.25, 0.04, 1),
            a.Share > 0 ? a.Share.ToString("P1", CultureInfo.CurrentCulture) : "—",
            a.Share > 0 && topShare > 0 ? Math.Clamp(a.Share / topShare, 0.03, 1) : Math.Clamp((double)a.Games / topGames, 0.03, 1),
            a.Games.ToString("N0", CultureInfo.CurrentCulture),
            a.FirstGames > 0 ? a.FirstWinRate.ToString("P0", CultureInfo.CurrentCulture) : "—",
            a.CoinGames > 0 ? a.CoinWinRate.ToString("P0", CultureInfo.CurrentCulture) : "—");
    }

    /// <summary>The heat map: every archetype of the matrix against every other, rows and columns in the same order.</summary>
    private void RebuildMatchupMatrix(MetaMatchups matchups)
    {
        var archetypes = matchups.Archetypes;
        MatchupHeads = archetypes
            .Select(a => new MatchupHead(Short(a.Name), ClassHero(a.Class), a.Name))
            .ToList();
        MatchupMatrix = archetypes
            .Select(a => new MatchupMatrixRow(
                a.Name,
                ClassHero(a.Class),
                a.WinRate.ToString("P1", CultureInfo.CurrentCulture),
                a.WinRate - 0.5,
                archetypes.Select(o => CellOf(a, o, matchups.Cell(a.Key, o.Key))).ToList()))
            .ToList();
    }

    private static MatchupCellView CellOf(MatchupArchetype archetype, MatchupArchetype opponent, MatchupCell? cell)
    {
        if (cell is null || cell.Games <= 0)
        {
            return new MatchupCellView("—", 0, 0, Loc.F("Main.Meta.MatchupNoGames", archetype.Name, opponent.Name));
        }

        var edge = cell.WinRate - 0.5;
        return new MatchupCellView(
            (cell.WinRate * 100).ToString("0", CultureInfo.CurrentCulture),
            edge,
            // Even a mirror (50 %) gets a faint tint so the grid reads as one; 15 points off even is the full tint.
            Math.Clamp(Math.Abs(edge) / 0.15, 0.1, 1) * 0.6,
            Loc.P("Main.Meta.MatchupTip", cell.Games, archetype.Name, opponent.Name, cell.WinRate.ToString("P1", CultureInfo.CurrentCulture)));
    }

    /// <summary>A column head fits about ten characters: longer names are cut, the tooltip has the whole one.</summary>
    private static string Short(string name) => name.Length <= 11 ? name : name[..10].TrimEnd() + "…";

    private void RebuildArchetypeDetail()
    {
        var archetype = PageMeta?.Archetypes.FirstOrDefault(a => a.Id == _selectedArchetype?.Id);
        if (archetype is null)
        {
            ArchetypeSummary = string.Empty;
            ArchetypeDecks = [];
            Mulligan = [];
            return;
        }

        // A source without play-rate data reports a 0 % share: that is left out rather than shown as "0.0 % of the meta".
        ArchetypeSummary = Loc.P(archetype.Share > 0 ? "Main.Meta.Summary" : "Main.Meta.SummaryNoShare", archetype.Games, ConstructedMeta.ClassName(archetype.Class),
            archetype.WinRate.ToString("P1", CultureInfo.CurrentCulture), archetype.Share.ToString("P1", CultureInfo.CurrentCulture));

        ArchetypeDecks = PageMeta!.Decks
            .Where(d => d.ArchetypeId == archetype.Id)
            .OrderByDescending(d => d.Games >= 200 ? 1 : 0)
            .ThenByDescending(d => d.WinRate)
            .Take(5)
            .Select(DeckRowOf)
            .ToList();

        // Best cards to keep first, by the win rate of games where the card stayed in the opening hand.
        Mulligan = archetype.Mulligan
            .Take(12)
            .Select(c => new MulliganRow(
                c.CardId,
                _cards.NameOf(c.CardId),
                c.InHandWinRate.ToString("P0", CultureInfo.CurrentCulture),
                c.InHandWinRate - (archetype.MulliganBaseline ?? archetype.WinRate),
                Loc.F("Main.Meta.Kept", c.KeepRate.ToString("P0", CultureInfo.CurrentCulture)),
                Loc.P("Main.Meta.SeenIn", c.InHand)))
            .ToList();
    }

    private MetaDeckRow DeckRowOf(MetaDeck d) => new(
        d.Archetype,
        ConstructedMeta.ClassName(d.Class),
        ClassHero(d.Class),
        d.WinRate.ToString("P1", CultureInfo.CurrentCulture),
        d.WinRate - 0.5,
        d.Games.ToString("N0", CultureInfo.CurrentCulture),
        DustOf(d.DeckString) is var dust and > 0 ? dust.ToString("N0", CultureInfo.CurrentCulture) : "—",
        d.DeckString);

    /// <summary>Dust to craft every card of the deck; Core set cards are free for everyone.</summary>
    private int DustOf(string deckCode)
    {
        if (Hearthpulse.Core.Cards.DeckString.TryDecode(deckCode) is not { } deck)
        {
            return 0;
        }

        var dust = 0;
        foreach (var (dbf, count) in deck.Cards)
        {
            if (_cards.Get(dbf) is not { } card || card.Set == "CORE")
            {
                continue;
            }

            dust += count * card.Rarity switch
            {
                "COMMON" => 40,
                "RARE" => 100,
                "EPIC" => 400,
                "LEGENDARY" => 1600,
                _ => 0,
            };
        }

        return dust;
    }
}
