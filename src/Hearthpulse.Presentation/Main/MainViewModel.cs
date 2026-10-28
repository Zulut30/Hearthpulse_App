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
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Updates;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed record GameRow(
    string When,
    string Mode,
    string HeroCardId,
    string Hero,
    string Result,
    string ResultKey,
    string Detail,
    string Rating,
    int RatingValue,
    string Key = "");

public sealed partial record PlacementBar(int Place, int Count, double Fraction, string Share, bool Top);

public sealed record HeroRow(string CardId, string Name, int Games, string AveragePlace, double AverageValue, string Top4, string Wins, string Net, int NetValue, string Tier);

public sealed record MetaHeroRow(
    int Rank,
    string CardId,
    string Name,
    string Tier,
    string AveragePlace,
    double AverageValue,
    string PickRate,
    string DataPoints,
    string Personal,
    double BarFraction,
    string Top4 = "",
    string FirstPlace = "",
    double Top4Value = 0,
    double FirstValue = 0,
    double PickValue = 0,
    int Games = 0);

public sealed record MetaCompRow(
    int Rank,
    string Name,
    string Tribes,
    string Power,
    string Difficulty,
    string Placement,
    double BarFraction,
    string Commit,
    string KeyCards);

/// <summary>A deck in "Your decks": a family of versions or a single one (<see cref="Key"/> is the family key).</summary>
public sealed record DeckRow(
    string Key,
    string Name,
    string Class,
    int Games,
    string Record,
    string WinRate,
    double WinFraction,
    string LastPlayed,
    string? DeckCode,
#if HEARTHPULSE_WPF
    string ClassKey = "",
#endif
    string Meta = "",
    string Versions = "")
{
    /// <summary>Too few games to tell: the win bar is faded.</summary>
    public bool Few => Games < Hearthpulse.Core.Stats.DeckDetails.SmallSample;
}

public sealed record MatchupRow(string Class, int Games, string Record, string WinRate, double WinFraction);

public sealed record StatTile(string Label, string Value, string Detail, int Sign = 0);

/// <summary>One trinket in the community tier list.</summary>
public sealed record MetaTrinketRow(int Rank, string CardId, string Name, string Tier, string Placement, string PickRate, string DataPoints, double BarFraction);

/// <summary>One minion or tavern spell in the community card list (compare cards of the same tier).</summary>
public sealed record MetaCardRow(int Rank, string CardId, string Name, string Kind, string Tier, string Placement, string Games, double BarFraction);

/// <summary>One MMR bracket the community stats have ("All", "Top 50%", "20%"...), for the tier lists' picker.</summary>
public sealed record BracketOption(int Value, string Label, bool IsSelected, RelayCommand Select);

public sealed partial class MainViewModel : Observable
{
    private readonly HistoryStore _history;
    private CardDb _cards = CardDb.Empty;
    private BattlegroundsMeta? _meta;

    public MainViewModel(HistoryStore history, AppSettings settings)
    {
        _history = history;
        Settings = settings;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
        SetBracket = new RelayCommand(p =>
        {
            Bracket = Convert.ToInt32(p, CultureInfo.InvariantCulture);
            RebuildMeta();
        });
        SetBgMode = new RelayCommand(p =>
        {
            BgMode = p as string ?? "Battlegrounds";
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
            RebuildMeta();
        });
        InstallUpdate = new RelayCommand(() => UpdateRequested?.Invoke());
#if HEARTHPULSE_WPF
        AnswerPool = new RelayCommand(p => PoolSharing = p as string == "yes");
#endif
    }

    public RelayCommand SetCardTier { get; }

    // ---- updates --------------------------------------------------------------------------

    /// <summary>The player asked to install a downloaded update right away (the app installs it and restarts).</summary>
    public event Action? UpdateRequested;

    private bool _updateReady;
    private string? _updateVersion;
    private string? _updateStatus;

