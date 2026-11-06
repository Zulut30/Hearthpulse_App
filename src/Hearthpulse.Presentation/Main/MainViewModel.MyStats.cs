#if HEARTHPULSE_WPF
using Hearthpulse.App.Controls;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>A minion of the final board in the rating chart's hover card.</summary>
public sealed record BoardMinionRow(string CardId, string Name, int Attack, int Health, bool Golden) : IImmutableView;

/// <summary>A Battlegrounds game on the rating chart, with what its hover card shows.</summary>
public sealed record BgGamePoint(
    double ChartValue,
    string ChartLabel,
    string HeroCardId,
    string HeroName,
    string Place,
    string PlaceKey,
    string Delta,
    int DeltaValue,
    string When,
    IReadOnlyList<BoardMinionRow> Board) : IImmutableView, IChartValue;

/// <summary>A ranked game on the rank chart, with what its hover card shows (<see cref="Result"/> is the saved "Win"/"Loss" for its colour).</summary>
public sealed record CtGamePoint(
    double ChartValue,
    string ChartLabel,
    string HeroCardId,
    string Title,
    string Result,
    string ResultText,
    string Detail,
    string When) : IImmutableView, IChartValue;

/// <summary>One hero in "Hero performance": your numbers, the community's (when known) and the difference.</summary>
public sealed record BgHeroPerfRow(
    string CardId,
    string Name,
    string Games,
    string Net,
    int NetValue,
    IReadOnlyList<double?> CombatYou,
    IReadOnlyList<double?>? CombatGlobal,
    IReadOnlyList<double> PlacesYou,
    IReadOnlyList<double>? PlacesGlobal,
    string AveragePlace,
    double AverageFraction,
    VsGlobal AverageVs,
    string PickRate,
    double PickFraction,
    VsGlobal PickVs) : IImmutableView;

/// <summary>
/// A "vs Global" difference: the text (e.g. "0.42"), an arrow for its direction and whether it is good for the
/// player (+1), bad (-1) or neutral (0). <see cref="Shown"/> is false when there is no community number.
/// </summary>
public sealed record VsGlobal(bool Shown, string Text, string Glyph, int Sign) : IImmutableView
{
    public static VsGlobal None { get; } = new(false, string.Empty, string.Empty, 0);

    /// <summary>
    /// Difference of <paramref name="mine"/> to <paramref name="global"/>; <paramref name="lowerIsBetter"/> for placements.
    /// Rates (<paramref name="percent"/>) are written in the culture's percent pattern, like every other percentage.
    /// </summary>
    public static VsGlobal Of(double? mine, double? global, bool lowerIsBetter, string format, bool percent = false)
    {
        if (mine is not { } m || global is not { } g)
        {
            return None;
        }

        var diff = m - g;
        var rounded = percent ? Math.Round(diff * 100) : Math.Round(diff, format == "0.00" ? 2 : 0);
        var sign = rounded == 0 ? 0 : (rounded < 0) == lowerIsBetter ? 1 : -1;
        var glyph = rounded > 0 ? "\U000F0360" : rounded < 0 ? "\U000F035D" : "\U000F0374";
        var text = percent ? (Math.Abs(rounded) / 100).ToString("P0", CultureInfo.CurrentCulture) : Math.Abs(diff).ToString(format, CultureInfo.CurrentCulture);
        return new VsGlobal(true, text, glyph, sign);
    }
}

/// <summary>A group in a My Stats table (class, opponent class, archetype, game length).</summary>
public sealed record StatGroupRow(string Label, string CardId, string Games, string Record, string WinRate, double WinFraction, VsGlobal Vs, int GameCount = 0) : IImmutableView
{
    /// <summary>The win rate's colour: +1 from 53 %, -1 up to 47 %, 0 between them and when there is no rate.</summary>
    public int WinSign => WinRate == "—" ? 0 : MetaTiers.WinRateSign(WinFraction);

    /// <summary>+1 on the table's best win rate, -1 on its worst (groups with enough games only), else 0.</summary>
    public int WinMark { get; init; }

    public bool WinBest => WinMark > 0;

