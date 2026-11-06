using System.Net;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Hosting;
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
    private readonly LiveFeed _feed;
    private UpdateInfo? _stagedUpdate;
    private readonly AppServices _services;

    /// <param name="rendering">Disables account restore and all live machine changes for off-screen renders.</param>
    public AppHost(AppSettings settings, bool rendering = false)
    {
        _rendering = rendering;
        Settings = settings;
        Loc.Instance.SetLanguage(settings.InterfaceLanguage());
        _services = new AppServices(settings, Version, () => Cards);
        CardArt.Http = Http;

        // A decision of the daily free use (plan 3.1) makes the session publish again.
        Usage.Changed += () => Dispatcher.UIThread.Post(() => Session.SetMeta(Session.Advisor.Meta));
        Main = new MainViewModel(History, Settings, _services.Data);
        Main.AttachAccount(Account);
        Main.AttachUploader(Uploader);
        if (!_rendering)
        {
            Account.RestoreAsync().Forget("Account restore");
        }
        Overlay = new OverlayViewModel();
        _feed = new LiveFeed(
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
            view => LiveDeckRecord.Read(History, InstallDirectory, view),
            Main.Apply,
            RefreshOverlay);
        _constructedLoader = new ConstructedMetaLoader(
            (format, names, tierList, cancel) => Core.Constructed.ConstructedMeta.LoadAsync(new HearthpulseData(Api), Cards, format, Settings.MetaRank, Settings.MetaPeriod,
                Loc.CardLocale, tierList, cancel, names),
            () => ArchetypeTranslations.LoadAsync(Http),
            () => Core.Constructed.ConstructedMeta.Key(Core.Constructed.ConstructedMeta.Formats, Settings.MetaFormat),
            () => Core.Constructed.ConstructedMeta.FormatKeyOf(Main.ConstructedFormat),
            Main.SetConstructedMeta,
            Session.SetConstructedMeta,
            () => Session.RequestMemoryTrimWhenWindowAway(),
            names => (Main.ArchetypeNames, Overlay.ArchetypeNames) = (names, names));
        Session.ApplySettings(Settings);
        Overlay.SecretToggleRequested += Session.ToggleSecret;
        Overlay.CardPoolBrowserResized += OnSettingsChanged;
        Overlay.CounterModeRequested += (id, mode) =>
        {
            Main.ApplyCounterMode(id, mode);
            OnSettingsChanged();
        };
        Main.CountersChanged += OnSettingsChanged;
        // Plan 2.4: the mulligan's rank filter on the meta page; the overlay's guide follows it.
        var mulliganData = new HearthpulseData(Api);
        Main.LoadRankMulligan = (archetype, format, rank, cancel) => MulliganGuideSource.LoadRankSliceAsync(mulliganData, archetype, format, rank, cancel);
        Main.MulliganRankChanged += () =>
        {
            _services.MulliganGuide.Rank = Settings.MulliganRank;
            OnSettingsChanged();
        };
        Main.UpdateRequested += OnInstallUpdateRequested;

        _historyDebounce = Debounce.Timer(TimeSpan.FromMilliseconds(600), () => Main.RefreshAsync().Forget("History refresh"));
        _settingsDebounce = Debounce.Timer(TimeSpan.FromMilliseconds(400), Settings.Save);
        Main.UploadSettingChanged += () =>
        {
            _settingsDebounce.Kick();
        };
        Main.GameStatsChanged += on =>
        {
            _settingsDebounce.Kick();
            if (on) _services.GameStats.Kick();
            else _services.GameStats.Withdraw();
        };
        Main.CrashReportsChanged += () =>
        {
            // Switched off: consent withdrawn, what waits is not sent.
            if (!Settings.SendCrashReports)
            {
                CrashReporter?.ClearPending();
            }

            _settingsDebounce.Kick();
        };
        _metaRefresh = new DispatcherTimer { Interval = BattlegroundsMeta.RefreshInterval };
        _metaRefresh.Tick += (_, _) => RefreshMetaAsync().Forget("Battlegrounds meta load");

        Session.SnapshotChanged += _feed.Publish;
        Session.ConstructedMetaNeeded += format => Dispatcher.UIThread.Post(() => _constructedLoader.Ensure(format));
        Main.ConstructedMetaRequested += format =>
        {
            if (format is not null)
            {
                _constructedLoader.Ensure(format);
                return;
            }

            // A meta filter changed: it is saved, and every format in use loads again for it.
            _settingsDebounce.Kick();
            _constructedLoader.ReloadAllAsync().Forget("Constructed meta load");
        };
        Session.GameFinished += outcome => Dispatcher.UIThread.Post(() => OnGameFinished(outcome));
        History.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _historyDebounce.Kick();
            _feed.ForgetDeck();
        });
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public static string Version { get; } = typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public AppSettings Settings { get; }

    public HttpClient Http => _services.Http;

    /// <summary>The Hearthpulse API (fallback community stats).</summary>
    public HearthpulseApi Api => _services.Api;

    public AccountService Account => _services.Account;

    /// <summary>Sends finished games to the hearthpulse.net profile while signed in (Settings, account card).</summary>
    public ProfileUploader Uploader => _services.Uploader;

    public HistoryStore History => _services.History;

    /// <summary>Daily free use of the stats pool's numbers in the overlay (plan 3.1).</summary>
    public UsageGate Usage => _services.Usage;

    public HearthstoneMemory Memory => _services.Memory;

    /// <summary>
    /// Crash reports the player agreed to (plan 0.5), built by the desktop app before this host so a crash in its
    /// constructor is reported too; null while rendering off-screen.
    /// </summary>
    public Core.Diagnostics.CrashReporter? CrashReporter { get; init; }

    public GameSession Session => _services.Session;

    public MainViewModel Main { get; }

    public OverlayViewModel Overlay { get; }

    public string? InstallDirectory { get; private set; }

    /// <summary>Stats of <see cref="AppSettings.MetaFormat"/> (the format loaded at start); null until they are in.</summary>
    public ConstructedMeta? ConstructedMeta => _constructedLoader.Default;

    private readonly ConstructedMetaLoader _constructedLoader;

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
            Main.ShowBusy(() => Loc.T("Host.Busy.LookingForGame"));
            Task.Run(CacheCleanup.Run).Forget("Cache clean-up");
            (InstallDirectory, var clientLocale) = await _services.Prefetch.Game;
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

            Main.ShowBusy(() => Loc.T("Host.Busy.LoadingCards"));
            Cards = await _services.Prefetch.CardsAsync(locale);
            CardArt.FallbackUrl = id => Cards.BgCards.ImageUrl(id);
            if (Cards.Count == 0 && locale != "enUS")
            {
                Cards = await CardDb.LoadAsync("enUS", Http);
            }

            Session.Cards = Cards;
            Main.SetCards(Cards);
            _services.LoadCardPoolAsync().Forget("Card pool load");

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
            _services.GameStats.Start();
            Main.ShowBusy(InstallDirectory is null
                ? () => Loc.T("Host.Busy.GameNotFound")
                : null);

            if (logConfigChanged && HearthstoneInstall.IsRunning())
            {
                Notification?.Invoke(Loc.T("Host.Notify.RestartTitle"), Loc.T("Host.Notify.LogSettingsChanged"));
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

            Main.ShowBusy(() => Loc.T("Host.Busy.StartupProblem"));
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
            Main.SetUpdateStatus(() => Loc.T("Host.Update.Off"));
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
                    Main.SetUpdateReady(info.Version);
                });
                Notification?.Invoke(Loc.T("Host.Update.ReadyTitle"), Loc.F("Host.Update.Ready", info.Version));
            },
            // Failed check, unverifiable file or failed download: say so in Settings → About, retry later.
            failed: status => Dispatcher.UIThread.Post(() => Main.SetUpdateStatus(status))));
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

    /// <summary>The 6-hour refresh of the Battlegrounds meta; its parsing garbage is collected once things are quiet.</summary>
    private async Task RefreshMetaAsync()
    {
        await LoadMetaAsync();
        Session.RequestMemoryTrim();
    }

    /// <summary>
    /// Community stats: heroes and minions from the Hearthpulse API, and, when a stats pool is set up, the pool's meta
    /// laid over them for what the API lacks (or the pool's own meta when the API did not load). docs/bg-meta-sources.md.
    /// </summary>
    public async Task LoadMetaAsync()
    {
        // The limits on their own: a slow answer never holds the meta back.
        Usage.RefreshLimitsAsync().Forget("Usage limits");
        var meta = await BattlegroundsMeta.LoadCommunityAsync(Api, Http, Settings);
        if (meta is not null)
        {
            Meta = meta;
            Session.SetMeta(meta);
            Main.SetMeta(meta);
        }
        else
        {
            Main.BattlegroundsMetaUnavailable();
        }

        LoadArenaMetaAsync().Forget("Arena stats load");
        LoadBrawlMetaAsync().Forget("Brawl stats load");
        Session.RequestMemoryTrimWhenWindowAway();

        // The Standard and Wild pages show these stats; they also feed the in-game mulligan panel.
        await _constructedLoader.ReloadAllAsync();
    }

    public Task ImportLogsAsync(bool force) => Main.ImportLogsAsync(InstallDirectory, force);

    /// <summary>Call after any setting changed: saves (debounced), refreshes the overlay and applies autostart.</summary>
    public void OnSettingsChanged()
    {
        _settingsDebounce.Kick();
        Session.ApplySettings(Settings);
        _services.LoadCardPoolAsync().Forget("Card pool load");
        RefreshOverlay();
        Motion.Apply(Settings.ReduceMotion);
        if (!_rendering)
        {
            ApplyAutostart?.Invoke(Settings.StartWithWindows);
        }
    }

    public void RefreshOverlay() => Overlay.Update(_feed.Last, Cards, Settings, _feed.Deck, _feed.Matchup);

    /// <summary>
    /// The interface language changed (on the UI thread): the overlay rebuilds its texts from the last snapshot, and the
    /// busy line and update status follow by themselves (MainViewModel). The shell retitles its tray.
    /// </summary>
    private void OnLanguageChanged()
    {
        RefreshOverlay();
        // Meta texts (archetype labels, sources, empty states) are made when the meta loads; the caches make this cheap.
        if (Cards.Count > 0)
        {
            LoadMetaAsync().Forget("Community stats load");
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

    private void OnGameFinished(GameOutcome outcome)
    {
        // Reads the history row, so off the UI thread; a game that ends while signed out is not sent.
        Task.Run(() => Uploader.OnGameFinished(outcome)).Forget("Profile upload");

        if (Settings.ShowGameToasts && Settings.OverlayEnabled)
        {
            Overlay.ShowOutcome(outcome);
        }
    }

    public void Dispose()
    {
        Main.StopMigration();
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        _watcher?.Dispose();
        Uploader.Dispose();
        _services.GameStats.Dispose();
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
