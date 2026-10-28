using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Updates;
using Forms = System.Windows.Forms;

namespace Hearthpulse.App;

public partial class App : Application
{
    private const int HotkeyId = 0x484C;
    private const int LayoutHotkeyId = 0x484D;
    private const int RefreshHotkeyId = 0x484E;

    private Mutex? _mutex;
    private AppSettings _settings = new();
    private HttpClient _http = null!;
    private Task? _archetypeNames;
    private HearthpulseApi _api = null!;
    private AccountService _account = null!;
    private ProfileUploader _uploader = null!;
    private HistoryStore _history = null!;
    private HearthstoneMemory _memory = null!;
    private GameSession _session = null!;
    private LogWatcher? _watcher;
    private MainWindow _main = null!;
    private MainViewModel _mainViewModel = null!;
    private OverlayWindow _overlay = null!;
    private OverlayViewModel _overlayViewModel = null!;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _trayOverlayItem;
    private HwndSource? _hotkeySource;
    private string? _installDir;
    private StartupPrefetch _prefetch = null!;
    private CardDb _cards = CardDb.Empty;
    private LiveSnapshot? _pending;
    private LiveSnapshot _last = LiveSnapshot.Empty;
    private int _applyScheduled;
    private DispatcherTimer _historyDebounce = null!;
    private DispatcherTimer _settingsDebounce = null!;
    private DispatcherTimer _metaRefresh = null!;
    private string? _deckStatsKey;
    private DeckAggregate? _deckStats;
    private MatchupAggregate? _matchupStats;
    private string? _renderDir;
    /// <summary>The stats of <see cref="AppSettings.MetaFormat"/> (the format loaded at start), for the render scenes.</summary>
    private ConstructedMeta? _constructedMeta;

    /// <summary>Constructed stats by format key ("standard", "wild"): the page and a game each use their own format's.</summary>
    private readonly Dictionary<string, ConstructedMeta> _constructedMetas = new(StringComparer.Ordinal);

    /// <summary>Formats kept fresh: the one loaded at start, the pages opened and the formats played.</summary>
    private readonly HashSet<string> _constructedFormats = new(StringComparer.Ordinal);

    /// <summary>Downloads running per format: the latest request and its cancel source (a newer request stops an older one).</summary>
    private readonly Dictionary<string, (int Request, CancellationTokenSource Cancel)> _constructedMetaLoads = new(StringComparer.Ordinal);
    private int _constructedMetaRequest;
    private BattlegroundsMeta? _meta;
    private UpdateInfo? _stagedUpdate;
    private DispatcherTimer? _watchdog;
    private Forms.ToolStripMenuItem? _traySoftwareItem;
    private long _lastPending = -1;
    private DateTime _stallSince;
    private DateTime _lastRecovery;
    private DateTime _lastRestart;