    public bool WinWorst => WinMark < 0;

    /// <summary>The value of a sortable column: "Name", "Games" or "WinRate".</summary>
    internal IComparable? SortValue(string column) => column switch
    {
        "Name" => Label,
        "Games" => GameCount,
        _ => WinRate == "—" ? null : WinFraction,
    };

    /// <summary>The rows with the best and the worst win rate flagged (groups of <see cref="DeckDetails.SmallSample"/> games or more).</summary>
    internal static List<StatGroupRow> Marked(IReadOnlyList<StatGroupRow> rows)
    {
        var marks = TableSort.BestAndWorst(rows, r => r.SortValue("WinRate") as double?, r => r.GameCount >= DeckDetails.SmallSample);
        return rows.Select((r, i) => r with { WinMark = marks[i] }).ToList();
    }
}

/// <summary>
/// A final place in "Where you finish": "1st", its share of the games, the bar against the most common place and
/// whether it counts as a top finish (top 4, top 2 in Duos) for the bar's colour.
/// </summary>
public sealed record BgPlaceRow(string Place, string Share, double Fraction, bool Top, string Tip) : IImmutableView
{
    /// <summary>No game ended on this place: the row sinks back and shows no "0 %" (D3, A06).</summary>
    public bool Empty => Fraction <= 0;

    /// <summary>How far an empty place's row sinks back.</summary>
    public const double EmptyRowOpacity = 0.4;

    /// <summary>The row's opacity: an empty place sinks back.</summary>
    public double Dim => Empty ? EmptyRowOpacity : 1;
}

/// <summary>One Arena run.</summary>
/// <param name="DeckScore">"Deck score 41" (ArenaSmith average of the run's deck), or empty.</param>
public sealed record ArenaRunRow(string ClassName, string HeroCardId, string Record, double WinFraction, string When, string Status, bool Finished, string DeckScore = "", string DeckTier = "") : IImmutableView
{
    /// <summary>The run's key (<see cref="ArenaRun.Key"/>): its rewards are kept under it.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>The rewards the player entered, or null.</summary>
    public Core.Arena.ArenaRewards? Rewards { get; init; }

    /// <summary>The chip's rewards above 0; empty shows "Add rewards".</summary>
    public IReadOnlyList<ArenaRewardPart> RewardParts { get; init; } = [];

    public bool HasRewards => RewardParts.Count > 0;

    /// <summary>The chip's tooltip and screen-reader name.</summary>
    public string RewardsTip { get; init; } = string.Empty;

    /// <summary>Opens this run's rewards editor (the page's command, the key as its parameter).</summary>
    public RelayCommand? EditRewards { get; init; }

    /// <summary>The open rewards editor when it is this run's, else null.</summary>
    public ArenaRewardsEdit? Edit { get; init; }

    public bool Editing => Edit is not null;
}

public sealed partial class MainViewModel
{
    private IReadOnlyList<GameRecord> _allGames = [];
    private StatsPeriod _bgPeriod = StatsPeriod.All;
    private BgHeroSort _bgSort = BgHeroSort.Games;
    private int _bgMinGames = 1;
    private IReadOnlyList<StatTile> _bgStatTiles = [];
    private IReadOnlyList<BgPlaceRow> _bgPlaces = [];
    private IReadOnlyList<IChartValue> _bgRatingChart = [];
    private IReadOnlyList<BgHeroPerfRow> _bgHeroPerf = [];
    private string _bgStatsNote = string.Empty;

    private string _ctFormat = "FT_STANDARD";
    private StatsPeriod _ctPeriod = StatsPeriod.All;
    private IReadOnlyList<StatTile> _ctStatTiles = [];
    private IReadOnlyList<StatTile> _ctMoreTiles = [];
    private bool _bgHeroAllColumns;
    private IReadOnlyList<IChartValue> _ctRankChart = [];
    private IReadOnlyList<StatGroupRow> _ctClasses = [];
    private IReadOnlyList<StatGroupRow> _ctOpponents = [];
    private IReadOnlyList<StatGroupRow> _ctArchetypes = [];
    private IReadOnlyList<StatGroupRow> _ctLengths = [];
    private string _ctStatsNote = string.Empty;