    /// <summary>A newer version has been downloaded: it installs by itself when Hearthpulse closes.</summary>
    public bool UpdateReady
    {
        get => _updateReady;
        private set => Set(ref _updateReady, value);
    }

    /// <summary>The downloaded update in a sentence; built on read so it follows a language change.</summary>
    public string UpdateText => _updateVersion is null ? string.Empty : Loc.F("Main.Update.Downloaded", _updateVersion);

    public RelayCommand InstallUpdate { get; }

    /// <summary>Tells the dashboard that a downloaded update is waiting for the next restart.</summary>
    public void SetUpdateReady(string version)
    {
        _updateVersion = version;
        _updateStatus = null;
        Raise(nameof(UpdateText));
        UpdateReady = true;
        Raise(nameof(UpdateStatus));
    }

    /// <summary>The last update check in a sentence for Settings → About (up to date, failed and why, unverified file).</summary>
    public string UpdateStatus => _updateStatus ?? (_updateVersion is null ? Loc.T("Main.Update.NotChecked") : UpdateText);

    /// <summary>Shows what the last update check found.</summary>
    public void SetUpdateStatus(string status)
    {
        _updateStatus = status;
        Raise(nameof(UpdateStatus));
    }

    /// <summary>The running version, for Settings → About.</summary>
    public string VersionText => $"Hearthpulse {UpdateService.CurrentVersion}";

    public AppSettings Settings { get; }

    public RelayCommand SetBracket { get; }

    public RelayCommand SetBgMode { get; }

    public RelayCommand SetHistoryFilter { get; }

