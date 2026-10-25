using System.Globalization;
using Hearthpulse.App.Controls;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.App.Main;

/// <summary>A minion of the final board in the rating chart's hover card.</summary>
public sealed record BoardMinionRow(string CardId, string Name, int Attack, int Health, bool Golden);

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
    IReadOnlyList<BoardMinionRow> Board) : IChartValue;

/// <summary>A ranked game on the rank chart, with what its hover card shows (<see cref="Result"/> is the saved "Win"/"Loss" for its colour).</summary>
public sealed record CtGamePoint(
    double ChartValue,
    string ChartLabel,
    string HeroCardId,
    string Title,
    string Result,
    string ResultText,
    string Detail,
    string When) : IChartValue;

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
    VsGlobal PickVs);

/// <summary>
/// A "vs Global" difference: the text (e.g. "0.42"), an arrow for its direction and whether it is good for the
/// player (+1), bad (-1) or neutral (0). <see cref="Shown"/> is false when there is no community number.
/// </summary>
public sealed record VsGlobal(bool Shown, string Text, string Glyph, int Sign)
{
    public static VsGlobal None { get; } = new(false, string.Empty, string.Empty, 0);

    /// <summary>Difference of <paramref name="mine"/> to <paramref name="global"/>; <paramref name="lowerIsBetter"/> for placements.</summary>
    public static VsGlobal Of(double? mine, double? global, bool lowerIsBetter, string format, double scale = 1, string suffix = "")
    {
        if (mine is not { } m || global is not { } g)
        {
            return None;
        }

        var diff = (m - g) * scale;
        var rounded = Math.Round(diff, format == "0.00" ? 2 : 0);
        var sign = rounded == 0 ? 0 : (rounded < 0) == lowerIsBetter ? 1 : -1;
        var glyph = rounded > 0 ? "\U000F0360" : rounded < 0 ? "\U000F035D" : "\U000F0374";
        return new VsGlobal(true, Math.Abs(diff).ToString(format, CultureInfo.CurrentCulture) + suffix, glyph, sign);
    }
}

/// <summary>A group in a My Stats table (class, opponent class, archetype, game length).</summary>
public sealed record StatGroupRow(string Label, string CardId, string Games, string Record, string WinRate, double WinFraction, VsGlobal Vs);

/// <summary>One Arena run.</summary>
/// <param name="DeckScore">"Deck score 41" (ArenaSmith average of the run's deck), or empty.</param>
public sealed record ArenaRunRow(string ClassName, string HeroCardId, string Record, double WinFraction, string When, string Status, bool Finished, string DeckScore = "", string DeckTier = "");

public sealed partial class MainViewModel
{
    private IReadOnlyList<GameRecord> _allGames = [];
    private StatsPeriod _bgPeriod = StatsPeriod.All;
    private BgHeroSort _bgSort = BgHeroSort.Games;
    private int _bgMinGames = 1;
    private IReadOnlyList<StatTile> _bgStatTiles = [];
    private IReadOnlyList<double> _bgPlaceShares = [];
    private double _bgAveragePlace = double.NaN;
    private IReadOnlyList<IChartValue> _bgRatingChart = [];
    private IReadOnlyList<BgHeroPerfRow> _bgHeroPerf = [];
    private bool _bgHasGlobal;
    private string _bgStatsNote = string.Empty;

    private string _ctFormat = "FT_STANDARD";
    private StatsPeriod _ctPeriod = StatsPeriod.All;
    private IReadOnlyList<StatTile> _ctStatTiles = [];
    private IReadOnlyList<IChartValue> _ctRankChart = [];
    private IReadOnlyList<StatGroupRow> _ctClasses = [];
    private IReadOnlyList<StatGroupRow> _ctOpponents = [];
    private IReadOnlyList<StatGroupRow> _ctArchetypes = [];
    private IReadOnlyList<StatGroupRow> _ctLengths = [];
    private string _ctStatsNote = string.Empty;

