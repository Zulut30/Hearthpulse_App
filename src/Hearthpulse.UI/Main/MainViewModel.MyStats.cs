using System.Globalization;
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.UI.Main;

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

/// <summary>A ranked game on the rank chart, with what its hover card shows.</summary>
public sealed record CtGamePoint(
    double ChartValue,
    string ChartLabel,
    string HeroCardId,
    string Title,
    string Result,
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
public sealed record ArenaRunRow(string ClassName, string HeroCardId, string Record, double WinFraction, string When, string Status, bool Finished);

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
    private RelayCommand? _setCtFormat;
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

    public RelayCommand SetCtFormat => _setCtFormat ??= new RelayCommand(p => { _ctFormat = p as string ?? "FT_STANDARD"; RebuildConstructedStats(); });

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

    /// <summary>Reloads every game for the My Stats pages and rebuilds them.</summary>
    private async Task RefreshMyStatsAsync()
    {
        _allGames = await Task.Run(() => _history.Recent(int.MaxValue)).ConfigureAwait(true);
        RebuildMyStats();
    }

    private void RebuildMyStats()
    {
        RebuildBattlegroundsStats();
        RebuildConstructedStats();
        RebuildArenaStats();
    }

    private static StatsPeriod ParsePeriod(object? p) => Enum.TryParse<StatsPeriod>(p as string, out var period) ? period : StatsPeriod.All;

    private void RebuildBattlegroundsStats()
    {
        var duos = BgMode == "Duos";
        var games = StatsFilter.Apply(_allGames.Where(g => BattlegroundsStats.IsGame(g, duos)), _bgPeriod, DateTime.Now);
        var summary = BattlegroundsStats.Summary(games, duos);
        BgStatTiles =
        [
            new StatTile("Highest MMR", summary.HighestRating is { } top ? top.ToString("N0", CultureInfo.CurrentCulture) : "—",
                summary.NetRating is { } net ? $"{Signed(net)} net" : "rating not read yet", Math.Sign(summary.NetRating ?? 0)),
            new StatTile("Avg. placement", summary.AveragePlacement is { } avg ? avg.ToString("0.00", CultureInfo.CurrentCulture) : "—", "lower is better"),
            new StatTile("Total games", summary.Games.ToString("N0", CultureInfo.CurrentCulture), duos ? "Duos" : "Solo"),
            new StatTile("Time played", Hours(summary.TimePlayed), summary.Games > 0 ? $"{(summary.TimePlayed.TotalMinutes / summary.Games):0} min a game" : "—"),
        ];
        var total = Math.Max(1, summary.PlacementCounts.Sum());
        BgPlaceShares = summary.PlacementCounts.Select(c => c / (double)total).ToList();
        BgAveragePlace = summary.AveragePlacement ?? double.NaN;

        BgRatingChart = BattlegroundsStats.RatingTimeline(games)
            .Select(p => (IChartValue)new BgGamePoint(
                p.Rating,
                p.Rating.ToString("N0", CultureInfo.CurrentCulture),
                p.Game.HeroCardId,
                _cards.NameOf(p.Game.HeroCardId),
                Ordinal(p.Game.Placement),
                p.Game.Placement <= (duos ? 2 : 4) ? "Win" : "Loss",
                p.Delta is { } d ? Signed(d) : string.Empty,
                p.Delta ?? 0,
                When(p.Game.StartedAt),
                BattlegroundsStats.FinalBoard(p.Game)
                    .Select(m => new BoardMinionRow(m.CardId, _cards.NameOf(m.CardId), m.Attack, m.Health, m.Golden))
                    .ToList()))
            .ToList();

        var meta = _meta;
        var heroes = BattlegroundsStats.Heroes(games, _cards.BaseHeroId, duos,
            meta is null ? null : id => BattlegroundsStats.GlobalOf(meta.Hero(id, 100)));
        var shown = BattlegroundsStats.Sort(heroes, _bgSort, _bgMinGames);
        var maxPick = Math.Max(0.01, shown.Select(h => Math.Max(h.PickRate ?? 0, h.Global?.PickRate ?? 0)).DefaultIfEmpty(0).Max());
        var places = duos ? 4 : 8;
        BgHeroPerf = shown.Select(h =>
            {
                var turns = Math.Max(h.CombatWinRate.Keys.DefaultIfEmpty(0).Max(), h.Global?.CombatWinRate?.Keys.DefaultIfEmpty(0).Max() ?? 0);
                turns = Math.Min(turns, 16);
                var counted = Math.Max(1, h.PlacementCounts.Sum());
                return new BgHeroPerfRow(
                    h.HeroCardId,
                    _cards.NameOf(h.HeroCardId),
                    h.Games == 1 ? "1 game" : $"{h.Games:N0} games",
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
        BgHasGlobal = !duos && meta is not null;
        BgStatsNote = duos
            ? "Community numbers are for solo games, so Duos has no vs Global."
            : meta is null
                ? "Community numbers are loading…"
                : $"vs Global: {meta.Source}, all ranks. Pick rate counts games that saved the hero offer.";
    }

    private void RebuildConstructedStats()
    {
        var games = StatsFilter.Apply(_allGames.Where(g => StatsFilter.IsConstructed(g, _ctFormat)), _ctPeriod, DateTime.Now);
        var summary = ConstructedStats.Summary(games);
        CtStatTiles =
        [
            new StatTile("Win rate", Percent(summary.Record.WinRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile("Total games", summary.Record.Games.ToString("N0", CultureInfo.CurrentCulture), _ctFormat == "FT_WILD" ? "Wild" : "Standard"),
            new StatTile("Rank", summary.LatestRank ?? "—", summary.BestRank is { } best ? $"best {best}" : "ranked games show it"),
            new StatTile("Time played", Hours(summary.TimePlayed),
                summary.AverageDuration is { } avg ? $"{avg.TotalMinutes:0} min, {summary.AverageTurns:0.0} turns a game" : "—"),
            new StatTile("On the play", Percent(summary.OnPlay.WinRate), $"{summary.OnPlay.Wins}–{summary.OnPlay.Losses} going first"),
            new StatTile("On the coin", Percent(summary.OnCoin.WinRate), $"{summary.OnCoin.Wins}–{summary.OnCoin.Losses} going second"),
        ];

        CtRankChart = ConstructedStats.RankTimeline(games)
            .Select(r => (IChartValue)new CtGamePoint(
                r.StarLevel + (r.LegendRank is { } l ? 1 - Math.Min(1, l / 10000.0) : 0),
                r.Label,
                r.Game.HeroCardId,
                r.Game.DeckName is { Length: > 0 } deck ? deck : OverlayViewModel.ClassName(r.Game.PlayerClass),
                r.Game.Result,
                string.IsNullOrEmpty(r.Game.OpponentClass) ? $"{r.Game.Turns} turns" : $"vs {OverlayViewModel.ClassName(r.Game.OpponentClass)} · {r.Game.Turns} turns",
                When(r.Game.StartedAt)))
            .ToList();

        // This app has no Constructed meta yet, so there is nothing to compare with.
        Core.Constructed.ConstructedMeta? meta = null;
        CtClasses = ConstructedStats.ByPlayerClass(games, meta).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        CtOpponents = ConstructedStats.ByOpponentClass(games).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        CtArchetypes = ConstructedStats.ByArchetype(games, g => ConstructedStats.ArchetypeOf(g.DeckCode, g.PlayerClass, meta, _cards))
            .Select(g => Group(g, string.Empty, g.Label)).ToList();
        CtLengths = ConstructedStats.ByLength(games).Select(b => Group(new GroupStats(b.Label, $"{b.Label} turns", b.Record), string.Empty, $"{b.Label} turns")).ToList();
        CtStatsNote = "vs Global needs the Constructed community meta, which the Windows app loads.";
    }

    private void RebuildArenaStats()
    {
        var games = StatsFilter.Apply(_allGames.Where(StatsFilter.IsArena), _arenaPeriod, DateTime.Now);
        var runs = ArenaStats.Runs(games);
        var summary = ArenaStats.Summary(runs);
        ArenaStatTiles =
        [
            new StatTile("Runs", summary.Runs.ToString("N0", CultureInfo.CurrentCulture), $"{runs.Count(r => r.Finished)} finished"),
            new StatTile("Avg. wins", summary.AverageWins is { } avg ? avg.ToString("0.0", CultureInfo.CurrentCulture) : "—", "per finished run"),
            new StatTile("Best run", runs.Count > 0 ? $"{summary.BestWins} wins" : "—", "most wins in one run"),
            new StatTile("Win rate", Percent(summary.Record.WinRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile("Time played", Hours(summary.TimePlayed), $"{summary.Record.Games:N0} games"),
        ];
        ArenaRuns = runs.AsEnumerable().Reverse().Select(r => new ArenaRunRow(
                OverlayViewModel.ClassName(r.PlayerClass),
                ClassHero(r.PlayerClass),
                $"{r.Wins}–{r.Losses}",
                r.Wins / (double)ArenaStats.MaxWins,
                When(r.Started),
                r.Finished ? $"{r.Games.Count} games" : $"{r.Games.Count} games · in progress",
                r.Finished))
            .ToList();
        ArenaClasses = ArenaStats.ByClass(runs)
            .Select(c => new StatGroupRow(
                OverlayViewModel.ClassName(c.PlayerClass),
                ClassHero(c.PlayerClass),
                (c.Runs == 1 ? "1 run" : $"{c.Runs} runs") + (c.AverageWins is { } w ? $" · {w:0.0} avg wins" : " · none finished"),
                $"{c.Record.Wins}–{c.Record.Losses}",
                Percent(c.Record.WinRate),
                c.Record.WinRate ?? 0,
                VsGlobal.None))
            .ToList();
        ArenaOpponents = ConstructedStats.ByOpponentClass(games).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
    }

    private static StatGroupRow Group(GroupStats g, string cardId, string label) => new(
        label,
        cardId,
        g.Record.Games == 1 ? "1 game" : $"{g.Record.Games:N0} games",
        $"{g.Record.Wins}–{g.Record.Losses}",
        Percent(g.Record.WinRate),
        g.Record.WinRate ?? 0,
        VsGlobal.Of(g.Record.WinRate, g.GlobalWinRate, lowerIsBetter: false, "0", 100, "%"));

    /// <summary>The class's default hero, for its portrait.</summary>
    private static string ClassHero(string heroClass) => heroClass.ToUpperInvariant() switch
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

    private static string Percent(double? value) => value is { } v ? v.ToString("P0", CultureInfo.CurrentCulture) : "—";

    private static string Signed(int value) => value > 0 ? $"+{value:N0}" : value < 0 ? $"−{-value:N0}" : "±0";

    private static string Hours(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours}h {time.Minutes:00}m"
        : $"{(int)time.TotalMinutes}m";
}