    public string Version { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    public string DataFolder => AppPaths.Root;

    public string Attribution => Loc.T("Settings.Sources.Text");

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
    private IReadOnlyList<GameRow> _historyRows = Array.Empty<GameRow>();

    // Refreshes run off the UI thread; only the newest one may apply its result.
    private int _refreshRequest;

    // The games the last refresh read; the history filter is applied to them.
    private IReadOnlyList<GameRecord>? _recentGames;
    private IReadOnlyList<StatTile> _overviewTiles = Array.Empty<StatTile>();
    private IReadOnlyList<RatingPoint> _ratingPoints = Array.Empty<RatingPoint>();
    private IReadOnlyList<PlacementBar> _placements = Array.Empty<PlacementBar>();
    private bool _hasBgGames;
    private IReadOnlyList<HeroRow> _myHeroes = Array.Empty<HeroRow>();
    private IReadOnlyList<MetaHeroRow> _heroTiers = Array.Empty<MetaHeroRow>();
    private IReadOnlyList<MetaCompRow> _compTiers = Array.Empty<MetaCompRow>();
    private IReadOnlyList<DeckRow> _decks = Array.Empty<DeckRow>();
    private IReadOnlyList<MatchupRow> _matchups = Array.Empty<MatchupRow>();
    private string _bgMode = "Battlegrounds";
    private string _historyFilter = "All";
    private int _bracket = 100;
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
            }
        }
    }

    public IReadOnlyList<GameRow> HistoryRows { get => _historyRows; private set => Set(ref _historyRows, value); }
    public IReadOnlyList<StatTile> OverviewTiles { get => _overviewTiles; private set => Set(ref _overviewTiles, value); }
    public IReadOnlyList<RatingPoint> RatingPoints { get => _ratingPoints; private set => Set(ref _ratingPoints, value); }
    public IReadOnlyList<PlacementBar> Placements { get => _placements; private set => Set(ref _placements, value); }

    /// <summary>Home shows the Battlegrounds rating and placement cards only once the mode has games to draw.</summary>
    public bool HasBgGames { get => _hasBgGames; private set => Set(ref _hasBgGames, value); }

    public IReadOnlyList<HeroRow> MyHeroes { get => _myHeroes; private set => Set(ref _myHeroes, value); }
    public IReadOnlyList<MetaHeroRow> HeroTiers
    {
        get => _heroTiers;
        private set
        {
            if (Set(ref _heroTiers, value))
            {
                RebuildHeroGroups();
            }
        }
    }

    public IReadOnlyList<MetaCompRow> CompTiers { get => _compTiers; private set { if (Set(ref _compTiers, value)) OnCompTiersChanged(); } }


    private IReadOnlyList<BracketOption> _bracketOptions = Array.Empty<BracketOption>();
    private string _compEmptyText = string.Empty;
    private string _trinketEmptyText = string.Empty;
    private string _cardEmptyText = string.Empty;

    /// <summary>The MMR brackets the loaded stats have; the picker shows only when there is a choice.</summary>
    public IReadOnlyList<BracketOption> BracketOptions { get => _bracketOptions; private set { if (Set(ref _bracketOptions, value)) Raise(nameof(HasBracketChoice)); } }

    public bool HasBracketChoice => _bracketOptions.Count > 1;

    /// <summary>What the empty comp, trinket and card lists say: which source those numbers will come from.</summary>
    public string CompEmptyText { get => _compEmptyText; private set => Set(ref _compEmptyText, value); }

    public string TrinketEmptyText { get => _trinketEmptyText; private set => Set(ref _trinketEmptyText, value); }

    public string CardEmptyText { get => _cardEmptyText; private set => Set(ref _cardEmptyText, value); }

    private IReadOnlyList<MetaTrinketRow> _trinketTiers = Array.Empty<MetaTrinketRow>();
    private IReadOnlyList<MetaCardRow> _cardTiers = Array.Empty<MetaCardRow>();
    private int _cardTier = 1;

    public IReadOnlyList<MetaTrinketRow> TrinketTiers { get => _trinketTiers; private set { if (Set(ref _trinketTiers, value)) RebuildTrinketGroups(); } }


    public IReadOnlyList<MetaCardRow> CardTiers { get => _cardTiers; private set => Set(ref _cardTiers, value); }


    /// <summary>Tavern tier shown in the card list (0 = tavern spells).</summary>
    public int CardTier { get => _cardTier; private set => Set(ref _cardTier, value); }
    public IReadOnlyList<DeckRow> Decks { get => _decks; private set => Set(ref _decks, value); }
    public IReadOnlyList<MatchupRow> Matchups { get => _matchups; private set => Set(ref _matchups, value); }
    public string BgMode { get => _bgMode; private set => Set(ref _bgMode, value); }
    public string HistoryFilter { get => _historyFilter; private set => Set(ref _historyFilter, value); }
    public int Bracket { get => _bracket; private set => Set(ref _bracket, value); }
    public string MetaUpdated { get => _metaUpdated; private set => Set(ref _metaUpdated, value); }
    public int HistoryCount { get => _historyCount; private set { if (Set(ref _historyCount, value)) Raise(nameof(HistorySubtitle)); } }

    public void SetCards(CardDb cards)
    {
        _cards = cards;
        Raise(nameof(LanguageRestartHint));
        RefreshAsync().Forget("History refresh");
        RebuildMeta();
    }

    public void SetMeta(BattlegroundsMeta? meta)
    {
        _meta = meta;
        RebuildMeta();
        RebuildBattlegroundsStats();
    }

    public void Apply(LiveSnapshot s)
    {
        _lastSnapshot = s;
        ApplyLadder(s);
        HearthstoneRunning = s.HearthstoneRunning;
        MemoryAttached = s.MemoryAttached;
        MemoryStatus = s.MemoryStatus;
        InGame = s.InGame;
        SetupWarning = s.LogConfigMissing.Count > 0
            ? Loc.F("Main.Setup.LoggingEnabled", string.Join(", ", s.LogConfigMissing))
            : string.Empty;
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
            LiveDetail = Loc.F("Main.Live.CtDetail", c.DeckName is { Length: > 0 } deck ? Hearthpulse.Core.Stats.DeckName.Display(deck) : OverlayViewModel.ClassName(c.PlayerClass), OverlayViewModel.ClassName(c.OpponentClass), c.Turn);
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
        HistoryRows = (filter == "All" ? recent : recent.Where(g => FilterMatch(g, filter))).Take(300).Select(Row).ToList();
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
                Session: bgGames);
        }).ConfigureAwait(true);

        // A newer refresh (another mode, a new game) started while this one read the history: its result wins.
        if (request != _refreshRequest)
        {
            return;
        }

        _recentGames = data.Recent;
        HistoryCount = data.Total;
        Recent = data.Recent.Take(8).Select(Row).ToList();
        ApplyHistoryFilter();
        RatingPoints = data.Rating;
        RestoreRatingFromHistory(mode, data.Rating);

        var top = mode == "Duos" ? 2 : 4;
        var max = Math.Max(1, data.Distribution.Max());
        var total = Math.Max(1, data.Distribution.Sum());
        Placements = Enumerable.Range(1, mode == "Duos" ? 4 : 8)
            .Select(p => new PlacementBar(p, data.Distribution[p - 1], data.Distribution[p - 1] / (double)max,
                (data.Distribution[p - 1] / (double)total).ToString("P0", CultureInfo.CurrentCulture), p <= top))
            .ToList();
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

        var today = data.Recent.Where(g => g.StartedAt.Date == DateTime.Today && !g.IsAbandoned).ToList();
        var todayBg = today.Where(g => g.IsBattlegrounds && g.Placement > 0).ToList();
        OverviewTiles =
        [
            new StatTile(Loc.T("Main.Tile.SoloRating"), SoloRating, SessionDelta, SessionDeltaValue),
            new StatTile(Loc.T("Main.Tile.DuosRating"), DuosRating, string.Empty),
            new StatTile(Loc.T("Main.Tile.Today"), today.Count.ToString(CultureInfo.CurrentCulture),
                todayBg.Count > 0 ? Loc.F("Main.Tile.BgAvg", todayBg.Average(g => g.Placement)) : today.Count > 0 ? $"{today.Count(g => g.Result == "Win")}–{today.Count(g => g.Result == "Loss")}" : string.Empty),
            new StatTile(Loc.T("Main.Tile.AllTime"), HistoryCount.ToString("N0", CultureInfo.CurrentCulture), Loc.T("Main.Tile.GamesTracked")),
        ];

        await RefreshMyStatsAsync();
    }

    private void RebuildMeta()
    {
        if (_meta is not { } meta || _cards.Count == 0)
        {
            return;
        }

        // Only the brackets the source has are offered (the API: top 50/20/5/1%, no all-ranks hero slice).
        var brackets = meta.HeroBrackets;
        if (!brackets.Contains(Bracket))
        {
            Bracket = meta.WidestBracket;
        }

        BracketOptions = brackets.Select((b, i) => new BracketOption(b, b >= 100 ? Loc.T("Main.Bracket.All") : i == 0 ? Loc.F("Main.Bracket.Top", b / 100.0) : (b / 100.0).ToString("P0", CultureInfo.CurrentCulture), b == Bracket, SetBracket)).ToList();

        var personal = MyHeroes.ToDictionary(h => h.CardId);
        var heroes = meta.HeroesByBracket.GetValueOrDefault(Bracket) ?? new Dictionary<string, MetaHero>();

        var ranked = heroes.Values.Where(h => h.DataPoints == 0 || h.DataPoints >= meta.MinHeroGames).OrderBy(h => h.AveragePosition).ToList();
        var best = ranked.Count > 0 ? ranked[0].AveragePosition : 3.5;
        var worst = ranked.Count > 0 ? ranked[^1].AveragePosition : 5.5;
        HeroTiers = ranked.Select((h, i) => new MetaHeroRow(
                i + 1,
                h.HeroCardId,
                _cards.NameOf(h.HeroCardId),
                BattlegroundsAdvisor.TierOf(h.AveragePosition),
                h.AveragePosition.ToString("0.00", CultureInfo.CurrentCulture),
                h.AveragePosition,
                h.PickRate.ToString("P0", CultureInfo.CurrentCulture),
                h.DataPoints > 0 ? h.DataPoints.ToString("N0", CultureInfo.CurrentCulture) : "—",
                personal.TryGetValue(h.HeroCardId, out var p) ? $"{p.Games} · {p.AveragePlace}" : string.Empty,
                worst - best < 0.01 ? 1 : 1 - ((h.AveragePosition - best) / (worst - best) * 0.85),
                h.Top4Rate > 0 ? h.Top4Rate.ToString("P0", CultureInfo.CurrentCulture) : "—",
                h.FirstRate > 0 ? h.FirstRate.ToString("P0", CultureInfo.CurrentCulture) : "—",
                h.Top4Rate,
                h.FirstRate,
                h.PickRate,
                h.DataPoints))
            .ToList();

        var tribeRows = UseTribeRows(meta);
        if (tribeRows)
        {
            RebuildTribeRows(meta);
        }
        else
        {
            // Downloaded stats have real comps instead of tribes.
            var comps = meta.Comps
                .Select(c => (Comp: c, Placement: c.PlacementAtMmr.TryGetValue(Bracket, out var p) ? p : c.AveragePlacement))
                .OrderBy(c => c.Placement)
                .ToList();
            var cBest = comps.Count > 0 ? comps[0].Placement : 3;
            var cWorst = comps.Count > 0 ? comps[^1].Placement : 5;
            CompTiers = comps.Select((c, i) => new MetaCompRow(
                    i + 1,
                    c.Comp.Name,
                    string.Join(" · ", c.Comp.Tribes.Select(t => Races.Display(t, _cards.Locale))),
                    string.IsNullOrEmpty(c.Comp.PowerLevel) ? "–" : c.Comp.PowerLevel,
                    c.Comp.Difficulty,
                    c.Placement.ToString("0.00", CultureInfo.CurrentCulture),
                    cWorst - cBest < 0.01 ? 1 : 1 - ((c.Placement - cBest) / (cWorst - cBest) * 0.85),
                    c.Comp.WhenToCommit ?? string.Empty,
                    string.Join(", ", c.Comp.Cards.Where(x => x.Status == "CORE").Select(x => _cards.NameOf(x.CardId)))))
                .ToList();
        }

        OnCompRowsRebuilt(tribeRows);

        var trinkets = meta.Trinkets.Values
            .Select(t => (Trinket: t, Stats: meta.Trinket(t.CardId, Bracket)))
            .Where(t => t.Stats is not null)
            .OrderBy(t => t.Stats!.Value.Placement)
            .ToList();
        var tBest = trinkets.Count > 0 ? trinkets[0].Stats!.Value.Placement : 3;
        var tWorst = trinkets.Count > 0 ? trinkets[^1].Stats!.Value.Placement : 5;
        TrinketTiers = trinkets.Select((t, i) => new MetaTrinketRow(
                i + 1,
                t.Trinket.CardId,
                _cards.NameOf(t.Trinket.CardId) is { Length: > 0 } n ? n : t.Trinket.CardId,
                BattlegroundsAdvisor.TierOf(t.Stats!.Value.Placement),
                t.Stats.Value.Placement.ToString("0.00", CultureInfo.CurrentCulture),
                t.Stats.Value.PickRate.ToString("P0", CultureInfo.CurrentCulture),
                t.Stats.Value.DataPoints.ToString("N0", CultureInfo.CurrentCulture),
                tWorst - tBest < 0.01 ? 1 : 1 - ((t.Stats.Value.Placement - tBest) / (tWorst - tBest) * 0.85)))
            .ToList();

        CompEmptyText = meta.PoolPending(Loc.T("Main.Bg.PoolWhat.Comps"));
        TrinketEmptyText = meta.PoolPending(Loc.T("Main.Bg.PoolWhat.Trinkets"));

        // Cards are only comparable within one tavern tier: late, high-tier cards always look better overall.
        var cardBracket = meta.CardsByBracket.ContainsKey(Bracket) ? Bracket : 100;
        var cardStats = meta.CardsByBracket.GetValueOrDefault(cardBracket);
        var cards = (cardStats?.Values ?? Enumerable.Empty<MetaCard>())
            .Where(c => c.DataPoints >= BattlegroundsMeta.MinCardGames)
            .Select(c => (Stats: c, Card: _cards.Get(c.CardId)))
            .Where(c => c.Card is not null && (CardTier == 0
                ? c.Card.Type == "BATTLEGROUND_SPELL" && c.Card.IsBattlegroundsPoolSpell
                : c.Card.Type == "MINION" && c.Card.IsBattlegroundsPoolMinion && c.Card.TechLevel == CardTier))
            .OrderBy(c => c.Stats.AveragePlacement)
            .ToList();
        var kBest = cards.Count > 0 ? cards[0].Stats.AveragePlacement : 3;
        var kWorst = cards.Count > 0 ? cards[^1].Stats.AveragePlacement : 5;
        CardTiers = cards.Select((c, i) => new MetaCardRow(
                i + 1,
                c.Stats.CardId,
                c.Card!.Name,
                c.Card.Type == "MINION" ? string.Join(" · ", c.Card.Races.Select(r => Races.Display(r, _cards.Locale))) : Loc.T("Main.Bg.TavernSpell"),
                c.Card.TechLevel > 0 ? c.Card.TechLevel.ToString(CultureInfo.CurrentCulture) : "–",
                c.Stats.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                c.Stats.DataPoints.ToString("N0", CultureInfo.CurrentCulture),
                kWorst - kBest < 0.01 ? 1 : 1 - ((c.Stats.AveragePlacement - kBest) / (kWorst - kBest) * 0.85)))
            .ToList();

        // The API publishes tavern spells nowhere and has fewer minion brackets than hero brackets: both are said, not hidden.
        CardEmptyText = CardTier == 0 ? meta.PoolPending(Loc.T("Main.Bg.PoolWhat.Spells")) : Loc.T("Main.Bg.NoCardData");
        var bracketText = BattlegroundsMeta.BracketLabel(Bracket)
            + (cardBracket != Bracket && meta.CardsByBracket.Count > 0 ? Loc.F("Main.Bg.CardsBracket", BattlegroundsMeta.BracketLabel(cardBracket)) : string.Empty);
        var sharedText = meta.SharedGames > 0 ? Loc.F("Main.Bg.SharedGames", meta.SharedGames) : string.Empty;
        MetaUpdated = Loc.F("Main.Bg.MetaUpdated", meta.Source, bracketText, meta.Updated.ToLocalTime(), sharedText);
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
            Mode(g.Mode),
            g.HeroCardId,
            _cards.NameOf(g.HeroCardId),
            bg && !g.IsAbandoned ? Ordinal(g.Placement) : ResultText(g.Result),
            resultKey,
            detail,
            g.RatingDelta is { } d ? (d >= 0 ? $"+{d}" : $"−{-d}") : string.Empty,
            g.RatingDelta ?? 0,
            g.Key);
    }

    private static bool FilterMatch(GameRecord g, string filter) => filter switch
    {
        "Battlegrounds" => g.Mode == "Battlegrounds",
        "Duos" => g.Mode == "Duos",
        "Ranked" => g.Mode.StartsWith("Ranked", StringComparison.Ordinal),
        "Other" => !g.IsBattlegrounds && !g.Mode.StartsWith("Ranked", StringComparison.Ordinal),
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

    partial void OnCompTiersChanged();

    partial void RestoreRatingFromHistory(string mode, IReadOnlyList<RatingPoint> rating);

    partial void RebuildTribeRows(BattlegroundsMeta meta);

    partial void OnCompRowsRebuilt(bool tribeRows);
}
