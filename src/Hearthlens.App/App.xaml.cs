using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthlens.App.Infrastructure;
using Hearthlens.App.Main;
using Hearthlens.App.Overlay;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Constructed;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Data;
using Hearthlens.Core.Hearthstone;
using Hearthlens.Core.Logs;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Tracking;
using Hearthlens.Core.Updates;
using Forms = System.Windows.Forms;

namespace Hearthlens.App;

public partial class App : Application
{
    private const int HotkeyId = 0x484C;
    private const int LayoutHotkeyId = 0x484D;
    private const int RefreshHotkeyId = 0x484E;

    private Mutex? _mutex;
    private AppSettings _settings = new();
    private HttpClient _http = null!;
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
    private ConstructedMeta? _constructedMeta;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        for (var i = 0; i < e.Args.Length - 1; i++)
        {
            if (e.Args[i] == "--render-ui")
            {
                _renderDir = e.Args[i + 1];
            }
        }

        _mutex = new Mutex(true, @"Local\Hearthlens.SingleInstance", out var firstInstance);
        if (!firstInstance && _renderDir is null)
        {
            MessageBox.Show("Hearthlens is already running — look for it in the system tray.", "Hearthlens", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Diag.Error("UI error", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Diag.Error("Background error", args.Exception);
            args.SetObserved();
        };

        _settings = AppSettings.Load();
        ApplyTheme();
        Diag.Info($"Hearthlens {UpdateService.CurrentVersion} (app {typeof(App).Assembly.GetName().Version?.ToString(3)}) starting on "
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
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthlens/" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1"));
        CardArt.Http = _http;

        _history = new HistoryStore(AppPaths.Database);
        _memory = new HearthstoneMemory();
        _session = new GameSession(_history, _memory);

        _mainViewModel = new MainViewModel(_history, _settings);
        _mainViewModel.UpdateRequested += OnInstallUpdateRequested;
        _overlayViewModel = new OverlayViewModel();
        _session.SetCompMode(_settings.CompMode, _settings.ChosenCompId);
        _overlayViewModel.PinRequested += OnPinRequested;
        _session.SetCompFocus(_settings.CompFocus);
        _overlayViewModel.FocusRequested += tribe =>
        {
            _settings.CompFocus = tribe;
            _session.SetCompFocus(tribe);
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        };

        _mainViewModel.ThemeChanged += OnThemeChanged;
        _mainViewModel.ConstructedMetaRequested += () =>
        {
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
            _ = LoadConstructedMetaAsync();
        };
        _main = CreateMainWindow();
        _overlay = CreateOverlay();
        if (_renderDir is not null)
        {
            PrepareOffscreenRender();
        }
        else
        {
            _overlay.Show();
            if (!_settings.StartMinimized)
            {
                _main.Show();
            }
        }

        _historyDebounce = Debouncer(TimeSpan.FromMilliseconds(600), () => _ = _mainViewModel.RefreshAsync());
        _settingsDebounce = Debouncer(TimeSpan.FromMilliseconds(400), _settings.Save);
        _metaRefresh = new DispatcherTimer { Interval = TimeSpan.FromHours(3) };
        _metaRefresh.Tick += (_, _) =>
        {
            _ = LoadMetaAsync();
            _ = LoadConstructedMetaAsync();
        };

        _session.SnapshotChanged += OnSnapshot;
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

        _ = InitializeAsync();
        StartUpdateChecks();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watchdog?.Stop();
        _uiProbe?.Dispose();
        _watcher?.Dispose();
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
            _mainViewModel.Busy = "Looking for Hearthstone…";
            _installDir = await Task.Run(() => HearthstoneInstall.FindInstallDirectory(_settings.HearthstonePath));
            if (_installDir is not null && _settings.HearthstonePath != _installDir)
            {
                _settings.HearthstonePath = _installDir;
                _settings.Save();
            }

            _session.InstallDirectory = _installDir;

            var locale = _settings.Locale == "auto" ? HearthstoneInstall.DetectLocale(_installDir) : _settings.Locale;
            CardArt.Locale = locale;

            var logConfigChanged = await Task.Run(() =>
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

            _mainViewModel.Busy = "Loading card data…";
            _cards = await CardDb.LoadAsync(locale, _http);
            CardArt.FallbackUrl = id => _cards.BgCards.ImageUrl(id);
            if (_cards.Count == 0 && locale != "enUS")
            {
                _cards = await CardDb.LoadAsync("enUS", _http);
            }

            _session.Cards = _cards;
            _mainViewModel.SetCards(_cards);

            if (!_settings.HistoryImported)
            {
                await ImportLogsAsync(force: false);
            }

            if (_renderDir is not null)
            {
                await RenderUiAsync(_renderDir);
                return;
            }

            _watcher = new LogWatcher(_session, () => _installDir);
            _watcher.Start();
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _watchdog.Tick += (_, _) => WatchPipeline();
            _watchdog.Start();
            _uiProbe = new System.Threading.Timer(ProbeUiThread, null, 2000, 1000);
            _mainViewModel.Busy = string.Empty;

            if (logConfigChanged && HearthstoneInstall.IsRunning())
            {
                Notify("Restart Hearthstone", "Hearthlens changed the game's log settings. Restart Hearthstone once so tracking starts and lasts whole games.");
            }

            await LoadMetaAsync();
            await LoadConstructedMetaAsync();
            _metaRefresh.Start();
        }
        catch (Exception ex)
        {
            Diag.Error("Startup failed", ex);
            _mainViewModel.Busy = "Startup problem — see hearthlens.log in the data folder.";
        }
    }