    private StatsPeriod _arenaPeriod = StatsPeriod.All;
    private IReadOnlyList<StatTile> _arenaStatTiles = [];
    private IReadOnlyList<StatTile> _arenaMoreTiles = [];
    private IReadOnlyList<ArenaRunRow> _arenaRuns = [];
    private IReadOnlyList<StatGroupRow> _arenaClasses = [];
    private IReadOnlyList<StatGroupRow> _arenaOpponents = [];

    private RelayCommand? _setBgPeriod;
    private RelayCommand? _setBgSort;
    private RelayCommand? _setBgMinGames;
    private RelayCommand? _setCtPeriod;
    private RelayCommand? _setArenaPeriod;
    private TableSort? _ctClassesSort;
    private TableSort? _ctOpponentsSort;
    private TableSort? _ctArchetypesSort;
    private TableSort? _arenaClassesSort;
    private TableSort? _arenaOpponentsSort;

    public RelayCommand SetBgPeriod => _setBgPeriod ??= new RelayCommand(p => { _bgPeriod = ParsePeriod(p); RebuildBattlegroundsStats(); });

    public RelayCommand SetBgSort => _setBgSort ??= new RelayCommand(p =>
    {
        _bgSort = Enum.TryParse<BgHeroSort>(p as string, out var sort) ? sort : BgHeroSort.Games;
        RebuildBattlegroundsStats();
    });

    public RelayCommand SetBgMinGames => _setBgMinGames ??= new RelayCommand(p =>
    {
        _bgMinGames = Math.Max(1, Convert.ToInt32(p, CultureInfo.InvariantCulture));
        RebuildBattlegroundsStats();
    });

    /// <summary>The FormatType the Standard or Wild page shows: "FT_STANDARD" or "FT_WILD".</summary>
    public string ConstructedFormat => _ctFormat;

    /// <summary>The Constructed page shows Wild: its title takes the Wild glyph.</summary>
    public bool ConstructedWild => _ctFormat == "FT_WILD";

    /// <summary>The page title: Standard or Wild.</summary>
    public string ConstructedTitle => Loc.T(_ctFormat == "FT_WILD" ? "Main.Nav.Wild" : "Main.Nav.Standard");

    /// <summary>
    /// Shows the Constructed page for one FormatType (the Standard and Wild nav items). My stats and your decks follow
    /// it. An open deck of the other format closes.
    /// </summary>
    public void ShowConstructedFormat(string formatType)
    {
        OnShowingConstructedFormat(formatType);
        if (formatType != _ctFormat)
        {
            _ctFormat = formatType;
            if (DeckOpen && _deckFamilies.FirstOrDefault(f => f.Key == _openDeckKey) is { } open
                && ConstructedPage.FormatOf(ConstructedPage.PageOf(open.Format)) != formatType)
            {
                CloseDeck();
            }

            Raise(nameof(ConstructedFormat));
            Raise(nameof(ConstructedTitle));
            OnConstructedTitleChanged();
            RebuildConstructedStats();
            RebuildDecks();
            OnConstructedFormatChanged();
        }

        OnConstructedFormatShown();
    }

    public RelayCommand SetCtPeriod => _setCtPeriod ??= new RelayCommand(p => { _ctPeriod = ParsePeriod(p); RebuildConstructedStats(); });

    public RelayCommand SetArenaPeriod => _setArenaPeriod ??= new RelayCommand(p => { _arenaPeriod = ParsePeriod(p); RebuildArenaStats(); });

    public IReadOnlyList<StatTile> BgStatTiles { get => _bgStatTiles; private set => Set(ref _bgStatTiles, value); }

    /// <summary>Share of games per final place (index 0 = 1st) as one bar per place, empty without games.</summary>
    public IReadOnlyList<BgPlaceRow> BgPlaces { get => _bgPlaces; private set => Set(ref _bgPlaces, value); }