    private StatsPeriod _arenaPeriod = StatsPeriod.All;
    private IReadOnlyList<StatTile> _arenaStatTiles = [];
    private IReadOnlyList<ArenaRunRow> _arenaRuns = [];
    private IReadOnlyList<StatGroupRow> _arenaClasses = [];
    private IReadOnlyList<StatGroupRow> _arenaOpponents = [];

    private RelayCommand? _setBgPeriod;
    private RelayCommand? _setBgSort;
    private RelayCommand? _setBgMinGames;
    private RelayCommand? _setCtPeriod;
    private RelayCommand? _setArenaPeriod;

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

    /// <summary>The page title: Standard or Wild.</summary>
    public string ConstructedTitle => Loc.T(_ctFormat == "FT_WILD" ? "Main.Nav.Wild" : "Main.Nav.Standard");

    /// <summary>
    /// Shows the Constructed page for one FormatType (the Standard and Wild nav items): my stats, your decks and the meta
    /// follow it. An open deck of the other format closes.
    /// </summary>
    public void ShowConstructedFormat(string formatType)
    {
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
            RebuildConstructedStats();
            RebuildDecks();
            RebuildConstructedMeta();
        }

        // The tier list and top decks follow the page; the overlay keeps the stats of the game's own format.
        RequestPageMeta();
    }

    public RelayCommand SetCtPeriod => _setCtPeriod ??= new RelayCommand(p => { _ctPeriod = ParsePeriod(p); RebuildConstructedStats(); });

    public RelayCommand SetArenaPeriod => _setArenaPeriod ??= new RelayCommand(p => { _arenaPeriod = ParsePeriod(p); RebuildArenaStats(); });

    public IReadOnlyList<StatTile> BgStatTiles { get => _bgStatTiles; private set => Set(ref _bgStatTiles, value); }

    /// <summary>Share of games per final place (index 0 = 1st), for the header histogram.</summary>
    public IReadOnlyList<double> BgPlaceShares { get => _bgPlaceShares; private set => Set(ref _bgPlaceShares, value); }

    /// <summary>Average place for the histogram marker, NaN without games.</summary>
    public double BgAveragePlace { get => _bgAveragePlace; private set => Set(ref _bgAveragePlace, value); }

    public IReadOnlyList<IChartValue> BgRatingChart { get => _bgRatingChart; private set => Set(ref _bgRatingChart, value); }

    public IReadOnlyList<BgHeroPerfRow> BgHeroPerf { get => _bgHeroPerf; private set => Set(ref _bgHeroPerf, value); }

    /// <summary>True when community numbers are there to compare with (solo, meta loaded).</summary>
    public bool BgHasGlobal { get => _bgHasGlobal; private set => Set(ref _bgHasGlobal, value); }

    public string BgStatsNote { get => _bgStatsNote; private set => Set(ref _bgStatsNote, value); }

    private string _bgSimAccuracy = string.Empty;

    /// <summary>One line on how well the combat simulator predicted these games' fights; empty without predictions.</summary>
    public string BgSimAccuracy { get => _bgSimAccuracy; private set => Set(ref _bgSimAccuracy, value); }

    public IReadOnlyList<StatTile> CtStatTiles { get => _ctStatTiles; private set => Set(ref _ctStatTiles, value); }

    public IReadOnlyList<IChartValue> CtRankChart { get => _ctRankChart; private set => Set(ref _ctRankChart, value); }

    public IReadOnlyList<StatGroupRow> CtClasses { get => _ctClasses; private set => Set(ref _ctClasses, value); }

    public IReadOnlyList<StatGroupRow> CtOpponents { get => _ctOpponents; private set => Set(ref _ctOpponents, value); }

    public IReadOnlyList<StatGroupRow> CtArchetypes { get => _ctArchetypes; private set => Set(ref _ctArchetypes, value); }

    public IReadOnlyList<StatGroupRow> CtLengths { get => _ctLengths; private set => Set(ref _ctLengths, value); }

    public string CtStatsNote { get => _ctStatsNote; private set => Set(ref _ctStatsNote, value); }

    public IReadOnlyList<StatTile> ArenaStatTiles { get => _arenaStatTiles; private set => Set(ref _arenaStatTiles, value); }

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
        RebuildDecks();
    }

    /// <summary>Builds a page off the UI thread; the returned action applies the result unless a newer build started.</summary>
    private async void RunStatsBuild(int page, Func<Action> build)
    {
        var generation = ++_statsBuilds[page];
        try
        {
            var apply = await Task.Run(build).ConfigureAwait(true);
            if (generation == _statsBuilds[page])
            {
                apply();
            }
        }
        catch (Exception ex)
        {
            Diag.Error("My Stats build failed", ex);
        }
    }

    private static StatsPeriod ParsePeriod(object? p) => Enum.TryParse<StatsPeriod>(p as string, out var period) ? period : StatsPeriod.All;

    private void RebuildBattlegroundsStats()
    {
        var (all, mode, period, sort, minGames, cards, meta) = (_allGames, BgMode, _bgPeriod, _bgSort, _bgMinGames, _cards, _meta);
        _statsBgMode = mode;
        _statsCards = cards;
        RunStatsBuild(0, () => BuildBattlegroundsStats(all, mode == "Duos", period, sort, minGames, cards, meta));
    }

    private Action BuildBattlegroundsStats(IReadOnlyList<GameRecord> all, bool duos, StatsPeriod period, BgHeroSort sort, int minGames, CardDb cards, BattlegroundsMeta? meta)
    {
        var games = StatsFilter.Apply(all.Where(g => BattlegroundsStats.IsGame(g, duos)), period, DateTime.Now);
        var summary = BattlegroundsStats.Summary(games, duos);
        var timed = Math.Max(1, games.Count(g => g.Duration > TimeSpan.Zero));
        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Stats.Bg.HighestMmr"), summary.HighestRating is { } top ? top.ToString("N0", CultureInfo.CurrentCulture) : "—",
                summary.NetRating is { } net ? Loc.F("Stats.Bg.NetDetail", Signed(net)) : Loc.T("Stats.Bg.RatingNotRead"), Math.Sign(summary.NetRating ?? 0)),
            new StatTile(Loc.T("Stats.Bg.AvgPlacement"), summary.AveragePlacement is { } avg ? avg.ToString("0.00", CultureInfo.CurrentCulture) : "—", Loc.T("Stats.Bg.LowerIsBetter")),
            new StatTile(Loc.T("Stats.TotalGames"), summary.Games.ToString("N0", CultureInfo.CurrentCulture), Loc.T(duos ? "Stats.Bg.Duos" : "Stats.Bg.Solo")),
            new StatTile(Loc.T("Stats.TimePlayed"), Hours(summary.TimePlayed), summary.Games > 0 ? Loc.F("Stats.Bg.MinPerGame", summary.TimePlayed.TotalMinutes / timed) : "—"),
        ];
        var total = Math.Max(1, summary.PlacementCounts.Sum());
        var shares = summary.PlacementCounts.Select(c => c / (double)total).ToList();

        var chart = BattlegroundsStats.RatingTimeline(games)
            .Select(p => (IChartValue)new BgGamePoint(
                p.Rating,
                p.Rating.ToString("N0", CultureInfo.CurrentCulture),
                p.Game.HeroCardId,
                cards.NameOf(p.Game.HeroCardId),
                Ordinal(p.Game.Placement),
                p.Game.Placement <= (duos ? 2 : 4) ? "Win" : "Loss",
                p.Delta is { } d ? Signed(d) : string.Empty,
                p.Delta ?? 0,
                When(p.Game.StartedAt),
                BattlegroundsStats.FinalBoard(p.Game)
                    .Select(m => new BoardMinionRow(m.CardId, cards.NameOf(m.CardId), m.Attack, m.Health, m.Golden))
                    .ToList()))
            .ToList();

        var heroes = BattlegroundsStats.Heroes(games, cards.BaseHeroId, duos,
            meta is null ? null : id => BattlegroundsStats.GlobalOf(meta.Hero(id, meta.WidestBracket)));
        var shown = BattlegroundsStats.Sort(heroes, sort, minGames);
        var maxPick = Math.Max(0.01, shown.Select(h => Math.Max(h.PickRate ?? 0, h.Global?.PickRate ?? 0)).DefaultIfEmpty(0).Max());
        var places = duos ? 4 : 8;
        var rows = shown.Select(h =>
            {
                var turns = Math.Max(h.CombatWinRate.Keys.DefaultIfEmpty(0).Max(), h.Global?.CombatWinRate?.Keys.DefaultIfEmpty(0).Max() ?? 0);
                turns = Math.Min(turns, 16);
                var counted = Math.Max(1, h.PlacementCounts.Sum());
                return new BgHeroPerfRow(
                    h.HeroCardId,
                    cards.NameOf(h.HeroCardId),
                    Loc.P("Stats.Games", h.Games),
                    h.NetRating is { } n ? Signed(n) : "—",
                    h.NetRating ?? 0,
                    Enumerable.Range(1, turns).Select(t => h.CombatWinRate.TryGetValue(t, out var v) ? v : (double?)null).ToList(),
                    h.Global?.CombatWinRate is { } gc ? Enumerable.Range(1, turns).Select(t => gc.TryGetValue(t, out var v) ? v : (double?)null).ToList() : null,
                    h.PlacementCounts.Select(c => c / (double)counted).ToList(),
                    h.Global?.PlacementShares,
                    h.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                    1 - ((h.AveragePlacement - 1) / (places - 1)),
                    VsGlobal.Of(h.AveragePlacement, h.Global?.AveragePlacement, lowerIsBetter: true, "0.00"),
                    h.PickRate is { } pick ? pick.ToString("P0", CultureInfo.CurrentCulture) : "—",
                    (h.PickRate ?? 0) / maxPick,
                    VsGlobal.Of(h.PickRate, h.Global?.PickRate, lowerIsBetter: false, "0", 100, "%"));
            })
            .ToList();
        var note = duos
            ? Loc.T("Stats.Bg.NoteDuos")
            : meta is null
                ? Loc.T("Stats.Bg.NoteLoading")
                : Loc.F("Stats.Bg.NoteSource", meta.Source, BattlegroundsMeta.BracketLabel(meta.WidestBracket));
        // Duos fights are not predicted with damage (the loser of one board is not the one who takes it).
        var accuracy = duos ? null : BattlegroundsStats.SimAccuracy(games);
        var accuracyText = accuracy is not { Fights: > 0 }
            ? string.Empty
            : Loc.F("Stats.Bg.SimAccuracy", accuracy.DamageRate is { } dr ? dr.ToString("P0", CultureInfo.CurrentCulture) : "—",
                accuracy.ResultRate!.Value.ToString("P0", CultureInfo.CurrentCulture), accuracy.Fights);
        return () =>
        {
            BgStatTiles = tiles;
            BgSimAccuracy = accuracyText;
            BgPlaceShares = shares;
            BgAveragePlace = summary.AveragePlacement ?? double.NaN;
            BgRatingChart = chart;
            BgHeroPerf = rows;
            BgHasGlobal = !duos && meta is not null;
            BgStatsNote = note;
        };
    }

    private void RebuildConstructedStats()
    {
        var (all, format, period, cards) = (_allGames, _ctFormat, _ctPeriod, _cards);
        var meta = ConstructedMetaFor(format);
        _statsCards = cards;
        RunStatsBuild(1, () => BuildConstructedStats(all, format, period, cards, meta));
    }

    private Action BuildConstructedStats(IReadOnlyList<GameRecord> all, string format, StatsPeriod period, CardDb cards, ConstructedMeta? meta)
    {
        var games = StatsFilter.Apply(all.Where(g => StatsFilter.IsConstructed(g, format)), period, DateTime.Now);
        var summary = ConstructedStats.Summary(games);
        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Stats.WinRate"), Percent(summary.Record.WinRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile(Loc.T("Stats.TotalGames"), summary.Record.Games.ToString("N0", CultureInfo.CurrentCulture), Loc.T(format == "FT_WILD" ? "Stats.Ct.Wild" : "Stats.Ct.Standard")),
            new StatTile(Loc.T("Stats.Ct.Rank"), summary.LatestRank ?? "—", summary.BestRank is { } best ? Loc.F("Stats.Ct.BestRank", best) : Loc.T("Stats.Ct.RankHidden")),
            new StatTile(Loc.T("Stats.TimePlayed"), Hours(summary.TimePlayed),
                summary.AverageDuration is { } avg ? Loc.F("Stats.Ct.AverageGame", avg.TotalMinutes, summary.AverageTurns) : "—"),
            new StatTile(Loc.T("Stats.Ct.OnPlay"), Percent(summary.OnPlay.WinRate), Loc.F("Stats.Ct.OnPlayDetail", summary.OnPlay.Wins, summary.OnPlay.Losses)),
            new StatTile(Loc.T("Stats.Ct.OnCoin"), Percent(summary.OnCoin.WinRate), Loc.F("Stats.Ct.OnCoinDetail", summary.OnCoin.Wins, summary.OnCoin.Losses)),
        ];

        var chart = ConstructedStats.RankTimeline(games)
            .Select(r => (IChartValue)new CtGamePoint(
                r.StarLevel + (r.LegendRank is { } l ? 1 - Math.Min(1, l / 10000.0) : 0),
                r.Label,
                r.Game.HeroCardId,
                r.Game.DeckName is { Length: > 0 } deck ? deck : OverlayViewModel.ClassName(r.Game.PlayerClass),
                r.Game.Result,
                ResultText(r.Game.Result),
                string.IsNullOrEmpty(r.Game.OpponentClass)
                    ? Loc.P("Stats.Turns", r.Game.Turns)
                    : Loc.F("Stats.Ct.VsOpponent", OverlayViewModel.ClassName(r.Game.OpponentClass), Loc.P("Stats.Turns", r.Game.Turns)),
                When(r.Game.StartedAt)))
            .ToList();

        var classes = ConstructedStats.ByPlayerClass(games, meta).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        var opponents = ConstructedStats.ByOpponentClass(games).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        var archetypes = ConstructedStats.ByArchetype(games, g => ConstructedStats.ArchetypeOf(g.DeckCode, g.PlayerClass, meta, cards))
            .Select(g => Group(g, string.Empty, g.Label)).ToList();
        IReadOnlyList<StatGroupRow> lengths = games.Count == 0
            ? []
            : ConstructedStats.ByLength(games).Select(b => Group(new GroupStats(b.Label, Loc.F("Stats.Ct.LengthTurns", b.Label), b.Record), string.Empty, Loc.F("Stats.Ct.LengthTurns", b.Label))).ToList();
        var note = ConstructedNote(meta);
        return () =>
        {
            CtStatTiles = tiles;
            CtRankChart = chart;
            CtClasses = classes;
            CtOpponents = opponents;
            CtArchetypes = archetypes;
            CtLengths = lengths;
            CtStatsNote = note;
        };
    }

    private void RebuildArenaStats()
    {
        var (all, period, cards, arena) = (_allGames, _arenaPeriod, _cards, _arenaMeta);
        RunStatsBuild(2, () => BuildArenaStats(all, period, cards, arena));
    }

    private Action BuildArenaStats(IReadOnlyList<GameRecord> all, StatsPeriod period, CardDb cards, Core.Arena.ArenaMeta? arena)
    {
        var games = StatsFilter.Apply(all.Where(StatsFilter.IsArena), period, DateTime.Now);
        var runs = ArenaStats.Runs(games);
        var summary = ArenaStats.Summary(runs);
        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Stats.Arena.Runs"), summary.Runs.ToString("N0", CultureInfo.CurrentCulture), Loc.F("Stats.Arena.Finished", runs.Count(r => r.Finished))),
            new StatTile(Loc.T("Stats.Arena.AvgWins"), summary.AverageWins is { } avg ? avg.ToString("0.0", CultureInfo.CurrentCulture) : "—", Loc.T("Stats.Arena.PerFinishedRun")),
            new StatTile(Loc.T("Stats.Arena.BestRun"), runs.Count > 0 ? Loc.P("Stats.Arena.Wins", summary.BestWins) : "—", Loc.T("Stats.Arena.MostWins")),
            new StatTile(Loc.T("Stats.WinRate"), Percent(summary.Record.WinRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile(Loc.T("Stats.TimePlayed"), Hours(summary.TimePlayed), Loc.P("Stats.Games", summary.Record.Games)),
        ];
        var runRows = runs.AsEnumerable().Reverse().Select(r =>
            {
                var (deckScore, deckTier) = RunDeckScore(r, cards, arena);
                return new ArenaRunRow(
                    OverlayViewModel.ClassName(r.PlayerClass),
                    ClassHero(r.PlayerClass),
                    $"{r.Wins}–{r.Losses}",
                    r.Wins / (double)ArenaStats.MaxWins,
                    When(r.Started),
                    r.Finished ? Loc.P("Stats.Games", r.Games.Count) : Loc.F("Stats.Arena.InProgress", Loc.P("Stats.Games", r.Games.Count)),
                    r.Finished,
                    deckScore,
                    deckTier);
            })
            .ToList();
        var applyDeck = BuildArenaDeck(runs, cards, arena);
        var classes = ArenaStats.ByClass(runs)
            .Select(c => new StatGroupRow(
                OverlayViewModel.ClassName(c.PlayerClass),
                ClassHero(c.PlayerClass),
                Loc.P("Stats.Arena.RunCount", c.Runs) + (c.AverageWins is { } w ? Loc.F("Stats.Arena.AvgWinsSuffix", w) : Loc.T("Stats.Arena.NoneFinished")),
                $"{c.Record.Wins}–{c.Record.Losses}",
                Percent(c.Record.WinRate),
                c.Record.WinRate ?? 0,
                VsGlobal.None))
            .ToList();
        var opponents = ConstructedStats.ByOpponentClass(games).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        return () =>
        {
            ArenaStatTiles = tiles;
            ArenaRuns = runRows;
            ArenaClasses = classes;
            ArenaOpponents = opponents;
            applyDeck(this);
        };
    }

    /// <summary>The loaded Constructed meta when it is the tab's format (else nothing to compare with).</summary>
    private ConstructedMeta? ConstructedMetaFor(string format) =>
        ConstructedMeta.FormatKeyOf(format) is { } key && _constructedMetas.TryGetValue(key, out var meta) ? meta : null;

    private static string ConstructedNote(ConstructedMeta? meta) => meta is null
        ? Loc.T("Stats.Ct.NoteNoMeta")
        : Loc.F("Stats.Ct.NoteSource", meta.Source, ConstructedMeta.Label(ConstructedMeta.Ranks, meta.Rank));

    private static StatGroupRow Group(GroupStats g, string cardId, string label) => new(
        label,
        cardId,
        Loc.P("Stats.Games", g.Record.Games),
        $"{g.Record.Wins}–{g.Record.Losses}",
        Percent(g.Record.WinRate),
        g.Record.WinRate ?? 0,
        VsGlobal.Of(g.Record.WinRate, g.GlobalWinRate, lowerIsBetter: false, "0", 100, "%"));

    private static string Percent(double? value) => value is { } v ? v.ToString("P0", CultureInfo.CurrentCulture) : "—";

    private static string Signed(int value) => value > 0 ? $"+{value:N0}" : value < 0 ? $"−{-value:N0}" : "±0";

    private static string Hours(TimeSpan time) => time.TotalHours >= 1
        ? Loc.F("Stats.Hours", (int)time.TotalHours, time.Minutes)
        : Loc.F("Stats.Minutes", (int)time.TotalMinutes);

}