    /// <summary>
    /// Looks for a newer release shortly after startup and every six hours after that. A downloaded update installs by
    /// itself when the app closes, so keeping Hearthlens current never needs a download or a setup run by hand.
    /// </summary>
    private void StartUpdateChecks()
    {
        if (!_settings.AutoUpdate)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            while (true)
            {
                try
                {
                    var info = await UpdateService.CheckAsync(_http);
                    if (info is not null && await UpdateService.DownloadAsync(_http, info) is not null)
                    {
                        _stagedUpdate = info;
                        _ = Dispatcher.BeginInvoke(() => _mainViewModel.SetUpdateReady(info.Version));
                        Notify("Hearthlens update ready", $"{info.Version} is downloaded — it installs when you close Hearthlens.");
                        return;
                    }
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

    private async Task LoadMetaAsync()
    {
        try
        {
            var meta = await BattlegroundsMeta.LoadAsync(_http);
            if (meta is not null)
            {
                _meta = meta;
                _session.SetMeta(meta);
                _mainViewModel.SetMeta(meta);
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Community stats unavailable", ex);
        }
    }

    private async Task LoadConstructedMetaAsync()
    {
        // Filters can change while a download runs; only the latest request may update the page.
        var request = ++_constructedMetaRequest;
        try
        {
            var meta = await ConstructedMeta.LoadAsync(_http, _settings.MetaFormat, _settings.MetaRank, _settings.MetaPeriod);
            if (request == _constructedMetaRequest)
            {
                _constructedMeta = meta ?? _constructedMeta;
                _mainViewModel.SetConstructedMeta(meta);
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Constructed stats unavailable", ex);
        }
    }

    private async Task ImportLogsAsync(bool force)
    {
        if (_installDir is null)
        {
            _mainViewModel.Busy = "Hearthstone install not found — nothing to import.";
            return;
        }

        _mainViewModel.Busy = "Importing past games from Hearthstone logs…";
        var progress = new Progress<string>(text => _mainViewModel.Busy = text);
        var result = await Task.Run(() => LogBackfill.Run(_installDir, _history, _cards, progress));
        _settings.HistoryImported = true;
        _settings.Save();
        _mainViewModel.Busy = force || result.GamesAdded > 0
            ? $"Imported {result.GamesAdded} new games from {result.Sessions} log sessions."
            : string.Empty;
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
        var key = view is null ? null : $"{view.DeckName}|{view.PlayerClass}|{view.OpponentClass}";
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
            var deck = _history.DeckStats().FirstOrDefault(d => d.DeckName == view.DeckName && d.PlayerClass == view.PlayerClass);
            var matchup = _history.MatchupStats(deck?.DeckCode).FirstOrDefault(m => m.OpponentClass == view.OpponentClass);
            return (deck, matchup);
        }).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && _deckStatsKey == key)
            {
                (_deckStats, _matchupStats) = t.Result;
                _overlayViewModel.Update(_last, _cards, _settings, _deckStats, _matchupStats);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnGameFinished(GameOutcome outcome)
    {
        if (_settings.ShowGameToasts && _settings.OverlayEnabled)
        {
            _overlayViewModel.ShowOutcome(outcome);
        }
    }

    private MainWindow CreateMainWindow()
    {
        var main = new MainWindow(_mainViewModel);
        main.SettingsChanged += OnSettingsChanged;
        main.ReimportRequested += () => _ = ImportLogsAsync(force: true);
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
        var name = _renderDir is not null && Environment.GetEnvironmentVariable("HEARTHLENS_THEME") is { Length: > 0 } forced ? forced : _settings.Theme;
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

        _session.SetCompMode(_settings.CompMode, _settings.ChosenCompId);
        _overlayViewModel.Update(_last, _cards, _settings, _deckStats, _matchupStats);
        Autostart.Apply(_settings.StartWithWindows);
    }

    /// <summary>The player chose (or cleared) a comp in the overlay. With "I choose" the choice is saved and kept across games.</summary>
    private void OnPinRequested(string? compId)
    {
        _session.PinComp(compId);
        if (_settings.CompMode == CompMode.Manual && _settings.ChosenCompId != compId)
        {
            // Saved only: the overlay follows on the session's next snapshot, which already carries the new target.
            _settings.ChosenCompId = compId;
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        }
    }

    // ---- shell: tray + hotkey ----------------------------------------------------------------

    private void SetupTray()
    {
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/hearthlens.ico"))?.Stream;
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add("Open Hearthlens", null, (_, _) => ShowMain());
        _trayOverlayItem = new Forms.ToolStripMenuItem("Overlay", null, (_, _) => ToggleOverlaySetting()) { Checked = _settings.OverlayEnabled };
        menu.Items.Add(_trayOverlayItem);
        menu.Items.Add("Arrange overlay panels", null, (_, _) => _overlayViewModel.LayoutEditing = !_overlayViewModel.LayoutEditing);
        menu.Items.Add("Hard refresh (re-read the current game)", null, (_, _) => HardRefresh("tray menu"));
        _traySoftwareItem = new Forms.ToolStripMenuItem("Software overlay rendering (restart)", null, (_, _) => ToggleSoftwareRendering())
        {
            Checked = _settings.OverlaySoftwareRendering,
        };
        menu.Items.Add(_traySoftwareItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        foreach (Forms.ToolStripItem item in menu.Items)
        {
            item.ForeColor = System.Drawing.Color.FromArgb(0xF4, 0xE7, 0xC9);
        }

        _tray = new Forms.NotifyIcon
        {
            Icon = iconStream is null ? System.Drawing.SystemIcons.Application : new System.Drawing.Icon(iconStream),
            Text = "Hearthlens",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void Notify(string title, string text)
    {
        _tray?.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info);
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
            Notify("Hearthlens refreshed", "Re-read the current game from Hearthstone's log.");
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
                _ = Task.Run(() => LogConfig.EnsureAll(install));
                Notify("Restart Hearthstone", "Hearthstone stopped writing its game log at its 10 MB limit, so tracking stopped. Hearthlens removed the limit: restart Hearthstone to keep tracking.");
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
        Notify("Overlay renderer", _settings.OverlaySoftwareRendering
            ? "Software rendering is on; restart Hearthlens to apply it."
            : "Hardware rendering is on; restart Hearthlens to apply it.");
    }

    private void Quit()
    {
        _main.CloseForReal();
        _overlay.Close();
        Shutdown();
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
                Notify(_overlay.Suppressed ? "Overlay hidden" : "Overlay shown",
                    _overlay.Suppressed ? "Press Ctrl+Shift+H to show it again." : "Following Hearthstone again.");
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