    public IReadOnlyList<IChartValue> BgRatingChart { get => _bgRatingChart; private set => Set(ref _bgRatingChart, value); }

    /// <summary>«Результаты героев»: the combat win rate and pick rate columns; off by default.</summary>
    public bool BgHeroAllColumns { get => _bgHeroAllColumns; set => Set(ref _bgHeroAllColumns, value); }

    public IReadOnlyList<BgHeroPerfRow> BgHeroPerf { get => _bgHeroPerf; private set => Set(ref _bgHeroPerf, value); }

    public string BgStatsNote { get => _bgStatsNote; private set => Set(ref _bgStatsNote, value); }

    /// <summary>The page's headline numbers: win rate, games, rank, time played (at most four at the top of a page).</summary>
    public IReadOnlyList<StatTile> CtStatTiles { get => _ctStatTiles; private set => Set(ref _ctStatTiles, value); }

    /// <summary>The rest, under «Подробнее»: going first and on the coin.</summary>
    public IReadOnlyList<StatTile> CtMoreTiles { get => _ctMoreTiles; private set => Set(ref _ctMoreTiles, value); }

    public IReadOnlyList<IChartValue> CtRankChart { get => _ctRankChart; private set => Set(ref _ctRankChart, value); }

    // The class and archetype tables sort by their column heads (most played first until one is clicked).
    public TableSort CtClassesSort => _ctClassesSort ??= GroupSort(() => CtClasses = CtClassesSort.Apply(CtClasses, (r, c) => r.SortValue(c)));

    public TableSort CtOpponentsSort => _ctOpponentsSort ??= GroupSort(() => CtOpponents = CtOpponentsSort.Apply(CtOpponents, (r, c) => r.SortValue(c)));

    public TableSort CtArchetypesSort => _ctArchetypesSort ??= GroupSort(() => CtArchetypes = CtArchetypesSort.Apply(CtArchetypes, (r, c) => r.SortValue(c)));

    public TableSort ArenaClassesSort => _arenaClassesSort ??= GroupSort(() => ArenaClasses = ArenaClassesSort.Apply(ArenaClasses, (r, c) => r.SortValue(c)));

    public TableSort ArenaOpponentsSort => _arenaOpponentsSort ??= GroupSort(() => ArenaOpponents = ArenaOpponentsSort.Apply(ArenaOpponents, (r, c) => r.SortValue(c)));

    /// <summary>How many summary numbers a page shows at its top; the rest wait under «Подробнее».</summary>
    internal const int HeadlineTiles = 4;

    private static TableSort GroupSort(Action changed) => new("Games", changed, "Name");

    public IReadOnlyList<StatGroupRow> CtClasses { get => _ctClasses; private set => Set(ref _ctClasses, value); }

    public IReadOnlyList<StatGroupRow> CtOpponents { get => _ctOpponents; private set => Set(ref _ctOpponents, value); }

    public IReadOnlyList<StatGroupRow> CtArchetypes { get => _ctArchetypes; private set => Set(ref _ctArchetypes, value); }

    public IReadOnlyList<StatGroupRow> CtLengths { get => _ctLengths; private set => Set(ref _ctLengths, value); }

    public string CtStatsNote { get => _ctStatsNote; private set => Set(ref _ctStatsNote, value); }

    /// <summary>The page's headline numbers: runs, average wins, best run, win rate.</summary>
    public IReadOnlyList<StatTile> ArenaStatTiles { get => _arenaStatTiles; private set => Set(ref _arenaStatTiles, value); }

    /// <summary>The rest, under «Подробнее»: time played and the rewards' total.</summary>
    public IReadOnlyList<StatTile> ArenaMoreTiles { get => _arenaMoreTiles; private set => Set(ref _arenaMoreTiles, value); }

    public IReadOnlyList<ArenaRunRow> ArenaRuns { get => _arenaRuns; private set => Set(ref _arenaRuns, value); }

    public IReadOnlyList<StatGroupRow> ArenaClasses { get => _arenaClasses; private set => Set(ref _arenaClasses, value); }

    public IReadOnlyList<StatGroupRow> ArenaOpponents { get => _arenaOpponents; private set => Set(ref _arenaOpponents, value); }

