#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel : Observable
{
    private readonly HistoryStore _history;
    private CardDb _cards = CardDb.Empty;
    private BattlegroundsMeta? _meta;

    /// <param name="data">The data folder and its replay stores (<see cref="Core.Hosting.AppServices.Data"/>).</param>
    public MainViewModel(HistoryStore history, AppSettings settings, AppData data)
    {
        _history = history;
        Settings = settings;
        DataFolder = data.Root;
        LibraryFiles = data.Library;
        LibraryUploads = data.Uploads;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        SetBracket = new RelayCommand(p => SelectBgSlice(Convert.ToInt32(p, CultureInfo.InvariantCulture)));
        SetBgMode = new RelayCommand(p =>
        {
            BgMode = p as string ?? "Battlegrounds";
            BgModeChanged();
            RefreshAsync().Forget("History refresh");
        });
        SetHistoryFilter = new RelayCommand(p =>
        {
            HistoryFilter = p as string ?? "All";
            ApplyHistoryFilter();
        });
        SetCardTier = new RelayCommand(p =>
        {
            CardTier = Convert.ToInt32(p, CultureInfo.InvariantCulture);
            RebuildCardRows();
        });
        InstallUpdate = new RelayCommand(() => UpdateRequested?.Invoke());
#if HEARTHPULSE_WPF
        AnswerPool = new RelayCommand(p =>
        {
            PoolSharing = p as string == "yes";
            Raise(nameof(ShowGameStatsNotice));
        });
#endif
    }

    public RelayCommand SetCardTier { get; }

    public AppSettings Settings { get; }

    public RelayCommand SetBracket { get; }

    public RelayCommand SetBgMode { get; }

    public RelayCommand SetHistoryFilter { get; }

    public string Version { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    public string DataFolder { get; }

    // ---- live status ----------------------------------------------------------------------
    private LiveSnapshot? _lastSnapshot;
    private bool _hearthstoneRunning;
    private bool _memoryAttached;
    private string _memoryStatus = Loc.T("Main.Live.WaitingHs");
    private string _liveTitle = Loc.T("Main.Live.NotInGame");
    private string _liveDetail = Loc.T("Main.Live.StartHs");
    private bool _inGame;
    private string _soloRating = "—";
    private string _duosRating = "—";
    private string _sessionDelta = string.Empty;
    private int _sessionDeltaValue;
    private string _sessionSummary = Loc.T("Main.Session.None");
    private string _setupWarning = string.Empty;
    private string _busy = string.Empty;

    public bool HearthstoneRunning { get => _hearthstoneRunning; private set => Set(ref _hearthstoneRunning, value); }
    public bool MemoryAttached { get => _memoryAttached; private set => Set(ref _memoryAttached, value); }
    public string MemoryStatus { get => _memoryStatus; private set => Set(ref _memoryStatus, value); }
    public string LiveTitle { get => _liveTitle; private set => Set(ref _liveTitle, value); }
    public string LiveDetail { get => _liveDetail; private set => Set(ref _liveDetail, value); }
    public bool InGame { get => _inGame; private set => Set(ref _inGame, value); }
    public string SoloRating { get => _soloRating; private set => Set(ref _soloRating, value); }
    public string DuosRating { get => _duosRating; private set => Set(ref _duosRating, value); }
    public string SessionDelta { get => _sessionDelta; private set => Set(ref _sessionDelta, value); }
    public int SessionDeltaValue { get => _sessionDeltaValue; private set => Set(ref _sessionDeltaValue, value); }
    public string SessionSummary { get => _sessionSummary; private set => Set(ref _sessionSummary, value); }
    public string SetupWarning { get => _setupWarning; private set => Set(ref _setupWarning, value); }

    private string _parseErrorsText = string.Empty;

    /// <summary>Settings → About: Power.log lines the parser skipped since start (empty when none).</summary>
    public string ParseErrorsText { get => _parseErrorsText; private set => Set(ref _parseErrorsText, value); }
    public string Busy { get => _busy; set => Set(ref _busy, value); }

    // ---- history-derived ------------------------------------------------------------------
    private IReadOnlyList<GameRow> _recent = Array.Empty<GameRow>();
    private IReadOnlyList<GameRecord> _historyGames = [];
    private IReadOnlyList<GameRow>? _historyRows;

    // Refreshes run off the UI thread; only the newest one may apply its result.
    private int _refreshRequest;

    // The games the last refresh read; the history filter is applied to them.
    private IReadOnlyList<GameRecord>? _recentGames;
    private IReadOnlyList<StatTile> _overviewTiles = Array.Empty<StatTile>();
    private IReadOnlyList<RatingPoint> _ratingPoints = Array.Empty<RatingPoint>();
    private bool _hasBgGames;
    private IReadOnlyList<HeroRow> _myHeroes = Array.Empty<HeroRow>();
    private IReadOnlyList<DeckRow> _decks = Array.Empty<DeckRow>();
    private string _bgMode = "Battlegrounds";
    private string _historyFilter = "All";
    private string _metaUpdated = Loc.T("Main.Bg.CommunityLoading");
    private int _historyCount;

    public IReadOnlyList<GameRow> Recent
    {
        get => _recent;
        private set
        {
            if (Set(ref _recent, value))
            {
                OnRecentChanged();
                RaisePageHero();
            }
        }
    }

    /// <summary>
    /// History's games for the current filter as rows. No view binds them (History shows <see cref="Library"/>'s cards):
    /// they are built on first read, not for each of up to 300 games on every refresh and filter change.
    /// </summary>
    public IReadOnlyList<GameRow> HistoryRows => _historyRows ??= _historyGames.Select(Row).ToList();

    /// <summary>The games behind <see cref="HistoryRows"/>; a new list drops the rows built from the old one.</summary>
    private void SetHistoryGames(IReadOnlyList<GameRecord> games)
    {
        _historyGames = games;
        _historyRows = null;
        Raise(nameof(HistoryRows));
        RaisePageHero();
    }
    public IReadOnlyList<StatTile> OverviewTiles { get => _overviewTiles; private set => Set(ref _overviewTiles, value); }
    public IReadOnlyList<RatingPoint> RatingPoints { get => _ratingPoints; private set => Set(ref _ratingPoints, value); }

    /// <summary>Home shows the Battlegrounds rating and placement cards only once the mode has games to draw.</summary>
    public bool HasBgGames { get => _hasBgGames; private set => Set(ref _hasBgGames, value); }

    public IReadOnlyList<HeroRow> MyHeroes { get => _myHeroes; private set => Set(ref _myHeroes, value); }
    private IReadOnlyList<BracketOption> _bracketOptions = Array.Empty<BracketOption>();

    /// <summary>The MMR slices of the open tier tab's list; the picker shows only when there is a choice.</summary>
    public IReadOnlyList<BracketOption> BracketOptions { get => _bracketOptions; private set { if (Set(ref _bracketOptions, value)) Raise(nameof(HasBracketChoice)); } }

    public bool HasBracketChoice => _bracketOptions.Count > 1;

    private int _cardTier = 1;

    /// <summary>Tavern tier shown in the card list (0 = tavern spells).</summary>
    public int CardTier { get => _cardTier; private set => Set(ref _cardTier, value); }
    public IReadOnlyList<DeckRow> Decks { get => _decks; private set => Set(ref _decks, value); }
    public string BgMode { get => _bgMode; private set => Set(ref _bgMode, value); }
    public string HistoryFilter { get => _historyFilter; private set => Set(ref _historyFilter, value); }
    public string MetaUpdated { get => _metaUpdated; private set => Set(ref _metaUpdated, value); }
    public int HistoryCount { get => _historyCount; private set => Set(ref _historyCount, value); }

    public void SetCards(CardDb cards)
    {
        _cards = cards;
        _migration?.InputsChanged();
        RebuildOverlayPreview();
        Raise(nameof(LanguageRestartHint));
        RefreshAsync().Forget("History refresh");
        RebuildMeta();
    }

    public void SetMeta(BattlegroundsMeta? meta)
    {
        _meta = meta;
        ClearBattlegroundsMetaFailure();
        RebuildMeta();
        RebuildBattlegroundsStats();
    }

    public void Apply(LiveSnapshot s)
    {
        _lastSnapshot = s;
        ApplyLadder(s);
        ShowDiscord(s);
        HearthstoneRunning = s.HearthstoneRunning;
        MemoryAttached = s.MemoryAttached;
        // The pill says where the connection is in the interface's language; the memory reader's own words go to the log.
        MemoryStatus = Loc.T(s.MemoryAttached ? "Main.Status.Attached" : s.HearthstoneRunning ? "Main.Live.HsRunning" : "Main.Live.WaitingHs");
        InGame = s.InGame;
        SetupWarning = s.LogConfigMissing.Count > 0
            ? Loc.F("Main.Setup.LoggingEnabled", string.Join(", ", s.LogConfigMissing))
            : string.Empty;
        LogsReady = s.HearthstoneRunning && s.LogConfigMissing.Count == 0;
        ParseErrorsText = s.ParseErrors is { } errors
            ? Loc.F("Main.Setup.ParseErrors", errors.Total, errors.ByType)
            : string.Empty;

        if (s.Rating is { } r)
        {
            SoloRating = r.Solo > 0 ? r.Solo.ToString("N0", CultureInfo.CurrentCulture) : "—";
            DuosRating = r.Duos > 0 ? r.Duos.ToString("N0", CultureInfo.CurrentCulture) : "—";
        }

        SessionDeltaValue = s.Session.Delta ?? 0;
        SessionDelta = s.Session.Delta is { } d && d != 0 ? Loc.F("Main.Session.Delta", d > 0 ? $"+{d}" : $"−{-d}") : string.Empty;
        SessionSummary = s.Session.Games == 0 && s.Session.Placements.Count == 0
            ? Loc.T("Main.Session.None")
            : s.Session.Placements.Count > 0
                ? Loc.P("Main.Session.BgGames", s.Session.Placements.Count, s.Session.Placements.Average())
                : Loc.F("Main.Session.Record", s.Session.Wins, s.Session.Losses);

        if (s.Battlegrounds is { } bg)
        {
            LiveTitle = bg.IsDuos ? Loc.T("Main.Live.BgDuos") : Loc.T("Main.Live.Bg");
            LiveDetail = bg.IsHeroSelection
                ? Loc.T("Main.Live.ChoosingHero")
                : Loc.F("Main.Live.BgDetail", _cards.NameOf(bg.HeroCardId), bg.Turn, bg.TechLevel, bg.Health + bg.Armor, bg.Place);
        }
        else if (s.Constructed is { } c)
        {
            LiveTitle = Loc.F("Main.Live.Mode", Mode(s.GameType));
            LiveDetail = Loc.F("Main.Live.CtDetail", c.DeckName is { Length: > 0 } deck ? Hearthpulse.Core.Stats.DeckName.Display(deck) : OverlayViewModel.ClassName(c.PlayerClass), OverlayViewModel.ClassName(c.OpponentClass), c.Turn)
                // The overlay's deck panel waits for a known deck: say why it is missing instead of leaving the player to guess.
                + (c.DeckKnown || !Settings.ShowDeckTracker ? string.Empty : " · " + Loc.T("Main.Live.DeckUnknown"));
        }
        else
        {
            LiveTitle = s.HearthstoneRunning ? Loc.T("Main.Live.HsRunning") : Loc.T("Main.Live.NotInGame");
            LiveDetail = s.HearthstoneRunning ? Loc.F("Main.Live.WaitingMatch", Scene(s.Scene)) : Loc.T("Main.Live.StartHs");
        }
    }

    /// <summary>
    /// Switches History to every game (to show one from a deck page). The task ends once the rows are there: at once when
    /// the last refresh's games are in memory, else when the reload started for them is in.
    /// </summary>
    public Task ShowAllGamesAsync()
    {
        HistoryFilter = "All";
        if (_recentGames is null)
        {
            return RefreshAsync();
        }

        ApplyHistoryFilter();
        return Task.CompletedTask;
    }

    /// <summary>History rows for the current filter, cut from the games the last refresh read (no new queries).</summary>
    private void ApplyHistoryFilter()
    {
        if (_recentGames is not { } recent)
        {
            RefreshAsync().Forget("History refresh");
            return;
        }

        var filter = HistoryFilter;
        var games = (filter == "All" ? recent : recent.Where(g => FilterMatch(g, filter))).Take(300).ToList();
        SetHistoryGames(games);
        ShowLibrary(games);
    }

    public async Task RefreshAsync()
    {
        var cards = _cards;
        var mode = BgMode;
        var request = ++_refreshRequest;
        var data = await Task.Run(() =>
        {
            var recent = _history.Recent(400);
            var bgGames = recent.Where(g => g.Mode == mode && g.Placement > 0).ToList();
            return (
                Recent: recent,
                Total: CountHistory(recent),
                Rating: _history.RatingHistory(mode, 200),
                Distribution: _history.PlacementDistribution(mode),
                Heroes: _history.HeroStats(cards.BaseHeroId, mode),
                // The heroes tab shows the solo list when the service has no Duos one: its "You" column needs solo games.
                SoloHeroes: mode == "Battlegrounds" ? null : _history.HeroStats(cards.BaseHeroId),
                Session: bgGames);
        }).ConfigureAwait(true);

        // A newer refresh (another mode, a new game) started while this one read the history: its result wins.
        if (request != _refreshRequest)
        {
            return;
        }

        _recentGames = data.Recent;
        _migration?.InputsChanged();
        HistoryCount = data.Total;
        ShowGettingStarted = data.Total == 0;
        OfferSetup(data.Total == 0);
        Recent = data.Recent.Take(8).Select(Row).ToList();
        ApplyHistoryFilter();
        RatingPoints = data.Rating;
        RestoreRatingFromHistory(mode, data.Rating);

        HasBgGames = data.Distribution.Sum() > 0 || data.Rating.Count >= 2;

        MyHeroes = data.Heroes.Select(h => new HeroRow(
                h.HeroCardId,
                cards.NameOf(h.HeroCardId),
                h.Games,
                h.AveragePlace.ToString("0.00", CultureInfo.CurrentCulture),
                h.AveragePlace,
                h.Top4Rate.ToString("P0", CultureInfo.CurrentCulture),
                h.WinRate.ToString("P0", CultureInfo.CurrentCulture),
                h.NetRating is { } n ? (n >= 0 ? $"+{n}" : $"−{-n}") : "—",
                h.NetRating ?? 0,
                BattlegroundsAdvisor.TierOf(h.AveragePlace)))
            .ToList();
        SetBgPersonal(mode, data.Heroes, data.SoloHeroes);

        var today = data.Recent.Where(g => g.StartedAt.Date == DateTime.Today && !g.IsAbandoned).ToList();
        var todayBg = today.Where(g => g.IsBattlegrounds && g.Placement > 0).ToList();
        // The Duos rating only for a player who has one: a "—" tile is a number nobody reads.
        OverviewTiles =
        [
            new StatTile(Loc.T("Main.Tile.SoloRating"), SoloRating, SessionDelta, SessionDeltaValue),
            .. (DuosRating == "—" ? (StatTile[])[] : [new StatTile(Loc.T("Main.Tile.DuosRating"), DuosRating, string.Empty)]),
            new StatTile(Loc.T("Main.Tile.Today"), today.Count.ToString(CultureInfo.CurrentCulture),
                todayBg.Count > 0 ? Loc.F("Main.Tile.BgAvg", todayBg.Average(g => g.Placement)) : today.Count > 0 ? $"{today.Count(g => g.Result == "Win")}–{today.Count(g => g.Result == "Loss")}" : string.Empty),
            new StatTile(Loc.T("Main.Tile.AllTime"), HistoryCount.ToString("N0", CultureInfo.CurrentCulture), Loc.T("Main.Tile.GamesTracked")),
        ];

        await RefreshMyStatsAsync();
    }

    private GameRow Row(GameRecord g)
    {
        var bg = g.IsBattlegrounds;
        var top = g.Mode == "Duos" ? 2 : 4;
        // An abandoned game has no placement or result: it is shown as such, in the muted colour.
        var resultKey = g.IsAbandoned ? g.Result : bg ? (g.Placement is > 0 && g.Placement <= top ? "Win" : "Loss") : g.Result;
        var detail = bg
            ? Loc.P("Main.Game.BgDetail", g.Turns, g.TavernTier)
            : string.Join(" · ", new[]
            {
                g.DeckName is { Length: > 0 } deck ? Hearthpulse.Core.Stats.DeckName.Display(deck) : null,
                string.IsNullOrEmpty(g.OpponentClass) ? null : Loc.F("Main.Game.Vs", OverlayViewModel.ClassName(g.OpponentClass)),
                RankInfo.Localize(g.Rank),
                Loc.P("Main.Game.Turns", g.Turns),
            }.Where(x => !string.IsNullOrEmpty(x)));
        return new GameRow(
            When(g.StartedAt),
            ModeOf(g),
            g.HeroCardId,
            _cards.NameOf(g.HeroCardId),
            bg && !g.IsAbandoned ? Ordinal(g.Placement) : ResultText(g.Result),
            resultKey,
            detail,
            g.RatingDelta is { } d ? (d >= 0 ? $"+{d}" : $"−{-d}") : string.Empty,
            g.RatingDelta ?? 0,
            g.Key);
    }

    /// <summary>
    /// The length of an average-place bar among others (0..1): full for the best place, 15 % for the worst, full for all
    /// when they are within 0.01 of each other.
    /// </summary>
    internal static double PlacementWidth(double place, double best, double worst) =>
        worst - best < 0.01 ? 1 : 1 - ((place - best) / (worst - best) * 0.85);

    internal static bool FilterMatch(GameRecord g, string filter) => filter switch
    {
        "Battlegrounds" => g.Mode == "Battlegrounds",
        "Duos" => g.Mode == "Duos",
        "Ranked" => g.Mode.StartsWith("Ranked", StringComparison.Ordinal),
        "Arena" => ArenaStats.Is(g, ArenaKind.Arena),
        "Underground" => ArenaStats.Is(g, ArenaKind.Underground),
        "Other" => !g.IsBattlegrounds && !g.Mode.StartsWith("Ranked", StringComparison.Ordinal) && !StatsFilter.IsArena(g),
        _ => true,
    };

    internal static string When(DateTime at)
    {
        var local = at.Kind == DateTimeKind.Utc ? at.ToLocalTime() : at;
        if (local.Date == DateTime.Today)
        {
            return Loc.F("Main.When.Today", local.ToString("HH:mm", CultureInfo.CurrentCulture));
        }

        return local.Date == DateTime.Today.AddDays(-1)
            ? Loc.F("Main.When.Yesterday", local.ToString("HH:mm", CultureInfo.CurrentCulture))
            : local.ToString("dd MMM HH:mm", Loc.DateCulture);
    }

    /// <summary>A game type ("GT_RANKED") or a history mode ("Ranked", see <c>GameRecords.ModeOf</c>) as the player reads it.</summary>
    private static string Mode(string? mode) => mode switch
    {
        null => string.Empty,
        "GT_RANKED" or "Ranked" => Loc.T("Main.Mode.Ranked"),
        "GT_CASUAL" or "Casual" => Loc.T("Main.Mode.Casual"),
        "GT_ARENA" or "Arena" => Loc.T("Main.Mode.Arena"),
        "GT_UNDERGROUND_ARENA" => Loc.T("Main.Mode.Underground"),
        "GT_BATTLEGROUNDS" or "Battlegrounds" => Loc.T("Main.Mode.Battlegrounds"),
        "GT_BATTLEGROUNDS_DUO" or "Duos" => Loc.T("Main.Mode.Duos"),
        "GT_VS_AI" or "Practice" => Loc.T("Main.Mode.Practice"),
        "GT_VS_FRIEND" or "Friendly" => Loc.T("Main.Mode.Friendly"),
        "GT_TAVERNBRAWL" => Loc.T("Main.Mode.TavernBrawl"),
        "Brawl" => Loc.T("Main.Mode.Brawl"),
        "Mercenaries" => Loc.T("Main.Mode.Mercenaries"),
        "Other" => Loc.T("Main.Mode.Other"),
        _ when mode.StartsWith("GT_", StringComparison.Ordinal) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(mode[3..].Replace('_', ' ').ToLowerInvariant()),
        _ => mode,
    };

    /// <summary>A saved game's mode as the player reads it; Underground Arena apart from the regular one.</summary>
    private static string ModeOf(GameRecord g) => StatsFilter.IsArena(g) && ArenaStats.KindOf(g) == ArenaKind.Underground ? Loc.T("Main.Mode.Underground") : Mode(g.Mode);

    private static string Scene(string scene) => scene switch
    {
        "HUB" => Loc.T("Main.Scene.Hub"),
        "BACON" => Loc.T("Main.Scene.Bacon"),
        "TOURNAMENT" => Loc.T("Main.Scene.Tournament"),
        "COLLECTIONMANAGER" => Loc.T("Main.Scene.Collection"),
        "DRAFT" => Loc.T("Main.Mode.Arena"),
        "GAMEPLAY" => Loc.T("Main.Scene.Gameplay"),
        _ => scene.ToLowerInvariant(),
    };

    private static string Ordinal(int n) => n switch
    {
        <= 0 => "—",
        1 => Loc.T("Main.Place.1"),
        2 => Loc.T("Main.Place.2"),
        3 => Loc.T("Main.Place.3"),
        _ => Loc.F("Main.Place.N", n),
    };

    /// <summary>A history result ("Win", "Loss", "Tie", "Abandoned", "Unknown") as the player reads it; the colour keeps the stored value.</summary>
    private static string ResultText(string result) => result switch
    {
        "Win" => Loc.T("Main.Result.Win"),
        "Loss" => Loc.T("Main.Result.Loss"),
        "Tie" => Loc.T("Main.Result.Tie"),
        GameRecord.AbandonedResult => Loc.T("Main.Result.Abandoned"),
        "Unknown" => Loc.T("Main.Result.Unknown"),
        _ => result,
    };

    partial void OnRecentChanged();

    partial void RestoreRatingFromHistory(string mode, IReadOnlyList<RatingPoint> rating);
}
