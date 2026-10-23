using System.Globalization;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.App.Main;

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

public sealed record PlacementBar(int Place, int Count, double Fraction, string Share, bool Top);

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
    string FirstPlace = "");

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
public sealed record DeckRow(string Key, string Name, string Class, int Games, string Record, string WinRate, double WinFraction, string LastPlayed, string? DeckCode, string ClassKey = "", string Meta = "", string Versions = "")
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
            RefreshAsync().Forget("History refresh");
        });
        SetCardTier = new RelayCommand(p =>
        {
            CardTier = Convert.ToInt32(p, CultureInfo.InvariantCulture);
            RebuildMeta();
        });
        InstallUpdate = new RelayCommand(() => UpdateRequested?.Invoke());
        AnswerPool = new RelayCommand(p => PoolSharing = p as string == "yes");
    }

    public RelayCommand SetCardTier { get; }

    // ---- updates --------------------------------------------------------------------------

    /// <summary>The player asked to install a downloaded update right away (the app installs it and restarts).</summary>
    public event Action? UpdateRequested;

    private bool _updateReady;
    private string _updateText = string.Empty;

    /// <summary>A newer version has been downloaded: it installs by itself when Hearthpulse closes.</summary>
    public bool UpdateReady
    {
        get => _updateReady;
        private set => Set(ref _updateReady, value);
    }

    public string UpdateText
    {
        get => _updateText;
        private set => Set(ref _updateText, value);
    }

    public RelayCommand InstallUpdate { get; }

    /// <summary>Tells the dashboard that a downloaded update is waiting for the next restart.</summary>
    public void SetUpdateReady(string version)
    {
        UpdateText = $"Version {version} is downloaded — it installs when you close Hearthpulse.";
        UpdateReady = true;
        UpdateStatus = UpdateText;
    }

    private string _updateStatus = "Updates have not been checked yet.";

    /// <summary>The last update check in a sentence for Settings → About (up to date, failed and why, unverified file).</summary>
    public string UpdateStatus
    {
        get => _updateStatus;
        private set => Set(ref _updateStatus, value);
    }

    /// <summary>Shows what the last update check found.</summary>
    public void SetUpdateStatus(string status) => UpdateStatus = status;

    /// <summary>The running version, for Settings → About.</summary>
    public string VersionText => $"Hearthpulse {UpdateService.CurrentVersion}";

    /// <summary>"Good morning" / "Good afternoon" / "Good evening" for the page header.</summary>
    public string Greeting => DateTime.Now.Hour switch
    {
        < 5 => "Good night",
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    public AppSettings Settings { get; }

    // ---- stats pool -----------------------------------------------------------------------

    private bool _poolAvailable;

    /// <summary>The stats pool is set up; the first start banner and the Settings switch only show then.</summary>
    public bool PoolAvailable
    {
        get => _poolAvailable;
        set
        {
            if (Set(ref _poolAvailable, value))
            {
                Raise(nameof(AskPoolConsent));
                Raise(nameof(AskUpload));
            }
        }
    }

    /// <summary>The player has not said yet whether to share their Battlegrounds results.</summary>
    public bool AskPoolConsent => _poolAvailable
        && (Settings.PoolConsent is null || (Settings.PoolConsent == true && Settings.PoolConsentVersion < PoolClient.ConsentVersion));

    public bool PoolSharing
    {
        get => Settings.PoolConsent == true;
        set
        {
            Settings.PoolConsent = value;
            Settings.PoolConsentVersion = PoolClient.ConsentVersion;
            Raise();
            Raise(nameof(AskPoolConsent));
            Raise(nameof(AskUpload));
            PoolConsentChanged?.Invoke();
        }
    }

    /// <summary>The banner's buttons: "yes" shares results, anything else declines.</summary>
    public RelayCommand AnswerPool { get; }

    public event Action? PoolConsentChanged;

    public RelayCommand SetBracket { get; }

    public RelayCommand SetBgMode { get; }

    public RelayCommand SetHistoryFilter { get; }

    public string Version { get; } = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0");

    public string DataFolder => AppPaths.Root;

    public string Attribution => BattlegroundsMeta.Attribution + " (api.kolodahearthstone.com) · " + BattlegroundsMeta.PoolName + " when set up · Card data & art: HearthstoneJSON";

    // ---- live status ----------------------------------------------------------------------
    private bool _hearthstoneRunning;
    private bool _memoryAttached;
    private string _memoryStatus = "Waiting for Hearthstone";
    private string _liveTitle = "Not in a game";
    private string _liveDetail = "Start Hearthstone — Hearthpulse attaches automatically.";
    private bool _inGame;
    private string _soloRating = "—";
    private string _duosRating = "—";
    private string _sessionDelta = string.Empty;
    private int _sessionDeltaValue;
    private string _sessionSummary = "No games this session";
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
    private IReadOnlyList<StatTile> _overviewTiles = Array.Empty<StatTile>();
    private IReadOnlyList<StatTile> _bgTiles = Array.Empty<StatTile>();
    private IReadOnlyList<StatTile> _constructedTiles = Array.Empty<StatTile>();
    private IReadOnlyList<RatingPoint> _ratingPoints = Array.Empty<RatingPoint>();
    private IReadOnlyList<PlacementBar> _placements = Array.Empty<PlacementBar>();
    private IReadOnlyList<HeroRow> _myHeroes = Array.Empty<HeroRow>();
    private IReadOnlyList<MetaHeroRow> _heroTiers = Array.Empty<MetaHeroRow>();
    private IReadOnlyList<MetaCompRow> _compTiers = Array.Empty<MetaCompRow>();
    private IReadOnlyList<DeckRow> _decks = Array.Empty<DeckRow>();
    private IReadOnlyList<MatchupRow> _matchups = Array.Empty<MatchupRow>();
    private string _bgMode = "Battlegrounds";
    private string _historyFilter = "All";
    private int _bracket = 100;
    private string _metaUpdated = "Community data loading…";
    private int _historyCount;

    public IReadOnlyList<GameRow> Recent
    {
        get => _recent;
        private set
        {
            if (Set(ref _recent, value))
            {
                Raise(nameof(BannerHero));
            }
        }
    }

    public IReadOnlyList<GameRow> HistoryRows { get => _historyRows; private set => Set(ref _historyRows, value); }
    public IReadOnlyList<StatTile> OverviewTiles { get => _overviewTiles; private set => Set(ref _overviewTiles, value); }
    public IReadOnlyList<StatTile> BgTiles { get => _bgTiles; private set => Set(ref _bgTiles, value); }
    public IReadOnlyList<StatTile> ConstructedTiles { get => _constructedTiles; private set => Set(ref _constructedTiles, value); }
    public IReadOnlyList<RatingPoint> RatingPoints { get => _ratingPoints; private set => Set(ref _ratingPoints, value); }
    public IReadOnlyList<PlacementBar> Placements { get => _placements; private set => Set(ref _placements, value); }
    public IReadOnlyList<HeroRow> MyHeroes { get => _myHeroes; private set => Set(ref _myHeroes, value); }
    public IReadOnlyList<MetaHeroRow> HeroTiers
    {
        get => _heroTiers;
        private set
        {
            if (Set(ref _heroTiers, value))
            {
                Raise(nameof(HasHeroTiers));
            }
        }
    }

    public bool HasHeroTiers => _heroTiers.Count > 0;
    public IReadOnlyList<MetaCompRow> CompTiers { get => _compTiers; private set { if (Set(ref _compTiers, value)) Raise(nameof(HasCompTiers)); } }

    public bool HasCompTiers => _compTiers.Count > 0;

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

    private string _compTabLabel = "Comps";

    /// <summary>The stats pool groups final boards by tribe; downloaded stats have comps.</summary>
    public string CompTabLabel { get => _compTabLabel; private set { if (Set(ref _compTabLabel, value)) Raise(nameof(CompCaption)); } }

    public string CompCaption => _compTabLabel == "Tribes"
        ? "Final boards grouped by their main tribe (4 or more minions of it), ranked by how well those players finished."
        : "Board archetypes ranked by how well players who played them finished. Core cards are what the comp needs; tribes must be in your lobby.";

    private IReadOnlyList<MetaTrinketRow> _trinketTiers = Array.Empty<MetaTrinketRow>();
    private IReadOnlyList<MetaCardRow> _cardTiers = Array.Empty<MetaCardRow>();
    private int _cardTier = 1;

    public IReadOnlyList<MetaTrinketRow> TrinketTiers { get => _trinketTiers; private set { if (Set(ref _trinketTiers, value)) Raise(nameof(HasTrinketTiers)); } }

    public bool HasTrinketTiers => _trinketTiers.Count > 0;

    public IReadOnlyList<MetaCardRow> CardTiers { get => _cardTiers; private set { if (Set(ref _cardTiers, value)) Raise(nameof(HasCardTiers)); } }

    public bool HasCardTiers => _cardTiers.Count > 0;

    /// <summary>Tavern tier shown in the card list (0 = tavern spells).</summary>
    public int CardTier { get => _cardTier; private set => Set(ref _cardTier, value); }
    public IReadOnlyList<DeckRow> Decks { get => _decks; private set => Set(ref _decks, value); }
    public IReadOnlyList<MatchupRow> Matchups { get => _matchups; private set => Set(ref _matchups, value); }
    public string BgMode { get => _bgMode; private set => Set(ref _bgMode, value); }
    public string HistoryFilter { get => _historyFilter; private set => Set(ref _historyFilter, value); }
    public int Bracket { get => _bracket; private set => Set(ref _bracket, value); }
    public string MetaUpdated { get => _metaUpdated; private set => Set(ref _metaUpdated, value); }
    public int HistoryCount { get => _historyCount; private set => Set(ref _historyCount, value); }

    public void SetCards(CardDb cards)
    {
        _cards = cards;
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
        HearthstoneRunning = s.HearthstoneRunning;
        MemoryAttached = s.MemoryAttached;
        MemoryStatus = s.MemoryStatus;
        InGame = s.InGame;
        SetupWarning = s.LogConfigMissing.Count > 0
            ? $"Hearthstone logging was just enabled ({string.Join(", ", s.LogConfigMissing)}). Restart Hearthstone once to start tracking."
            : string.Empty;
        ParseErrorsText = s.ParseErrors is { } errors
            ? $"Log reading: {errors.Total:N0} Power.log line(s) could not be read ({errors.ByType}). The first of each kind is in hearthpulse.log."
            : string.Empty;

        if (s.Rating is { } r)
        {
            SoloRating = r.Solo > 0 ? r.Solo.ToString("N0", CultureInfo.CurrentCulture) : "—";
            DuosRating = r.Duos > 0 ? r.Duos.ToString("N0", CultureInfo.CurrentCulture) : "—";
        }

        SessionDeltaValue = s.Session.Delta ?? 0;
        SessionDelta = s.Session.Delta is { } d && d != 0 ? (d > 0 ? $"+{d}" : $"−{-d}") + " this session" : string.Empty;
        SessionSummary = s.Session.Games == 0 && s.Session.Placements.Count == 0
            ? "No games this session"
            : s.Session.Placements.Count > 0
                ? $"{s.Session.Placements.Count} BG games · avg {s.Session.Placements.Average():0.0}"
                : $"{s.Session.Wins}–{s.Session.Losses} this session";

        if (s.Battlegrounds is { } bg)
        {
            LiveTitle = bg.IsDuos ? "Live · Battlegrounds Duos" : "Live · Battlegrounds";
            LiveDetail = bg.IsHeroSelection
                ? "Choosing a hero"
                : $"{_cards.NameOf(bg.HeroCardId)} · turn {bg.Turn} · tier {bg.TechLevel} · {bg.Health + bg.Armor} HP · place {bg.Place}";
        }
        else if (s.Constructed is { } c)
        {
            LiveTitle = $"Live · {Mode(s.GameType)}";
            LiveDetail = $"{c.DeckName ?? OverlayViewModel.ClassName(c.PlayerClass)} vs {OverlayViewModel.ClassName(c.OpponentClass)} · turn {c.Turn}";
        }
        else
        {
            LiveTitle = s.HearthstoneRunning ? "Hearthstone is running" : "Not in a game";
            LiveDetail = s.HearthstoneRunning ? $"Waiting for a match · {Scene(s.Scene)}" : "Start Hearthstone — Hearthpulse attaches automatically.";
        }
    }

    public async Task RefreshAsync()
    {
        var cards = _cards;
        var mode = BgMode;
        var filter = HistoryFilter;
        var data = await Task.Run(() =>
        {
            var recent = _history.Recent(400);
            var bgGames = recent.Where(g => g.Mode == mode && g.Placement > 0).ToList();
            var all = _history.Recent(5000, mode);
            return (
                Recent: recent,
                Total: _history.GameCount(),
                Bg: all.Where(g => g.Placement > 0).ToList(),
                Rating: _history.RatingHistory(mode, 200),
                Distribution: _history.PlacementDistribution(mode),
                Heroes: _history.HeroStats(cards.BaseHeroId, mode),
                Decks: _history.DeckStats(),
                Classes: _history.ClassStats(),
                Filtered: filter == "All" ? recent : recent.Where(g => FilterMatch(g, filter)).ToList(),
                Session: bgGames);
        }).ConfigureAwait(true);

        HistoryCount = data.Total;
        Recent = data.Recent.Take(8).Select(Row).ToList();
        HistoryRows = data.Filtered.Take(300).Select(Row).ToList();
        RatingPoints = data.Rating;

        // Before Hearthstone reports a rating this session, show the last one from history.
        if (data.Rating.Count > 0)
        {
            var last = data.Rating[^1].Rating.ToString("N0", CultureInfo.CurrentCulture);
            if (mode == "Duos" && DuosRating == "—")
            {
                DuosRating = last;
            }
            else if (mode != "Duos" && SoloRating == "—")
            {
                SoloRating = last;
            }
        }

        var top = mode == "Duos" ? 2 : 4;
        var max = Math.Max(1, data.Distribution.Max());
        var total = Math.Max(1, data.Distribution.Sum());
        Placements = Enumerable.Range(1, mode == "Duos" ? 4 : 8)
            .Select(p => new PlacementBar(p, data.Distribution[p - 1], data.Distribution[p - 1] / (double)max,
                (data.Distribution[p - 1] / (double)total).ToString("P0", CultureInfo.CurrentCulture), p <= top))
            .ToList();

        var bg = data.Bg;
        var net = bg.Where(g => g.RatingDelta is not null).Sum(g => g.RatingDelta ?? 0);
        BgTiles =
        [
            new StatTile("Games", bg.Count.ToString("N0", CultureInfo.CurrentCulture), mode == "Duos" ? "Duos" : "Solo"),
            new StatTile("Avg place", bg.Count == 0 ? "—" : bg.Average(g => g.Placement).ToString("0.00", CultureInfo.CurrentCulture), "lower is better"),
            new StatTile($"Top {top}", bg.Count == 0 ? "—" : (bg.Count(g => g.Placement <= top) / (double)bg.Count).ToString("P0", CultureInfo.CurrentCulture), "of games"),
            new StatTile("Firsts", bg.Count(g => g.Placement == 1).ToString(CultureInfo.CurrentCulture), bg.Count == 0 ? "—" : (bg.Count(g => g.Placement == 1) / (double)bg.Count).ToString("P0", CultureInfo.CurrentCulture) + " win rate"),
            new StatTile("Net rating", net == 0 ? "±0" : net > 0 ? $"+{net:N0}" : $"−{-net:N0}", "tracked games", Math.Sign(net)),
        ];

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

        var constructed = data.Recent.Where(g => !g.IsBattlegrounds && g.Mode != "Mercenaries" && g.Result is "Win" or "Loss").ToList();
        var wins = constructed.Count(g => g.Result == "Win");
        ConstructedTiles =
        [
            new StatTile("Games", constructed.Count.ToString(CultureInfo.CurrentCulture), "recent"),
            new StatTile("Win rate", constructed.Count == 0 ? "—" : (wins / (double)constructed.Count).ToString("P0", CultureInfo.CurrentCulture), $"{wins}–{constructed.Count - wins}"),
            new StatTile("Decks", data.Decks.Count.ToString(CultureInfo.CurrentCulture), "tracked"),
            new StatTile("Best class", data.Classes.Where(c => c.Games >= 3).MaxBy(c => c.WinRate) is { } best ? OverlayViewModel.ClassName(best.PlayerClass) : "—",
                data.Classes.Where(c => c.Games >= 3).MaxBy(c => c.WinRate) is { } b2 ? b2.WinRate.ToString("P0", CultureInfo.CurrentCulture) : "3+ games needed"),
        ];

        var today = data.Recent.Where(g => g.StartedAt.Date == DateTime.Today && !g.IsAbandoned).ToList();
        var todayBg = today.Where(g => g.IsBattlegrounds && g.Placement > 0).ToList();
        OverviewTiles =
        [
            new StatTile("Solo rating", SoloRating, SessionDelta.Length > 0 ? SessionDelta : "Battlegrounds", SessionDeltaValue),
            new StatTile("Duos rating", DuosRating, "Battlegrounds Duos"),
            new StatTile("Today", today.Count.ToString(CultureInfo.CurrentCulture),
                todayBg.Count > 0 ? $"BG avg {todayBg.Average(g => g.Placement):0.0}" : today.Count > 0 ? $"{today.Count(g => g.Result == "Win")}–{today.Count(g => g.Result == "Loss")}" : "no games yet"),
            new StatTile("All time", HistoryCount.ToString("N0", CultureInfo.CurrentCulture), "games tracked"),
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

        BracketOptions = brackets.Select((b, i) => new BracketOption(b, b >= 100 ? "All" : i == 0 ? $"Top {b}%" : $"{b}%", b == Bracket, SetBracket)).ToList();

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
                h.Top4Rate > 0 ? h.Top4Rate.ToString("P0", CultureInfo.CurrentCulture) : string.Empty,
                h.FirstRate > 0 ? h.FirstRate.ToString("P0", CultureInfo.CurrentCulture) : string.Empty))
            .ToList();

        // Tribe rows stand in for comps only when the meta has none (the stats pool alone).
        var tribeRows = meta.Comps.Count == 0 && meta.Tribes.Count > 0;
        if (tribeRows)
        {
            // Final boards grouped by main tribe, from the stats pool.
            var tribes = meta.Tribes.OrderBy(t => t.AveragePlacement).ToList();
            var cBest = tribes.Count > 0 ? tribes[0].AveragePlacement : 3;
            var cWorst = tribes.Count > 0 ? tribes[^1].AveragePlacement : 5;
            CompTiers = tribes.Select((t, i) => new MetaCompRow(
                    i + 1,
                    t.Tribe == "MIXED" ? "Mixed" : Races.Display(t.Tribe, _cards.Locale),
                    string.Format(CultureInfo.CurrentCulture, "{0:N0} games", t.Games),
                    BattlegroundsAdvisor.TierOf(t.AveragePlacement),
                    string.Empty,
                    t.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                    cWorst - cBest < 0.01 ? 1 : 1 - ((t.AveragePlacement - cBest) / (cWorst - cBest) * 0.85),
                    string.Format(CultureInfo.CurrentCulture, "Top 4 {0:P0}, 1st place {1:P0}", t.Top4Rate, t.FirstRate),
                    string.Empty))
                .ToList();
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

        CompTabLabel = tribeRows ? "Tribes" : "Comps";

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

        CompEmptyText = meta.PoolPending("comp stats");
        TrinketEmptyText = meta.PoolPending("trinket stats");

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
                c.Card.Type == "MINION" ? string.Join(" · ", c.Card.Races.Select(r => Races.Display(r, _cards.Locale))) : "Tavern spell",
                c.Card.TechLevel > 0 ? c.Card.TechLevel.ToString(CultureInfo.CurrentCulture) : "–",
                c.Stats.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                c.Stats.DataPoints.ToString("N0", CultureInfo.CurrentCulture),
                kWorst - kBest < 0.01 ? 1 : 1 - ((c.Stats.AveragePlacement - kBest) / (kWorst - kBest) * 0.85)))
            .ToList();

        // The API publishes tavern spells nowhere and has fewer minion brackets than hero brackets: both are said, not hidden.
        CardEmptyText = CardTier == 0 ? meta.PoolPending("tavern spell stats") : "No card data for this tier yet.";
        var bracketText = BattlegroundsMeta.BracketLabel(Bracket)
            + (cardBracket != Bracket && meta.CardsByBracket.Count > 0 ? $" (cards: {BattlegroundsMeta.BracketLabel(cardBracket)})" : string.Empty);
        var sharedText = meta.SharedGames > 0 ? $" · {meta.SharedGames:N0} shared games" : string.Empty;
        MetaUpdated = $"{meta.Source} · {bracketText} · updated {meta.Updated.ToLocalTime():dd MMM HH:mm}{sharedText}";
    }

    private GameRow Row(GameRecord g)
    {
        var bg = g.IsBattlegrounds;
        var top = g.Mode == "Duos" ? 2 : 4;
        // An abandoned game has no placement or result: it is shown as such, in the muted colour.
        var resultKey = g.IsAbandoned ? g.Result : bg ? (g.Placement is > 0 && g.Placement <= top ? "Win" : "Loss") : g.Result;
        var detail = bg
            ? $"Tier {g.TavernTier} · {g.Turns} turns"
            : string.Join(" · ", new[]
            {
                g.DeckName,
                string.IsNullOrEmpty(g.OpponentClass) ? null : "vs " + OverlayViewModel.ClassName(g.OpponentClass),
                RankInfo.Localize(g.Rank),
                $"{g.Turns} turns",
            }.Where(x => !string.IsNullOrEmpty(x)));
        return new GameRow(
            When(g.StartedAt),
            Mode(g.Mode),
            g.HeroCardId,
            _cards.NameOf(g.HeroCardId),
            bg && !g.IsAbandoned ? Ordinal(g.Placement) : g.Result,
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
            return "Today " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
        }

        return local.Date == DateTime.Today.AddDays(-1)
            ? "Yesterday " + local.ToString("HH:mm", CultureInfo.CurrentCulture)
            : local.ToString("dd MMM HH:mm", CultureInfo.CurrentCulture);
    }

    private static string Mode(string? mode) => mode switch
    {
        null => string.Empty,
        "GT_RANKED" => "Ranked",
        "GT_CASUAL" => "Casual",
        "GT_ARENA" => "Arena",
        "GT_BATTLEGROUNDS" => "Battlegrounds",
        "GT_BATTLEGROUNDS_DUO" => "Duos",
        "GT_VS_AI" => "Practice",
        "GT_VS_FRIEND" => "Friendly",
        "GT_TAVERNBRAWL" => "Tavern Brawl",
        _ when mode.StartsWith("GT_", StringComparison.Ordinal) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(mode[3..].Replace('_', ' ').ToLowerInvariant()),
        _ => mode,
    };

    private static string Scene(string scene) => scene switch
    {
        "HUB" => "main menu",
        "BACON" => "Battlegrounds lobby",
        "TOURNAMENT" => "play menu",
        "COLLECTIONMANAGER" => "collection",
        "DRAFT" => "Arena",
        "GAMEPLAY" => "loading game",
        _ => scene.ToLowerInvariant(),
    };

    private static string Ordinal(int n) => n switch
    {
        <= 0 => "—",
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => $"{n}th",
    };
}
