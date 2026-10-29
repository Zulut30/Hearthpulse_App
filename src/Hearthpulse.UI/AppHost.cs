using System.Net;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Updates;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.UI;

/// <summary>
/// Everything the app does apart from its windows: settings, history, the live log pipeline, card data and community
/// stats, and the two view models. Desktop and mobile shells host one of these.
/// </summary>
public sealed partial class AppHost : IDisposable
{
    private readonly bool _rendering;
    private readonly DispatcherTimer _historyDebounce;
    private readonly DispatcherTimer _settingsDebounce;
    private readonly DispatcherTimer _metaRefresh;
    private LogWatcher? _watcher;
    private LiveSnapshot? _pending;
    private LiveSnapshot _last = LiveSnapshot.Empty;
    private int _applyScheduled;
    private readonly SnapshotPacer _pacer = new();
    private string? _deckStatsKey;
    private DeckAggregate? _deckStats;
    private MatchupAggregate? _matchupStats;
    private UpdateInfo? _stagedUpdate;
    private Func<string>? _busy;
    private Task? _archetypeNames;
    private Func<string>? _updateStatus;
    private readonly StartupPrefetch _prefetch;

    /// <param name="rendering">Disables account restore and all live machine changes for off-screen renders.</param>
    public AppHost(AppSettings settings, bool rendering = false)
    {
        _rendering = rendering;
        Settings = settings;
        Loc.Instance.SetLanguage(settings.InterfaceLanguage());
        Http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(40),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthpulse/" + Version);
        CardArt.Http = Http;
        Api = new HearthpulseApi(Http);
        // The game and the cards are looked up while the shell builds its windows, not after.
        _prefetch = StartupPrefetch.Start(settings, Http);
        var accountClient = new AccountClient(Http);
        Account = new AccountService(accountClient);

        History = new HistoryStore(AppPaths.Database) { Replays = Core.Replay.ReplayStore.Default };
        Memory = new HearthstoneMemory();
        Session = new GameSession(History, Memory);
        Session.SetMulliganGuide(new MulliganGuideSource(new HearthpulseData(Api)));
        Session.SetCardPoolWiki(new Hearthpulse.Core.CardPool.WikiPoolSource(new HearthpulseData(Api)));
        Session.SetDiscoverPicks(new Hearthpulse.Core.CardPool.DiscoverChoiceSource(new HearthpulseData(Api)));
        Session.SetBgLeaderboards(new BgLeaderboardSource(Api));
        Main = new MainViewModel(History, Settings);
        Main.AttachAccount(Account);
        Uploader = ProfileUploader.For(accountClient, Account, () => Settings.UploadGamesToProfile, History);
        Uploader.BaseHero = id => Cards.BaseHeroId(id);
        Account.Changed += Uploader.Kick;
        Main.AttachUploader(Uploader);
        if (!_rendering)
        {
            Account.RestoreAsync().Forget("Account restore");
        }
        Overlay = new OverlayViewModel();
        // No game plan panel: no comp to choose or see, so the advisor plans around none (a comp saved by an older
        // version stays in the settings, unused).
        Session.SetCompMode(CompMode.Manual, null);
        Session.SetCardPoolEnabled(Settings.ShowCardPool);
        Session.SetArenaDraftEnabled(Settings.ShowArenaDraft);
        Session.SetBgOpponentMmrEnabled(Settings.ShowBgOpponentMmr);
        Session.SetLadderBreak(TimeSpan.FromHours(Math.Clamp(Settings.LadderSessionBreakHours, 1, 12)));
        Overlay.SecretToggleRequested += Session.ToggleSecret;
        Overlay.CardPoolBrowserResized += OnSettingsChanged;
        Overlay.CounterModeRequested += (id, mode) =>
        {
            Main.ApplyCounterMode(id, mode);
            OnSettingsChanged();
        };
        Main.CountersChanged += OnSettingsChanged;
        Main.UpdateRequested += OnInstallUpdateRequested;

        _historyDebounce = Debouncer(TimeSpan.FromMilliseconds(600), () => Main.RefreshAsync().Forget("History refresh"));
        _settingsDebounce = Debouncer(TimeSpan.FromMilliseconds(400), Settings.Save);
        Main.UploadSettingChanged += () =>
        {
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        };
        _metaRefresh = new DispatcherTimer { Interval = TimeSpan.FromHours(3) };
        _metaRefresh.Tick += (_, _) => RefreshMetaAsync().Forget("Battlegrounds meta load");

        Session.SnapshotChanged += OnSnapshot;
        Session.ConstructedMetaNeeded += format => Dispatcher.UIThread.Post(() => EnsureConstructedMeta(format));
        Main.ConstructedMetaRequested += format =>
        {
            if (format is not null)
            {
                EnsureConstructedMeta(format);
                return;
            }

            // A meta filter changed: it is saved, and every format in use loads again for it.
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
            _constructedFormats.Add(ConstructedMeta.Key(ConstructedMeta.Formats, Settings.MetaFormat));
            foreach (var key in _constructedFormats.ToList())
            {
                LoadConstructedMetaAsync(key, restart: true).Forget("Constructed meta load");
            }
        };
        Session.GameFinished += outcome => Dispatcher.UIThread.Post(() => OnGameFinished(outcome));
        History.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _historyDebounce.Stop();
            _historyDebounce.Start();
            _deckStatsKey = null;
        });
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public static string Version { get; } = typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public AppSettings Settings { get; }

    public HttpClient Http { get; }

    /// <summary>The Hearthpulse API (fallback community stats).</summary>
    public HearthpulseApi Api { get; }

    public AccountService Account { get; }

    /// <summary>Sends finished games to the hearthpulse.net profile while signed in (Settings, account card).</summary>
    public ProfileUploader Uploader { get; }

    public HistoryStore History { get; }

    public HearthstoneMemory Memory { get; }

    public GameSession Session { get; }

    public MainViewModel Main { get; }

    public OverlayViewModel Overlay { get; }

    public string? InstallDirectory { get; private set; }

    /// <summary>Stats of <see cref="AppSettings.MetaFormat"/> (the format loaded at start); null until they are in.</summary>
    public ConstructedMeta? ConstructedMeta { get; private set; }

    /// <summary>Constructed stats by format key ("standard", "wild"): the page and a game each use their own format's.</summary>
    private readonly Dictionary<string, ConstructedMeta> _constructedMetas = new(StringComparer.Ordinal);

    /// <summary>Formats kept fresh: the one loaded at start, the pages opened and the formats played.</summary>
    private readonly HashSet<string> _constructedFormats = new(StringComparer.Ordinal);

    /// <summary>Formats whose download runs now, with the number of the latest request (only that one passes its stats on).</summary>
    private readonly Dictionary<string, int> _constructedLoading = new(StringComparer.Ordinal);

    private int _constructedRequest;

    public CardDb Cards { get; private set; } = CardDb.Empty;

    public BattlegroundsMeta? Meta { get; private set; }

    /// <summary>Something the player should know (title, text), e.g. "restart Hearthstone"; shells show it as a notification.</summary>
    public event Action<string, string>? Notification;

    /// <summary>The shell should close the app, e.g. because an update was just handed to its helper.</summary>
    public event Action? ExitRequested;

    /// <summary>Applies the "start when you sign in" setting; set by the shell for its platform.</summary>
    public Action<bool>? ApplyAutostart { get; set; }

    /// <summary>Finds Hearthstone, loads card data, imports history and (unless rendering off-screen) starts following the logs.</summary>
    public async Task InitializeAsync(bool watchLogs = true)
    {
        var live = watchLogs && !_rendering;
        try
        {
            SetBusy(() => Loc.T("Host.Busy.LookingForGame"));
            Task.Run(CacheCleanup.Run).Forget("Cache clean-up");
            (InstallDirectory, var clientLocale) = await _prefetch.Game;
            if (InstallDirectory is not null && Settings.HearthstonePath != InstallDirectory)
            {
                Settings.HearthstonePath = InstallDirectory;
                Settings.Save();
            }

            Session.InstallDirectory = InstallDirectory;
            // "auto" language and cards follow the installed client (a Russian client shows Russian cards and interface).
            Settings.ClientLocale = clientLocale;
            Loc.Instance.SetLanguage(Settings.InterfaceLanguage());
            var locale = Settings.CardLocale();
            CardArt.Locale = locale;

            // Hearthstone's config is outside HEARTHPULSE_DATA, even when our own data is a scratch copy.
            var logConfigChanged = live && await Task.Run(() =>
            {
                try
                {
                    return LogConfig.EnsureAll(InstallDirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diag.Error("log.config not writable", ex);
                    return false;
                }
            });

            SetBusy(() => Loc.T("Host.Busy.LoadingCards"));
            Cards = await _prefetch.CardsAsync(locale);
            CardArt.FallbackUrl = id => Cards.BgCards.ImageUrl(id);
            if (Cards.Count == 0 && locale != "enUS")
            {
                Cards = await CardDb.LoadAsync("enUS", Http);
            }

            Session.Cards = Cards;
            Main.SetCards(Cards);
            LoadCardPoolAsync().Forget("Card pool load");

            if (!Settings.HistoryImported || !Settings.HistoryStatsFilled || !Settings.HistoryCardsFilled)
            {
                await ImportLogsAsync(force: false);
            }
            else if (InstallDirectory is { } install)
            {
                // Games saved before Decks.log was read correctly (and imported ones) have no deck yet.
                Task.Run(() => LogBackfill.FillDecks(install, History, Cards)).Forget("Deck backfill");
            }

            if (!live)
            {
                return;
            }

            _watcher = new LogWatcher(Session, () => InstallDirectory);
            _watcher.Start();
            Uploader.Start();
            SetBusy(InstallDirectory is null
                ? () => Loc.T("Host.Busy.GameNotFound")
                : null);

            if (logConfigChanged && HearthstoneInstall.IsRunning())
            {
                Notification?.Invoke(Loc.T("Host.Notify.RestartTitle"), Loc.T("Host.Notify.LoggingEnabled"));
            }

            await LoadMetaAsync();
            _metaRefresh.Start();
            // Cards, the catch-up and the meta leave their parsing garbage behind; it is collected once things are quiet.
            Session.RequestMemoryTrim();
            StartUpdateChecks();
        }
        catch (Exception ex)
        {
            Diag.Error("Startup failed", ex);
            if (_rendering)
            {
                throw;
            }

            SetBusy(() => Loc.T("Host.Busy.StartupProblem"));
        }
    }

    /// <summary>
    /// Looks for a newer release shortly after startup and every six hours after that. A downloaded Windows update
    /// installs by itself when the app closes, so keeping Hearthpulse current never needs a download or setup run by hand.
    /// </summary>
    private void StartUpdateChecks()
    {
        if (!Settings.AutoUpdate || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
        {
            SetUpdateStatus(() => Loc.T("Host.Update.Off"));
            return;
        }

        _ = Task.Run(() => UpdateService.WatchAsync(Http, UpdateService.NotifyOnly,
            // The portable version and an AppImage never install anything: they only say a new version is out.
            available: info =>
            {
                var text = UpdateService.IsAppImage ? "Host.Update.AvailableAppImage" : "Host.Update.AvailablePortable";
                Notification?.Invoke(Loc.T("Host.Update.AvailableTitle"), Loc.F(text, info.Version));
            },
            staged: info =>
            {
                _stagedUpdate = info;
                Dispatcher.UIThread.Post(() =>
                {
                    _updateStatus = null;
                    Main.SetUpdateReady(info.Version);
                });
                Notification?.Invoke(Loc.T("Host.Update.ReadyTitle"), Loc.F("Host.Update.Ready", info.Version));
            },
            // Failed check, unverifiable file or failed download: say so in Settings → About, retry later.
            failed: status => Dispatcher.UIThread.Post(() => SetUpdateStatus(status))));
    }

    /// <summary>The "Update ready" chip: install the downloaded update now. Windows restarts into the new version,
    /// elsewhere the downloaded package is handed to the OS installer.</summary>
    private void OnInstallUpdateRequested()
    {
        if (_stagedUpdate is null)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            if (UpdateService.StagedFileMatches(_stagedUpdate))
            {
                UpdateService.OpenInstaller(UpdateService.StagedPath(_stagedUpdate));
            }

            return;
        }

        if (UpdateService.ApplyStaged(_stagedUpdate, relaunch: true))
        {
            _stagedUpdate = null;
            ExitRequested?.Invoke();
        }
    }

    /// <summary>
    /// Russian archetype names, once per run and only while the interface is Russian (the only language that uses them),
    /// fetched beside the meta, never before it: when they arrive the meta is loaded again (from its cache) so its labels
    /// use them. An empty answer lets the next meta load ask again (the loader itself backs off after failures).
    /// </summary>
    private async Task LoadArchetypeNamesAsync()
    {
        var names = await ArchetypeTranslations.LoadAsync(Http);
        if (names.Count == 0)
        {
            _archetypeNames = null;
            return;
        }

        ArchetypeTranslations.Current = names;
        if (Loc.IsRussian)
        {
            await LoadMetaAsync();
        }
    }

    /// <summary>The 3-hour refresh of the Battlegrounds meta (it asks for the collection of its parsing garbage).</summary>
    private Task RefreshMetaAsync() => LoadMetaAsync();

    /// <summary>
    /// Community stats: heroes and minions from the Hearthpulse API, and, when a stats pool is set up, the pool's meta
    /// laid over them for what the API lacks (or the pool's own meta when the API did not load). docs/bg-meta-sources.md.
    /// </summary>
    public async Task LoadMetaAsync()
    {
        var meta = await BattlegroundsMeta.LoadCommunityAsync(Api, Http, Settings);
        if (meta is not null)
        {
            Meta = meta;
            Session.SetMeta(meta);
            Main.SetMeta(meta);
        }

        LoadArenaMetaAsync().Forget("Arena stats load");
        Session.RequestMemoryTrim();

        try
        {
            // The Standard and Wild pages show these stats; they also feed the in-game mulligan panel.
            if (Loc.IsRussian && _archetypeNames is null)
            {
                _archetypeNames = LoadArchetypeNamesAsync();
                _archetypeNames.Forget("Archetype names load");
            }

            _constructedFormats.Add(ConstructedMeta.Key(ConstructedMeta.Formats, Settings.MetaFormat));
            await Task.WhenAll(_constructedFormats.ToList().Select(LoadConstructedMetaAsync));
        }
        catch (Exception ex)
        {
            Diag.Error("Constructed stats unavailable", ex);
        }
    }

    /// <summary>
    /// The stats of one format, asked by the Standard or Wild page or by a game of that format: passed on at once when
    /// they are in, else downloaded unless a download already runs.
    /// </summary>
    private void EnsureConstructedMeta(string format)
    {
        _constructedFormats.Add(format);
        if (_constructedMetas.TryGetValue(format, out var meta))
        {
            Session.SetConstructedMeta(meta);
            Main.SetConstructedMeta(meta);
        }
        else
        {
            LoadConstructedMetaAsync(format).Forget("Constructed meta load");
        }
    }

    private async Task LoadConstructedMetaAsync(string format) => await LoadConstructedMetaAsync(format, restart: false);

    /// <param name="format">The format key ("standard", "wild").</param>
    /// <param name="restart">The filters changed: a download already running is overtaken instead of waited for.</param>
    private async Task LoadConstructedMetaAsync(string format, bool restart)
    {
        if (_constructedLoading.ContainsKey(format) && !restart)
        {
            return;
        }

        var request = ++_constructedRequest;
        _constructedLoading[format] = request;
        bool Latest() => _constructedLoading.TryGetValue(format, out var latest) && latest == request;
        var done = false;

        // The tier list shows as soon as it is in; the mulligan numbers and matchups follow with the full answer.
        var tierList = new Progress<ConstructedMeta>(m =>
        {
            if (!done && Latest())
            {
                Main.SetConstructedMeta(m);
            }
        });
        try
        {
            var constructed = await Core.Constructed.ConstructedMeta.LoadAsync(new HearthpulseData(Api), Cards, format, Settings.MetaRank, Settings.MetaPeriod, Loc.CardLocale, tierList);
            done = true;
            if (!Latest())
            {
                return;
            }

            if (constructed is null)
            {
                if (ConstructedMeta.FormatKeyOf(Main.ConstructedFormat) == format)
                {
                    Main.SetConstructedMeta(null);
                }
            }
            else
            {
                _constructedMetas[format] = constructed;
                if (format == Core.Constructed.ConstructedMeta.Key(Core.Constructed.ConstructedMeta.Formats, Settings.MetaFormat))
                {
                    ConstructedMeta = constructed;
                }

                Session.SetConstructedMeta(constructed);
                Main.SetConstructedMeta(constructed);
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Constructed stats unavailable", ex);
        }
        finally
        {
            if (Latest())
            {
                _constructedLoading.Remove(format);
            }

            // A load (another format, filters, language) leaves about 30 MB of parsing garbage that an idle app would
            // never collect: once things are quiet, it is given back.
            Session.RequestMemoryTrim();
        }
    }

    public async Task ImportLogsAsync(bool force)
    {
        if (InstallDirectory is not { } install)
        {
            SetBusy(() => Loc.T("Host.Busy.NothingToImport"));
            return;
        }

        SetBusy(() => Loc.T("Host.Busy.Importing"));
        var progress = new Progress<string>(text => SetBusy(() => text));
        var result = await Task.Run(() => LogBackfill.Run(install, History, Cards, progress));
        Settings.HistoryImported = true;
        Settings.HistoryStatsFilled = true;
        Settings.HistoryCardsFilled = true;
        Settings.Save();
        SetBusy(force || result.GamesAdded > 0
            ? () => Loc.F("Host.Busy.Imported", result.GamesAdded, result.Sessions)
            : null);
        await Main.RefreshAsync();
    }

    /// <summary>Call after any setting changed: saves (debounced), refreshes the overlay and applies autostart.</summary>
    public void OnSettingsChanged()
    {
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
        // No game plan panel: no comp to choose or see, so the advisor plans around none (a comp saved by an older
        // version stays in the settings, unused).
        Session.SetCompMode(CompMode.Manual, null);
        Session.SetCardPoolEnabled(Settings.ShowCardPool);
        Session.SetArenaDraftEnabled(Settings.ShowArenaDraft);
        Session.SetBgOpponentMmrEnabled(Settings.ShowBgOpponentMmr);
        Session.SetLadderBreak(TimeSpan.FromHours(Math.Clamp(Settings.LadderSessionBreakHours, 1, 12)));
        LoadCardPoolAsync().Forget("Card pool load");
        RefreshOverlay();
        if (!_rendering)
        {
            ApplyAutostart?.Invoke(Settings.StartWithWindows);
        }
    }

    public void RefreshOverlay() => Overlay.Update(_last, Cards, Settings, _deckStats, _matchupStats);

    /// <summary>Shows a busy text in the main window, kept so a language switch can show it again in the new language.</summary>
    private void SetBusy(Func<string>? text)
    {
        _busy = text;
        Main.Busy = text?.Invoke() ?? string.Empty;
    }

    /// <summary>Shows an update status in Settings → About, kept so a language switch can show it again.</summary>
    private void SetUpdateStatus(Func<string> text)
    {
        _updateStatus = text;
        Main.SetUpdateStatus(text());
    }

    /// <summary>
    /// The interface language changed (on the UI thread): the overlay rebuilds its texts from the last snapshot, and the
    /// texts this host owns (busy line, update status) are shown again in the new language. The shell retitles its tray.
    /// </summary>
    private void OnLanguageChanged()
    {
        RefreshOverlay();
        // Meta texts (archetype labels, sources, empty states) are made when the meta loads; the caches make this cheap.
        if (Cards.Count > 0)
        {
            LoadMetaAsync().Forget("Community stats load");
        }

        if (_busy is not null)
        {
            Main.Busy = _busy();
        }

        if (_updateStatus is not null)
        {
            Main.SetUpdateStatus(_updateStatus());
        }
    }

    /// <summary>
    /// Replays the newest kept Battlegrounds log until <paramref name="when"/> matches, for off-screen rendering with
    /// the player's own data. Null when there is no such log.
    /// </summary>
    public BattlegroundsView? ReplayLatestBattlegrounds(Func<BattlegroundsView, bool> when)
    {
        if (InstallDirectory is null)
        {
            return null;
        }

        var log = HearthstoneInstall.AllLogSessions(InstallDirectory).Reverse()
            .SelectMany(d => new[] { Path.Combine(d, "Power.log"), Path.Combine(d, "Power_old.log") })
            .Where(File.Exists)
            .FirstOrDefault(f => ReadShared(f).Take(30_000).Any(x => x.Contains("GameType=GT_BATTLEGROUNDS", StringComparison.Ordinal)));
        if (log is null)
        {
            return null;
        }

        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
        var tracker = new BattlegroundsTracker { Cards = Cards };
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        var lines = 0;
        foreach (var line in ReadShared(log))
        {
            parser.Feed(line);
            if (++lines % 200 != 0 || parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                continue;
            }

            parser.Flush();
            var view = tracker.BuildView(game);
            if (when(view))
            {
                return view;
            }
        }

        return null;
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private void OnSnapshot(LiveSnapshot snapshot)
    {
        // Called on the log thread; coalesce into one UI update per dispatcher pass, at most one per frame.
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
        {
            var delay = _pacer.Delay(Environment.TickCount64);
            if (delay == 0)
            {
                Dispatcher.UIThread.Post(ApplySnapshot, DispatcherPriority.Background);
            }
            else
            {
                _ = Task.Delay(delay).ContinueWith(_ => Dispatcher.UIThread.Post(ApplySnapshot, DispatcherPriority.Background), TaskScheduler.Default);
            }
        }
    }

    private void ApplySnapshot()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        _pacer.Applied(Environment.TickCount64);
        var snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot is null)
        {
            return;
        }

        _last = snapshot;
        Main.Apply(snapshot);
        RefreshDeckStats(snapshot.Constructed);
        RefreshOverlay();
    }

    private void RefreshDeckStats(ConstructedView? view)
    {
        var key = LiveDeckRecord.Key(view);
        if (key == _deckStatsKey)
        {
            return;
        }

        _deckStatsKey = key;
        _deckStats = null;
        _matchupStats = null;
        if (view is null)
        {
            return;
        }

        _ = Task.Run(() => LiveDeckRecord.Read(History, InstallDirectory, view)).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Diag.Error("Deck stats lookup failed", t.Exception);
            }

            if (t.IsCompletedSuccessfully)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_deckStatsKey == key)
                    {
                        (_deckStats, _matchupStats) = t.Result;
                        RefreshOverlay();
                    }
                });
            }
        }, TaskScheduler.Default);
    }

    private void OnGameFinished(GameOutcome outcome)
    {
        // Reads the history row, so off the UI thread; a game that ends while signed out is not sent.
        Task.Run(() => Uploader.OnGameFinished(outcome)).Forget("Profile upload");

        if (Settings.ShowGameToasts && Settings.OverlayEnabled)
        {
            Overlay.ShowOutcome(outcome);
        }
    }

    private static DispatcherTimer Debouncer(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    public void Dispose()
    {
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _watcher?.Dispose();
        Uploader.Dispose();
        _metaRefresh.Stop();
        Memory.Dispose();
        Settings.Save();
        if (_stagedUpdate is not null && UpdateService.ApplyStaged(_stagedUpdate, relaunch: false))
        {
            _stagedUpdate = null;
        }

        Http.Dispose();
    }
}