    // My Stats work happens off the UI thread; each page drops a result that a newer rebuild has overtaken.
    private readonly int[] _statsBuilds = new int[3];
    private int _statsLoad;
    private volatile bool _statsStale = true;
    private bool _statsWatching;
    private string? _statsBgMode;
    private CardDb? _statsCards;

    /// <summary>
    /// Brings the My Stats pages up to date: reloads every game only after the history changed, otherwise rebuilds
    /// just what a new card database or Solo/Duos switch affects (other refreshes, e.g. the History filter, cost nothing).
    /// </summary>
    private async Task RefreshMyStatsAsync()
    {
        if (!_statsWatching)
        {
            _statsWatching = true;
            _history.Changed += () => _statsStale = true;
        }

        if (_statsStale)
        {
            _statsStale = false;
            var load = ++_statsLoad;
            var games = await Task.Run(() => _history.Recent(int.MaxValue)).ConfigureAwait(true);
            if (load != _statsLoad)
            {
                return;
            }

            _allGames = games;
            RebuildMyStats();
            RaisePageHero();
        }
        else if (!ReferenceEquals(_statsCards, _cards))
        {
            RebuildMyStats();
        }
        else if (_statsBgMode != BgMode)
        {
            RebuildBattlegroundsStats();
        }
    }

    private void RebuildMyStats()
    {
        RebuildBattlegroundsStats();
        RebuildConstructedStats();
        RebuildArenaStats();
        RebuildBrawlStats();
        RebuildDecks();
    }

    /// <summary>Builds a page off the UI thread; the returned action applies the result unless a newer build started.</summary>
    private readonly Lock _statsBuildGate = new();

    private void RunStatsBuild(int page, Func<Action> build) => BuildStatsPageAsync(page, build).Forget("My Stats build");

    private async Task BuildStatsPageAsync(int page, Func<Action> build)
    {
        int generation;
        lock (_statsBuildGate)
        {
            generation = ++_statsBuilds[page];
        }

        try
        {
            var apply = await Task.Run(build).ConfigureAwait(true);
            // Without a UI thread (tests, renders before the window) two builds can finish on two threads: the check and
            // the apply go together, so a stale build never lands after a newer one.
            lock (_statsBuildGate)
            {
                if (generation == _statsBuilds[page])
                {
                    apply();
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Error("My Stats build failed", ex);
        }
    }

    private static StatsPeriod ParsePeriod(object? p) => Enum.TryParse<StatsPeriod>(p as string, out var period) ? period : StatsPeriod.All;

    /// <summary>The loaded Constructed meta when it is the tab's format (else nothing to compare with).</summary>
    private ConstructedMeta? ConstructedMetaFor(string format) =>
        ConstructedMeta.FormatKeyOf(format) is { } key && _constructedMetas.TryGetValue(key, out var meta) ? meta : null;

    private static StatGroupRow Group(GroupStats g, string cardId, string label) => new(
        label,
        cardId,
        Loc.P("Stats.Games", g.Record.Games),
        $"{g.Record.Wins}–{g.Record.Losses}",
        Percent(g.Record.WinRate),
        g.Record.WinRate ?? 0,
        VsGlobal.Of(g.Record.WinRate, g.GlobalWinRate, lowerIsBetter: false, "0", percent: true),
        g.Record.Games);

    private static string Percent(double? value) => value is { } v ? v.ToString("P0", CultureInfo.CurrentCulture) : "—";

    private static string Signed(int value) => value > 0 ? $"+{value:N0}" : value < 0 ? $"−{-value:N0}" : "±0";

    private static string Hours(TimeSpan time) => time.TotalHours >= 1
        ? Loc.F("Stats.Hours", (int)time.TotalHours, time.Minutes)
        : Loc.F("Stats.Minutes", (int)time.TotalMinutes);

    partial void OnShowingConstructedFormat(string formatType);

    partial void OnConstructedTitleChanged();

    partial void OnConstructedFormatChanged();

    partial void OnConstructedFormatShown();
}
