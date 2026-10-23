using System.Net;
using Avalonia.Threading;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Data;
using Hearthlens.Core.Hearthstone;
using Hearthlens.Core.Logs;
using Hearthlens.Core.Memory;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;
using Hearthlens.Core.Updates;
using Hearthlens.UI.Infrastructure;
using Hearthlens.UI.Main;
using Hearthlens.UI.Overlay;

namespace Hearthlens.UI;

/// <summary>
/// Everything the app does apart from its windows: settings, history, the live log pipeline, card data and community
/// stats, and the two view models. Desktop and mobile shells host one of these.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly DispatcherTimer _historyDebounce;
    private readonly DispatcherTimer _settingsDebounce;
    private readonly DispatcherTimer _metaRefresh;
    private LogWatcher? _watcher;
    private LiveSnapshot? _pending;
    private LiveSnapshot _last = LiveSnapshot.Empty;
    private int _applyScheduled;
    private string? _deckStatsKey;
    private DeckAggregate? _deckStats;
    private MatchupAggregate? _matchupStats;
    private UpdateInfo? _stagedUpdate;

    public AppHost(AppSettings settings)
    {
        Settings = settings;
        Http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(40),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthlens/" + Version);
        CardArt.Http = Http;

        History = new HistoryStore(AppPaths.Database);
        Memory = new HearthstoneMemory();
        Session = new GameSession(History, Memory);
        Main = new MainViewModel(History, Settings);
        Overlay = new OverlayViewModel();
        Session.SetCompMode(Settings.CompMode, Settings.ChosenCompId);
        Overlay.PinRequested += OnPinRequested;
        Main.UpdateRequested += OnInstallUpdateRequested;

        _historyDebounce = Debouncer(TimeSpan.FromMilliseconds(600), () => _ = Main.RefreshAsync());
        _settingsDebounce = Debouncer(TimeSpan.FromMilliseconds(400), Settings.Save);
        _metaRefresh = new DispatcherTimer { Interval = TimeSpan.FromHours(3) };
        _metaRefresh.Tick += (_, _) => _ = LoadMetaAsync();

        Session.SnapshotChanged += OnSnapshot;
        Session.GameFinished += outcome => Dispatcher.UIThread.Post(() => OnGameFinished(outcome));
        History.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            _historyDebounce.Stop();
            _historyDebounce.Start();
            _deckStatsKey = null;
        });
    }

    public static string Version { get; } = typeof(AppHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public AppSettings Settings { get; }

    public HttpClient Http { get; }

    public HistoryStore History { get; }

    public HearthstoneMemory Memory { get; }

    public GameSession Session { get; }

    public MainViewModel Main { get; }

    public OverlayViewModel Overlay { get; }

    public string? InstallDirectory { get; private set; }

    public CardDb Cards { get; private set; } = CardDb.Empty;

    public BattlegroundsMeta? Meta { get; private set; }

    public LiveSnapshot LastSnapshot => _last;

    /// <summary>Something the player should know (title, text), e.g. "restart Hearthstone"; shells show it as a notification.</summary>
    public event Action<string, string>? Notification;

    /// <summary>The shell should close the app, e.g. because an update was just handed to its helper.</summary>
    public event Action? ExitRequested;

    /// <summary>Applies the "start when you sign in" setting; set by the shell for its platform.</summary>
    public Action<bool>? ApplyAutostart { get; set; }

    /// <summary>Finds Hearthstone, loads card data, imports history and (unless rendering off-screen) starts following the logs.</summary>
    public async Task InitializeAsync(bool watchLogs = true)
    {
        try
        {
            Main.Busy = "Looking for Hearthstone…";
            InstallDirectory = await Task.Run(() => HearthstoneInstall.FindInstallDirectory(Settings.HearthstonePath));
            if (InstallDirectory is not null && Settings.HearthstonePath != InstallDirectory)
            {
                Settings.HearthstonePath = InstallDirectory;
                Settings.Save();
            }

            Session.InstallDirectory = InstallDirectory;
            var locale = Settings.Locale == "auto" ? HearthstoneInstall.DetectLocale(InstallDirectory) : Settings.Locale;
            CardArt.Locale = locale;

            var logConfigChanged = await Task.Run(() =>
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

            Main.Busy = "Loading card data…";
            Cards = await CardDb.LoadAsync(locale, Http);
            CardArt.FallbackUrl = id => Cards.BgCards.ImageUrl(id);
            if (Cards.Count == 0 && locale != "enUS")
            {
                Cards = await CardDb.LoadAsync("enUS", Http);
            }

            Session.Cards = Cards;
            Main.SetCards(Cards);

            if (!Settings.HistoryImported)
            {
                await ImportLogsAsync(force: false);
            }

            if (!watchLogs)
            {
                return;
            }

            _watcher = new LogWatcher(Session, () => InstallDirectory);
            _watcher.Start();
            Main.Busy = InstallDirectory is null
                ? "Hearthstone not found. Set its folder in settings.json (HearthstonePath) if it is installed somewhere unusual."
                : string.Empty;

            if (logConfigChanged && HearthstoneInstall.IsRunning())
            {
                Notification?.Invoke("Restart Hearthstone", "Hearthlens enabled the game's logging. Restart Hearthstone once so tracking can start.");
            }

            await LoadMetaAsync();
            _metaRefresh.Start();
            StartUpdateChecks();
        }
        catch (Exception ex)
        {
            Diag.Error("Startup failed", ex);
            Main.Busy = "Startup problem — see hearthlens.log in the data folder.";
        }
    }

    /// <summary>
    /// Looks for a newer release shortly after startup and every six hours after that. A downloaded Windows update
    /// installs by itself when the app closes, so keeping Hearthlens current never needs a download or setup run by hand.
    /// </summary>
    private void StartUpdateChecks()
    {
        if (!Settings.AutoUpdate || OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
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
                    var info = await UpdateService.CheckAsync(Http);
                    if (info is not null && await UpdateService.DownloadAsync(Http, info) is not null)
                    {
                        _stagedUpdate = info;
                        Dispatcher.UIThread.Post(() => Main.SetUpdateReady(info.Version));
                        Notification?.Invoke("Hearthlens update ready", $"{info.Version} is downloaded — it installs when you close Hearthlens.");
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
            UpdateService.OpenInstaller(UpdateService.StagedPath(_stagedUpdate));
            return;
        }

        if (UpdateService.ApplyStaged(_stagedUpdate, relaunch: true))
        {
            _stagedUpdate = null;
            ExitRequested?.Invoke();
        }
    }

    public async Task LoadMetaAsync()
    {
        try
        {
            var meta = await BattlegroundsMeta.LoadAsync(Http);
            if (meta is not null)
            {
                Meta = meta;
                Session.SetMeta(meta);
                Main.SetMeta(meta);
            }
        }
        catch (Exception ex)
        {
            Diag.Error("Community stats unavailable", ex);
        }
    }

    public async Task ImportLogsAsync(bool force)
    {
        if (InstallDirectory is not { } install)
        {
            Main.Busy = "Hearthstone install not found — nothing to import.";
            return;
        }

        Main.Busy = "Importing past games from Hearthstone logs…";
        var progress = new Progress<string>(text => Main.Busy = text);
        var result = await Task.Run(() => LogBackfill.Run(install, History, Cards, progress));
        Settings.HistoryImported = true;
        Settings.Save();
        Main.Busy = force || result.GamesAdded > 0
            ? $"Imported {result.GamesAdded} new games from {result.Sessions} log sessions."
            : string.Empty;
        await Main.RefreshAsync();
    }

    /// <summary>Call after any setting changed: saves (debounced), refreshes the overlay and applies autostart.</summary>
    public void OnSettingsChanged()
    {
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
        Session.SetCompMode(Settings.CompMode, Settings.ChosenCompId);
        RefreshOverlay();
        ApplyAutostart?.Invoke(Settings.StartWithWindows);
    }

    /// <summary>The player chose (or cleared) a comp in the overlay. With "I choose" the choice is saved and kept across games.</summary>
    private void OnPinRequested(string? compId)
    {
        Session.PinComp(compId);
        if (Settings.CompMode == CompMode.Manual && Settings.ChosenCompId != compId)
        {
            // Saved only: the overlay follows on the session's next snapshot, which already carries the new target.
            Settings.ChosenCompId = compId;
            _settingsDebounce.Stop();
            _settingsDebounce.Start();
        }
    }

    public void RefreshOverlay() => Overlay.Update(_last, Cards, Settings, _deckStats, _matchupStats);

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
        // Called on the log thread; coalesce into one UI update per dispatcher pass.
        Volatile.Write(ref _pending, snapshot);
        if (Interlocked.Exchange(ref _applyScheduled, 1) == 0)
        {
            Dispatcher.UIThread.Post(ApplySnapshot, DispatcherPriority.Background);
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

        _last = snapshot;
        Main.Apply(snapshot);
        RefreshDeckStats(snapshot.Constructed);
        RefreshOverlay();
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
            var deck = History.DeckStats().FirstOrDefault(d => d.DeckName == view.DeckName && d.PlayerClass == view.PlayerClass);
            var matchup = History.MatchupStats(deck?.DeckCode).FirstOrDefault(m => m.OpponentClass == view.OpponentClass);
            return (deck, matchup);
        }).ContinueWith(t =>
        {
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
        _watcher?.Dispose();
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