    // Diagnostics: how fast the UI thread shows snapshots and answers at all.
    private System.Threading.Timer? _uiProbe;
    private long _probeSentAt;
    private long _lastFreezeNote;
    private long _scheduledAt;
    private int _appliedCount;
    private long _slowestApply;
    private long _longestWait;
    private long _slowestUi;
    private int _watchTicks;
    private bool _capNotified;
    private bool _sharingHistory;
    private Func<string>? _busy;
    private Func<string>? _updateStatus;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        NumberCulture.Apply();
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--render-ui")
            {
                _renderDir = e.Args[i + 1];
            }
        }

        // A --render-ui run never takes the single-instance lock: it closes by itself, and holding the lock would make the
        // player's own copy refuse to start while it runs.
        var firstInstance = true;
        if (_renderDir is null)
        {
            _mutex = new Mutex(true, @"Local\Hearthpulse.SingleInstance", out firstInstance);
        }

        if (!firstInstance)
        {
            // Settings are not loaded yet; read the language only, so the message is in the player's language.
            Loc.Instance.SetLanguage(AppSettings.Load().InterfaceLanguage());
            MessageBox.Show(Loc.T("Host.AlreadyRunning"), "Hearthpulse", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Every unexpected error is logged; the player sees a window (Open log / Continue) the first time a kind of error
        // happens, instead of a silent stop or nothing at all. Not while rendering pages off-screen (--render-ui).
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            if (CrashLog.Record("UI error", args.Exception) && _renderDir is null)
            {
                // This handler runs inside the dispatcher's exception filter, where a modal window is never drawn:
                // show it once the failed operation has unwound.
                var error = args.Exception;
                Dispatcher.BeginInvoke(() => CrashDialog.Show(error, fatal: false));
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString());
            CrashLog.Record(args.IsTerminating ? "Fatal error" : "Unhandled error", ex);
            if (args.IsTerminating && _renderDir is null)
            {
                try
                {
                    // A background thread is dying and takes the process with it: say so before it goes.
                    // Bounded: the UI thread may itself be waiting on the dying thread, and the process must still end.
                    Dispatcher.Invoke(() => CrashDialog.Show(ex, fatal: true), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(30));
                }
                catch (Exception dialogError)
                {
                    Diag.Error("Error window could not be shown", dialogError);
                }
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Record("Background error", args.Exception);
            args.SetObserved();
        };

        _settings = AppSettings.Load();
        Loc.Instance.SetLanguage(_settings.InterfaceLanguage());
        ApplyTheme();
        Diag.Info($"Hearthpulse {UpdateService.CurrentVersion} (app {typeof(App).Assembly.GetName().Version?.ToString(3)}) starting on "
                  + $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {Environment.ProcessorCount} CPUs, "
                  + $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024):N0} MB RAM, software rendering {(_settings.OverlaySoftwareRendering ? "on" : "off")}");
        if (_settings.OverlaySoftwareRendering)
        {
            // Some Windows 10 GPUs and drivers never compose the transparent layered window in hardware,
            // so the overlay is invisible; software rendering is the known workaround.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(40),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthpulse/" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1"));
        CardArt.Http = _http;
        _api = new HearthpulseApi(_http);
        // The game and the cards are looked up while the windows below are built, not after their first frame.
        _prefetch = StartupPrefetch.Start(_settings, _http);

        _history = new HistoryStore(AppPaths.Database) { Replays = Core.Replay.ReplayStore.Default };
        _memory = new HearthstoneMemory();
        _session = new GameSession(_history, _memory);
        _session.SetMulliganGuide(new MulliganGuideSource(new HearthpulseData(_api)));
        _session.SetCardPoolWiki(new Hearthpulse.Core.CardPool.WikiPoolSource(new HearthpulseData(_api)));
        _session.SetDiscoverPicks(new Hearthpulse.Core.CardPool.DiscoverChoiceSource(new HearthpulseData(_api)));

        _mainViewModel = new MainViewModel(_history, _settings);
        _mainViewModel.UpdateRequested += OnInstallUpdateRequested;
        var accountClient = new AccountClient(_http);
        _account = new AccountService(accountClient);
        _mainViewModel.AttachAccount(_account);
        _uploader = ProfileUploader.For(accountClient, _account, () => _settings.UploadGamesToProfile, _history);
        _uploader.BaseHero = id => _cards.BaseHeroId(id);
        _account.Changed += _uploader.Kick;
        _mainViewModel.AttachUploader(_uploader);
        _mainViewModel.UploadSettingChanged += () =>
        {
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        };
        if (_renderDir is null)
        {
            _account.RestoreAsync().Forget("Account restore");
        }
        _overlayViewModel = new OverlayViewModel();
        // No game plan panel: no comp to choose or see, so the advisor plans around none (a comp saved by an older
        // version stays in the settings, unused).
        _session.SetCompMode(CompMode.Manual, null);
        _session.SetCardPoolEnabled(_settings.ShowCardPool);
        _session.SetArenaDraftEnabled(_settings.ShowArenaDraft);
        _session.SetLadderBreak(TimeSpan.FromHours(Math.Clamp(_settings.LadderSessionBreakHours, 1, 12)));
        _overlayViewModel.SecretToggleRequested += _session.ToggleSecret;
        _overlayViewModel.CardPoolBrowserResized += OnSettingsChanged;
        _overlayViewModel.CounterModeRequested += (id, mode) =>
        {
            _mainViewModel.ApplyCounterMode(id, mode);
            OnSettingsChanged();
        };
        _mainViewModel.CountersChanged += OnSettingsChanged;
        _session.SetCompFocus(_settings.CompFocus);
        _overlayViewModel.FocusRequested += tribe =>
        {
            _settings.CompFocus = tribe;
            _session.SetCompFocus(tribe);
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        };

        _mainViewModel.ThemeChanged += OnThemeChanged;
        _mainViewModel.PoolAvailable = PoolClient.Available(_settings);
        _mainViewModel.PoolConsentChanged += () =>
        {
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
            _ = ShareHistoryAsync();
        };
        _mainViewModel.ConstructedMetaRequested += format =>
        {
            if (format is not null)
            {
                EnsureConstructedMeta(format);
                return;
            }

            _settingsDebounce.Stop();
            _settingsDebounce.Start();
            LoadConstructedMetaAsync().Forget("Constructed meta load");
        };
        _main = CreateMainWindow();
        _overlay = CreateOverlay();
        if (_renderDir is not null)
        {
            PrepareOffscreenRender();
        }
        else if (_settings.StartMinimized)
        {
            _overlay.Show();
        }
        else
        {
            // The window shown first pays for the first frame of the whole app: the main window is on screen about
            // 180 ms sooner when the overlay (with no game to cover yet) comes up after it.
            void ShowOverlay(object? sender, EventArgs e)
            {
                ((Window)sender!).ContentRendered -= ShowOverlay;
                _overlay.Show();
            }

            _main.ContentRendered += ShowOverlay;
            _main.Show();
        }

        _historyDebounce = Debouncer(TimeSpan.FromMilliseconds(600), () => _mainViewModel.RefreshAsync().Forget("History refresh"));
        _settingsDebounce = Debouncer(TimeSpan.FromMilliseconds(400), _settings.Save);
        // The pool rebuilds its totals every 6 hours, so asking more often only spends its free requests.
        _metaRefresh = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _metaRefresh.Tick += (_, _) => RefreshMetaAsync().Forget("Meta refresh");

        _session.SnapshotChanged += OnSnapshot;
        _session.ConstructedMetaNeeded += format => Dispatcher.BeginInvoke(() => EnsureConstructedMeta(format));
        _session.GameFinished += outcome => Dispatcher.BeginInvoke(() => OnGameFinished(outcome));
        _history.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            _historyDebounce.Stop();
            _historyDebounce.Start();
            _deckStatsKey = null;
        });

        if (_renderDir is null)
        {
            SetupTray();
            RegisterHotkey();
        }

        Loc.Instance.LanguageChanged += OnLanguageChanged;
        InitializeAsync().Forget("Start-up");
        if (_renderDir is null)
        {
            StartUpdateChecks();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Stop();
        _uiProbe?.Dispose();
        _watcher?.Dispose();
        _uploader?.Dispose();
        if (_hotkeySource is not null)
        {
            Native.UnregisterHotKey(_hotkeySource.Handle, HotkeyId);
            Native.UnregisterHotKey(_hotkeySource.Handle, LayoutHotkeyId);
            Native.UnregisterHotKey(_hotkeySource.Handle, RefreshHotkeyId);
        }

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }

        _memory?.Dispose();
        if (_stagedUpdate is not null && UpdateService.ApplyStaged(_stagedUpdate, relaunch: false))
        {
            _stagedUpdate = null;
        }

        _settings.Save();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            SetBusy(() => Loc.T("Host.Busy.LookingForGame"));
            Task.Run(CacheCleanup.Run).Forget("Cache clean-up");
            (_installDir, var clientLocale) = await _prefetch.Game;
            if (_installDir is not null && _settings.HearthstonePath != _installDir)
            {
                _settings.HearthstonePath = _installDir;
                _settings.Save();
            }

            _session.InstallDirectory = _installDir;

            // "auto" language and cards follow the installed client (a Russian client shows Russian cards and interface).
            _settings.ClientLocale = clientLocale;
            Loc.Instance.SetLanguage(_settings.InterfaceLanguage());
            var locale = _settings.CardLocale();
            CardArt.Locale = locale;

            // HEARTHPULSE_DATA isolates our files, but Hearthstone's log.config is outside that folder.
            var logConfigChanged = _renderDir is null && await Task.Run(() =>
            {
                try
                {
                    return LogConfig.EnsureAll(_installDir);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diag.Error("log.config not writable", ex);
                    return false;
                }
            });

            SetBusy(() => Loc.T("Host.Busy.LoadingCards"));
            _cards = await _prefetch.CardsAsync(locale);
            CardArt.FallbackUrl = id => _cards.BgCards.ImageUrl(id);
            if (_cards.Count == 0 && locale != "enUS")
            {
                _cards = await CardDb.LoadAsync("enUS", _http);
            }

            _session.Cards = _cards;
            _mainViewModel.SetCards(_cards);
            LoadCardPoolAsync().Forget("Card pool load");

            if (!_settings.HistoryImported || !_settings.HistoryStatsFilled || !_settings.HistoryCardsFilled)
            {
                await ImportLogsAsync(force: false);
            }
            else if (_installDir is { } install)
            {
                // Games saved before Decks.log was read correctly (and imported ones) have no deck yet.
                Task.Run(() => LogBackfill.FillDecks(install, _history, _cards)).Forget("Deck backfill");
            }

            if (_renderDir is not null)
            {
                await RenderUiAsync(_renderDir);
                return;
            }

            _watcher = new LogWatcher(_session, () => _installDir);
            _watcher.Start();
            _uploader.Start();
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _watchdog.Tick += (_, _) => WatchPipeline();
            _watchdog.Start();
            _uiProbe = new System.Threading.Timer(ProbeUiThread, null, 2000, 1000);
            SetBusy(null);

            if (logConfigChanged && HearthstoneInstall.IsRunning())
            {
                Notify(Loc.T("Host.Notify.RestartTitle"), Loc.T("Host.Notify.LogSettingsChanged"));
            }

            await LoadMetaAsync();
            await LoadConstructedMetaAsync();
            _metaRefresh.Start();
            // Cards, the catch-up and the meta leave their parsing garbage behind; it is collected once things are quiet.
            _session.RequestMemoryTrim();
            _ = ShareHistoryAsync();
        }
        catch (Exception ex)
        {
            Diag.Error("Startup failed", ex);
            if (_renderDir is not null)
            {
                Quit(1);
                return;
            }

            SetBusy(() => Loc.T("Host.Busy.StartupProblem"));
        }
    }

    /// <summary>
    /// Looks for a newer release shortly after startup and every six hours after that. A downloaded update installs by
    /// itself when the app closes, so keeping Hearthpulse current never needs a download or a setup run by hand.
    /// </summary>
    private void StartUpdateChecks()
    {
        if (!_settings.AutoUpdate)
        {
            SetUpdateStatus(() => Loc.T("Host.Update.Off"));
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            while (true)
            {
                try
                {
                    var check = await UpdateService.CheckForUpdateAsync(_http);
                    var info = check.Update;
                    if (info is not null && AppPaths.IsPortable)
                    {
                        // The setup would install a second copy; the portable one only says a new version is out.
                        Notify(Loc.T("Host.Update.AvailableTitle"), Loc.F("Host.Update.AvailableZip", info.Version));
                        return;
                    }

                    if (info is { Sha256: not null } && await UpdateService.DownloadAsync(_http, info) is not null)
                    {
                        _stagedUpdate = info;
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            _updateStatus = null;
                            _mainViewModel.SetUpdateReady(info.Version);
                        });
                        Notify(Loc.T("Host.Update.ReadyTitle"), Loc.F("Host.Update.Ready", info.Version));
                        return;
                    }

                    // Failed check, unverifiable file or failed download: say so in Settings → About, retry later.
                    var message = check.Message;
                    Func<string> status = info is { Sha256: not null } ? () => Loc.F("Host.Update.DownloadFailed", info.Version) : () => message;
                    _ = Dispatcher.BeginInvoke(() => SetUpdateStatus(status));
                }
                catch (Exception ex)
                {
                    Diag.Error("Update check failed", ex);
                }

                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    /// <summary>The "Update ready" chip: install the downloaded update now and start the new version.</summary>
    private void OnInstallUpdateRequested()
    {
        if (_stagedUpdate is null || !UpdateService.ApplyStaged(_stagedUpdate, relaunch: true))
        {
            return;
        }

        _stagedUpdate = null;
        Quit();
    }

    /// <summary>
    /// Community stats: heroes and minions from the Hearthpulse API, and, when a stats pool is set up, the pool's meta
    /// laid over them for what the API lacks (or the pool's own meta when the API did not load). docs/bg-meta-sources.md.
    /// </summary>
    private async Task LoadMetaAsync()
    {
        BattlegroundsMeta? meta = null;
        try
        {
            meta = await BattlegroundsMeta.LoadAsync(new HearthpulseData(_api));
        }
        catch (Exception ex)
        {
            Diag.Error("Community stats unavailable", ex);
        }

        if (PoolClient.Available(_settings))
        {
            try
            {
                meta = await PoolClient.OverlayAsync(_http, PoolClient.Endpoint(_settings), meta);
            }
            catch (Exception ex)
            {
                Diag.Error("Stats pool unavailable", ex);
            }
        }

        if (meta is not null)
        {
            _meta = meta;
            _session.SetMeta(meta);
            _mainViewModel.SetMeta(meta);
        }

        LoadArenaMetaAsync().Forget("Arena stats load");
    }

    /// <summary>The 6-hour refresh of both metas; their parsing garbage is collected once things are quiet.</summary>
    private async Task RefreshMetaAsync()
    {
        await Task.WhenAll(LoadMetaAsync(), LoadConstructedMetaAsync());
        _session.RequestMemoryTrim();
    }

    /// <summary>Russian archetype names, once per run and only while the interface is Russian (the only language that uses them).</summary>
    private void LoadArchetypeNames()
    {
        if (Loc.IsRussian && _archetypeNames is null)
        {
            _archetypeNames = LoadArchetypeNamesAsync();
            _archetypeNames.Forget("Archetype names load");
        }
    }

    /// <summary>
    /// Fetched beside the meta, never before it: when the names arrive the meta is loaded again (from its cache) so its
    /// labels use them. An empty answer lets the next meta load ask again (the loader itself backs off after failures).
    /// </summary>
    private async Task LoadArchetypeNamesAsync()
    {
        var names = await ArchetypeTranslations.LoadAsync(_http);
        if (names.Count == 0)
        {
            _archetypeNames = null;
            return;
        }

        ArchetypeTranslations.Current = names;
        if (Loc.IsRussian)
        {
            await LoadConstructedMetaAsync();
        }
    }

    /// <summary>Loads the stats of every format in use again (start, filters, language, the 6-hour refresh).</summary>
    private Task LoadConstructedMetaAsync()
    {
        _constructedFormats.Add(ConstructedMeta.Key(ConstructedMeta.Formats, _settings.MetaFormat));
        return Task.WhenAll(_constructedFormats.ToList().Select(LoadConstructedMetaAsync));
    }

    /// <summary>
    /// The stats of one format, asked by the page (Standard or Wild) or by a game of that format: passed on at once when
    /// they are in, else downloaded unless a download already runs.
    /// </summary>
    private void EnsureConstructedMeta(string format)
    {
        _constructedFormats.Add(format);
        if (_constructedMetas.TryGetValue(format, out var meta))
        {
            _mainViewModel.SetConstructedMeta(meta);
            _session.SetConstructedMeta(meta);
        }
        else if (!_constructedMetaLoads.ContainsKey(format))
        {
            LoadConstructedMetaAsync(format).Forget("Constructed meta load");
        }
    }

    private async Task LoadConstructedMetaAsync(string format)
    {
        // Filters can change while a download runs; only the latest request of a format may pass its stats on, and an
        // older one stops.
        var request = ++_constructedMetaRequest;
        if (_constructedMetaLoads.TryGetValue(format, out var running))
        {
            running.Cancel.Cancel();
        }

        var cancel = new CancellationTokenSource();
        _constructedMetaLoads[format] = (request, cancel);
        bool Latest() => _constructedMetaLoads.TryGetValue(format, out var load) && load.Request == request;
        var done = false;

        // The tier list shows as soon as it is in; the mulligan numbers (many more requests) follow with the full answer.
        var tierList = new Progress<ConstructedMeta>(m =>
        {
            if (!done && Latest())
            {
                _mainViewModel.SetConstructedMeta(m);
            }
        });
        try
        {
            LoadArchetypeNames();
            var meta = await ConstructedMeta.LoadAsync(new HearthpulseData(_api), _cards, format, _settings.MetaRank, _settings.MetaPeriod, Loc.CardLocale, tierList, cancel.Token);
            done = true;
            if (!Latest())
            {
                return;
            }

            _constructedMetaLoads.Remove(format);
            if (meta is not null)
            {
                _constructedMetas[format] = meta;
                if (format == ConstructedMeta.Key(ConstructedMeta.Formats, _settings.MetaFormat))
                {
                    _constructedMeta = meta;
                }

                _mainViewModel.SetConstructedMeta(meta);
                _session.SetConstructedMeta(meta);
            }
            else if (ConstructedMeta.FormatKeyOf(_mainViewModel.ConstructedFormat) == format)
            {
                _mainViewModel.SetConstructedMeta(null);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (Latest())
            {
                _constructedMetaLoads.Remove(format);
            }

            Diag.Error("Constructed stats unavailable", ex);
        }
    }

    private async Task ImportLogsAsync(bool force)
    {
        if (_installDir is null)
        {
            SetBusy(() => Loc.T("Host.Busy.NothingToImport"));
            return;
        }

        SetBusy(() => Loc.T("Host.Busy.Importing"));
        var progress = new Progress<string>(text => SetBusy(() => text));
        var result = await Task.Run(() => LogBackfill.Run(_installDir, _history, _cards, progress));
        _settings.HistoryImported = true;
        _settings.HistoryStatsFilled = true;
        _settings.HistoryCardsFilled = true;
        _settings.Save();
        SetBusy(force || result.GamesAdded > 0
            ? () => Loc.F("Host.Busy.Imported", result.GamesAdded, result.Sessions)
            : null);
        await _mainViewModel.RefreshAsync();
    }

    // ---- live snapshots ---------------------------------------------------------------------

    private void OnSnapshot(LiveSnapshot snapshot)
    {
        // Called on the log thread; coalesce into one UI update per dispatcher pass.
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
        {
            Volatile.Write(ref _scheduledAt, Environment.TickCount64);
            Dispatcher.BeginInvoke(ApplySnapshot, DispatcherPriority.Background);
        }
    }

    private void ApplySnapshot()
    {
        Interlocked.Exchange(ref _applyScheduled, 0);
        var snapshot = Interlocked.Exchange(ref _pending, null);
        if (snapshot is null)
        {
            return;
        }

        var waited = Environment.TickCount64 - Volatile.Read(ref _scheduledAt);
        _longestWait = Math.Max(_longestWait, waited);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _last = snapshot;
        _mainViewModel.Apply(snapshot);
        RefreshDeckStats(snapshot.Constructed);
        _overlayViewModel.Update(snapshot, _cards, _settings, _deckStats, _matchupStats);
        var took = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _appliedCount++;
        _slowestApply = Math.Max(_slowestApply, took);
        if (took > 200 || waited > 1000)
        {
            Diag.Info($"Showing a snapshot took {took:N0} ms after waiting {waited:N0} ms for the UI thread (BG turn {snapshot.Battlegrounds?.Turn})");
        }
    }

    /// <summary>
    /// Watches the UI thread from a background thread. When the window stops answering, this writes it to the log while
    /// it happens; a frozen window could never write that line itself.
    /// </summary>
    private void ProbeUiThread(object? state)
    {
        var now = Environment.TickCount64;
        var sent = Volatile.Read(ref _probeSentAt);
        var snapshotWait = Volatile.Read(ref _applyScheduled) == 1 ? now - Volatile.Read(ref _scheduledAt) : 0;
        if (((sent != 0 && now - sent > 5000) || snapshotWait > 5000) && now - _lastFreezeNote > 10_000)
        {
            _lastFreezeNote = now;
            Diag.Info($"UI thread slow: {(sent != 0 ? $"not answering for {now - sent:N0} ms" : "answering")}, newest snapshot waiting {snapshotWait:N0} ms to be shown");
        }

        if (sent != 0)
        {
            return;
        }

        Volatile.Write(ref _probeSentAt, now);
        Dispatcher.BeginInvoke(() =>
        {
            var lag = Environment.TickCount64 - Volatile.Read(ref _probeSentAt);
            Volatile.Write(ref _probeSentAt, 0);
            _slowestUi = Math.Max(_slowestUi, lag);
            if (lag > 1000)
            {
                Diag.Info($"UI thread answered after {lag:N0} ms");
            }
        }, DispatcherPriority.Normal);
    }

    private void RefreshDeckStats(ConstructedView? view)
    {
        var key = view is null ? null : $"{view.DeckName}|{view.DeckId}|{view.DeckCode}|{view.PlayerClass}|{view.OpponentClass}";
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

        _ = Task.Run(() =>
        {
            var sameDeck = _installDir is { } dir ? DecksLog.CodesOf(DecksLog.ReadAll(dir), view.DeckId) : [];
            var all = _history.Recent(int.MaxValue, "Constructed");
            // Versions the player merged into one deck count together, as on the deck page.
            var games = Core.Stats.DeckFamilies.WithFamily(all, DeckRecords.Matching(all, view.DeckName, view.DeckCode, view.PlayerClass, sameDeck), _history.DeckFamilies());
            var deck = DeckRecords.Aggregate(games, view.DeckName, view.DeckCode, view.PlayerClass);
            // The matchup line counts the same games as the record; without any, the class matchup over all decks.
            var matchup = games.Count > 0
                ? DeckRecords.Matchup(games, view.OpponentClass)
                : _history.MatchupStats().FirstOrDefault(m => m.OpponentClass == view.OpponentClass);
            return (deck, matchup);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Diag.Error("Deck stats lookup failed", t.Exception);
            }

            if (t.IsCompletedSuccessfully && _deckStatsKey == key)
            {
                (_deckStats, _matchupStats) = t.Result;
                _overlayViewModel.Update(_last, _cards, _settings, _deckStats, _matchupStats);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private IReadOnlyList<string> CardRaces(string cardId) => _cards.Get(cardId)?.Races ?? [];

    /// <summary>
    /// Once the player agrees to the full list, their Battlegrounds games from the last 30 days are shared once,
    /// 20 per request. Ids are stable, so a game already sent live is not stored twice.
    /// </summary>
    private async Task ShareHistoryAsync()
    {
        if (_sharingHistory || _settings.PoolHistoryShared || !PoolClient.SharingAll(_settings) || _cards.Count == 0 || _renderDir is not null)
        {
            return;
        }

        _sharingHistory = true;
        try
        {
            var secret = PoolClient.Secret(_settings);
            var since = DateTime.Now.AddDays(-PoolClient.HistoryDays);
            var games = await Task.Run(() => _history.Since(since)
                .Where(g => g.IsBattlegrounds)
                .Select(g => PoolGame.FromHistory(g, secret, _cards.BaseHeroId, CardRaces))
                .OfType<PoolGame>()
                .ToList());
            foreach (var chunk in games.Chunk(PoolClient.MaxBatch))
            {
                if (!await PoolClient.SubmitAsync(_http, PoolClient.Endpoint(_settings), chunk))
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }

            _settings.PoolHistoryShared = true;
            Diag.Info($"Shared {games.Count} earlier game(s) with the stats pool");
        }
        catch (Exception ex)
        {
            Diag.Error("Sharing earlier games failed", ex);
        }
        finally
        {
            _sharingHistory = false;
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        }
    }

    private void OnGameFinished(GameOutcome outcome)
    {
        // Reads the history row, so off the UI thread; a game that ends while signed out is not sent.
        Task.Run(() => _uploader.OnGameFinished(outcome)).Forget("Profile upload");

        // The event fires again once the new rating is read; share the game only the first time.
        if (outcome.RatingAfter is null && PoolClient.Sharing(_settings)
            && PoolGame.From(outcome, _cards.BaseHeroId, CardRaces, PoolClient.SharingDetails(_settings), PoolClient.Secret(_settings)) is { } game)
        {
            _ = PoolClient.SubmitAsync(_http, PoolClient.Endpoint(_settings), [PoolClient.Limit(game, _settings)]);
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        }

        // A Constructed game shares only its Discover-style picks, once the player agreed to that list (version 4).
        if (PoolClient.SharingPicks(_settings) && ConstructedPoolGame.From(outcome, PoolClient.Secret(_settings)) is { } picks)
        {
            _ = PoolClient.SubmitConstructedAsync(_http, PoolClient.Endpoint(_settings), [picks]);
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        }

        if (_settings.ShowGameToasts && _settings.OverlayEnabled)
        {
            _overlayViewModel.ShowOutcome(outcome);
        }
    }

    private MainWindow CreateMainWindow()
    {
        var main = new MainWindow(_mainViewModel);
        main.SettingsChanged += OnSettingsChanged;
        main.ReimportRequested += () => ImportLogsAsync(force: true).Forget("Log import");
        main.ArrangeOverlayRequested += () => _overlayViewModel.LayoutEditing = !_overlayViewModel.LayoutEditing;
        return main;
    }

    private OverlayWindow CreateOverlay()
    {
        var overlay = new OverlayWindow(_overlayViewModel, _settings);
        overlay.LayoutChanged += OnSettingsChanged;
        overlay.HoveredHero = () => _memory.IsAttached ? _memory.GetHoveredLeaderboardHero() : null;
        return overlay;
    }

    private ThemeSpec CurrentTheme()
    {
        // Off-screen renders can preview a theme without touching the saved settings.
        var name = _renderDir is not null && Environment.GetEnvironmentVariable("HEARTHPULSE_THEME") is { Length: > 0 } forced ? forced : _settings.Theme;
        return ThemePalette.Resolve(name, _settings.ThemeAccentHue, _settings.ThemeBaseHue, _settings.ThemeBaseTint);
    }

    private bool ApplyTheme()
    {
        try
        {
            ThemeEngine.Apply(this, CurrentTheme());
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Diag.Error("Theme could not be built; keeping the current colours", ex);
            return false;
        }
    }

    /// <summary>Windows take their colours when they are created, so a new theme rebuilds both in place.</summary>
    private void OnThemeChanged()
    {
        _settings.Save();
        if (!ApplyTheme())
        {
            return;
        }

        var oldMain = _main;
        var bounds = oldMain.WindowState == WindowState.Normal ? new Rect(oldMain.Left, oldMain.Top, oldMain.Width, oldMain.Height) : oldMain.RestoreBounds;
        var main = CreateMainWindow();
        main.WindowStartupLocation = WindowStartupLocation.Manual;
        main.Left = bounds.Left;
        main.Top = bounds.Top;
        main.Width = bounds.Width;
        main.Height = bounds.Height;
        main.ShowPage(oldMain.CurrentPage);
        _main = main;
        if (oldMain.IsVisible)
        {
            main.Show();
            if (oldMain.WindowState == WindowState.Maximized)
            {
                main.WindowState = WindowState.Maximized;
            }

            main.Activate();
        }

        oldMain.CloseForReal();

        var oldOverlay = _overlay;
        _overlay = CreateOverlay();
        _overlay.Suppressed = oldOverlay.Suppressed;
        oldOverlay.Close();
        _overlay.Show();
    }

    private void OnSettingsChanged()
    {
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
        if (_trayOverlayItem is not null)
        {
            _trayOverlayItem.Checked = _settings.OverlayEnabled;
        }

        // No game plan panel: no comp to choose or see, so the advisor plans around none (a comp saved by an older
        // version stays in the settings, unused).
        _session.SetCompMode(CompMode.Manual, null);
        _session.SetCardPoolEnabled(_settings.ShowCardPool);
        _session.SetArenaDraftEnabled(_settings.ShowArenaDraft);
        _session.SetLadderBreak(TimeSpan.FromHours(Math.Clamp(_settings.LadderSessionBreakHours, 1, 12)));
        LoadCardPoolAsync().Forget("Card pool load");
        _overlayViewModel.Update(_last, _cards, _settings, _deckStats, _matchupStats);
        if (_renderDir is null)
        {
            Autostart.Apply(_settings.StartWithWindows);
        }
    }

    // ---- shell: tray + hotkey ----------------------------------------------------------------

    private void SetupTray()
    {
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/hearthpulse.ico"))?.Stream;
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        // Each item keeps its text's key in Tag, so a language switch can retitle it (SetTrayTexts).
        menu.Items.Add(Loc.T("Host.Tray.Open"), null, (_, _) => ShowMain()).Tag = "Host.Tray.Open";
        _trayOverlayItem = new Forms.ToolStripMenuItem(Loc.T("Host.Tray.Overlay"), null, (_, _) => ToggleOverlaySetting())
        {
            Checked = _settings.OverlayEnabled,
            Tag = "Host.Tray.Overlay",
        };
        menu.Items.Add(_trayOverlayItem);
        menu.Items.Add(Loc.T("Host.Tray.Arrange"), null, (_, _) => _overlayViewModel.LayoutEditing = !_overlayViewModel.LayoutEditing).Tag = "Host.Tray.Arrange";
        menu.Items.Add(Loc.T("Host.Tray.HardRefresh"), null, (_, _) => HardRefresh("tray menu")).Tag = "Host.Tray.HardRefresh";
        _traySoftwareItem = new Forms.ToolStripMenuItem(Loc.T("Host.Tray.SoftwareRendering"), null, (_, _) => ToggleSoftwareRendering())
        {
            Checked = _settings.OverlaySoftwareRendering,
            Tag = "Host.Tray.SoftwareRendering",
        };
        menu.Items.Add(_traySoftwareItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Host.Tray.Quit"), null, (_, _) => Quit()).Tag = "Host.Tray.Quit";
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = System.Drawing.Color.FromArgb(0xF4, 0xE7, 0xC9);
        }

        _tray = new Forms.NotifyIcon
        {
            Icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream),
            Text = "Hearthpulse",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void Notify(string title, string text)
    {
        _tray?.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info);
    }

    /// <summary>Retitles the tray menu in the current language (each item's Tag holds its key).</summary>
    private void SetTrayTexts()
    {
        if (_tray?.ContextMenuStrip is not { } menu)
        {
            return;
        }

        foreach (Forms.ToolStripItem item in menu.Items)
        {
            if (item.Tag is string key)
            {
                item.Text = Loc.T(key);
            }
        }
    }

    /// <summary>Shows a busy text in the main window, kept so a language switch can show it again in the new language.</summary>
    private void SetBusy(Func<string>? text)
    {
        _busy = text;
        _mainViewModel.Busy = text?.Invoke() ?? string.Empty;
    }

    /// <summary>Shows an update status in Settings → About, kept so a language switch can show it again.</summary>
    private void SetUpdateStatus(Func<string> text)
    {
        _updateStatus = text;
        _mainViewModel.SetUpdateStatus(text());
    }

    /// <summary>
    /// The interface language changed (on the UI thread): the overlay rebuilds its texts from the last snapshot, and the
    /// texts this host owns (tray menu, busy line, update status) are shown again in the new language.
    /// </summary>
    private void OnLanguageChanged()
    {
        _overlayViewModel.Update(_last, _cards, _settings, _deckStats, _matchupStats);
        // Meta texts (archetype labels, sources, empty states) are made when the meta loads; the caches make this cheap.
        if (_cards.Count > 0 && _renderDir is null)
        {
            LoadMetaAsync().Forget("Battlegrounds meta load");
            LoadConstructedMetaAsync().Forget("Constructed meta load");
        }

        SetTrayTexts();
        if (_busy is not null)
        {
            _mainViewModel.Busy = _busy();
        }

        if (_updateStatus is not null)
        {
            _mainViewModel.SetUpdateStatus(_updateStatus());
        }
    }

    private void ShowMain()
    {
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    private void ToggleOverlaySetting()
    {
        _settings.OverlayEnabled = !_settings.OverlayEnabled;
        _main.SyncOverlaySwitch();
        OnSettingsChanged();
    }

    /// <summary>
    /// Re-reads the log and rebuilds the tracked game: the manual recovery for an overlay frozen on an old turn.
    /// It also un-hides a suppressed overlay and re-asserts topmost, so a vanished overlay comes back with it.
    /// </summary>
    private void HardRefresh(string reason, bool quiet = false)
    {
        if (_watcher is null)
        {
            return;
        }

        Diag.Info($"Hard refresh ({reason})");
        _watcher.RequestResync(replayCurrentGame: true);
        _overlay.Suppressed = false;
        _overlay.ReassertTopmost();
        _lastPending = -1;
        _stallSince = default;
        if (!quiet)
        {
            Notify(Loc.T("Host.Notify.RefreshedTitle"), Loc.T("Host.Notify.Refreshed"));
        }
    }

    /// <summary>
    /// Watches the log pipeline itself. A watcher that stops polling was killed by an error on a background
    /// thread; a log that keeps growing while nothing is delivered means the parser is wedged. Either way the
    /// overlay would stay frozen, so the pipeline is restarted or the current game is replayed.
    /// </summary>
    private void WatchPipeline()
    {
        if (_watcher is null)
        {
            return;
        }

        if (++_watchTicks % 8 == 0)
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Diag.Debug($"Window: showed {_appliedCount} snapshots (BG turn {_last.Battlegrounds?.Turn}), slowest {_slowestApply:N0} ms, "
                       + $"longest wait {_longestWait:N0} ms, slowest UI answer {_slowestUi:N0} ms, overlay {(_overlay.Suppressed ? "hidden" : "on")}, "
                       + $"RAM {process.WorkingSet64 / (1024 * 1024):N0} MB, managed {GC.GetTotalMemory(false) / (1024 * 1024):N0} MB");
            _appliedCount = 0;
            _slowestApply = 0;
            _longestWait = 0;
            _slowestUi = 0;
        }

        if (_watcher.PowerLogCapped)
        {
            if (!_capNotified)
            {
                _capNotified = true;
                var install = _installDir;
                Task.Run(() => LogConfig.EnsureAll(install)).Forget("log.config update");
                Notify(Loc.T("Host.Notify.RestartTitle"), Loc.T("Host.Notify.LogCapped"));
            }
        }
        else
        {
            _capNotified = false;
        }

        if (_watcher.LastProgressAgeMs > 15_000)
        {
            // A long replay delivers batches and keeps the clock moving, so only a pass stuck in one place gets here.
            // At most one restart a minute: restarting a watcher that is still busy only adds work.
            if (DateTime.UtcNow - _lastRestart >= TimeSpan.FromSeconds(60))
            {
                _lastRestart = DateTime.UtcNow;
                Diag.Info($"Log watcher stuck for {_watcher.LastProgressAgeMs / 1000}s while {_watcher.Stage}; restarting it.");
                RestartWatcher();
            }

            return;
        }

        if (_watcher.LastPollAgeMs > 2_000)
        {
            // A pass is still running (a replay or a big batch): that is busy, not stalled. Replaying the game now
            // would only pile more work on a slow PC.
            _stallSince = default;
            return;
        }

        var pending = _watcher.PendingBytes;
        if (pending <= 0 || pending < _lastPending)
        {
            // Caught up, or still draining a backlog (progress) — not a stall.
            _stallSince = default;
            _lastPending = pending;
            return;
        }

        _lastPending = pending;
        if (_stallSince == default)
        {
            _stallSince = DateTime.UtcNow;
            return;
        }

        if (DateTime.UtcNow - _stallSince < TimeSpan.FromSeconds(12) || DateTime.UtcNow - _lastRecovery < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _lastRecovery = DateTime.UtcNow;
        Diag.Info($"Log pipeline stalled with {pending} unread bytes; re-reading the current game.");
        HardRefresh("automatic recovery", quiet: true);
    }

    private void RestartWatcher()
    {
        try
        {
            _watcher?.Dispose();
        }
        catch (Exception ex)
        {
            Diag.Error("Log watcher restart failed", ex);
        }

        _watcher = new LogWatcher(_session, () => _installDir);
        _watcher.Start();
        _lastPending = -1;
        _stallSince = default;
    }

    private void ToggleSoftwareRendering()
    {
        _settings.OverlaySoftwareRendering = !_settings.OverlaySoftwareRendering;
        if (_traySoftwareItem is not null)
        {
            _traySoftwareItem.Checked = _settings.OverlaySoftwareRendering;
        }

        _settings.Save();
        Notify(Loc.T("Host.Notify.RendererTitle"), _settings.OverlaySoftwareRendering
            ? Loc.T("Host.Notify.SoftwareRendering")
            : Loc.T("Host.Notify.HardwareRendering"));
    }

    private void Quit(int exitCode = 0)
    {
        _main.CloseForReal();
        _overlay.Close();
        Shutdown(exitCode);
    }

    private void RegisterHotkey()
    {
        var handle = new WindowInteropHelper(_main).EnsureHandle();
        _hotkeySource = HwndSource.FromHwnd(handle);
        _hotkeySource?.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                _overlay.Suppressed = !_overlay.Suppressed;
                Notify(_overlay.Suppressed ? Loc.T("Host.Notify.OverlayHiddenTitle") : Loc.T("Host.Notify.OverlayShownTitle"),
                    _overlay.Suppressed ? Loc.T("Host.Notify.OverlayHidden") : Loc.T("Host.Notify.OverlayShown"));
                handled = true;
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == RefreshHotkeyId)
            {
                HardRefresh("Ctrl+Shift+R");
                handled = true;
            }
            else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == LayoutHotkeyId)
            {
                _overlayViewModel.LayoutEditing = !_overlayViewModel.LayoutEditing;
                handled = true;
            }

            return IntPtr.Zero;
        });
        if (!Native.RegisterHotKey(handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x48))
        {
            Diag.Info("Ctrl+Shift+H is taken by another app; overlay hotkey disabled.");
        }

        if (!Native.RegisterHotKey(handle, LayoutHotkeyId, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x4C))
        {
            Diag.Info("Ctrl+Shift+L is taken by another app; layout hotkey disabled.");
        }

        if (!Native.RegisterHotKey(handle, RefreshHotkeyId, Native.MOD_CONTROL | Native.MOD_SHIFT | Native.MOD_NOREPEAT, 0x52))
        {
            Diag.Info("Ctrl+Shift+R is taken by another app; hard refresh hotkey disabled.");
        }
    }

    private DispatcherTimer Debouncer(TimeSpan delay, Action action)
    {
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        return timer;
    }

    /// <summary>Tray menu in the app's brown and gold palette (Surface, Surface3, Border and Accent).</summary>
    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly System.Drawing.Color Background = System.Drawing.Color.FromArgb(0x21, 0x16, 0x0E);
        private static readonly System.Drawing.Color Hover = System.Drawing.Color.FromArgb(0x3A, 0x28, 0x17);
        private static readonly System.Drawing.Color Line = System.Drawing.Color.FromArgb(0x3D, 0x2A, 0x19);

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) => e.Graphics.Clear(Background);

        // The check column is on (ShowCheckMargin); left to the base renderer it is a light grey strip.
        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new System.Drawing.SolidBrush(Background);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new System.Drawing.Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            using var brush = new System.Drawing.SolidBrush(e.Item.Selected ? Hover : Background);
            e.Graphics.FillRectangle(brush, new System.Drawing.Rectangle(System.Drawing.Point.Empty, e.Item.Size));
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new System.Drawing.Pen(Line);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0xE9, 0xB9, 0x49));
            e.Graphics.FillEllipse(brush, e.ImageRectangle.X + 4, e.ImageRectangle.Y + 4, 8, 8);
        }
    }
}

