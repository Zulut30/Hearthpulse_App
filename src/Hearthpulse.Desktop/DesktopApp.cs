using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Desktop.Platform;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Desktop;

/// <summary>Desktop shell: dashboard window, overlay window, tray icon, hotkeys and autostart.</summary>
internal sealed class DesktopApp : HearthpulseApp
{
    private const string ActivatePipe = "Hearthpulse.Activate";

    /// <summary>
    /// <c>--smoke-test</c> (CI on Linux and macOS): starts as usual, with the windows, tray and overlay, and quits once
    /// start-up is done. The exit code is 0 only when the card data loaded.
    /// </summary>
    private static readonly bool SmokeTest = Environment.GetCommandLineArgs().Contains("--smoke-test");

    private SingleInstance? _instance;
    private AppHost? _host;

    /// <summary>
    /// The dashboard: built when first shown (start minimized: from the tray), let go after a minute hidden in the tray
    /// (<see cref="ReleaseMain"/>), forgotten once closed for good; <see cref="ShowMain"/> builds it again where it was.
    /// </summary>
    private readonly MainWindowLife<MainWindow, MainPlace> _mainLife = new(PlaceOf);

    /// <summary>Null while there is no dashboard to show (see <see cref="_mainLife"/>).</summary>
    private MainWindow? _main => _mainLife.Window;

    /// <summary>While the dashboard waits in the tray: is it time to let it go? (A game in progress puts it off.)</summary>
    private DispatcherTimer? _releaseMain;
    private OverlayWindow? _overlay;
    private IOverlayPlatform? _platform;
    private NativeMenuItem? _overlayItem;
    private readonly List<(NativeMenuItem Item, string Key)> _trayTexts = [];
    private IDisposable? _hotkeys;
    private CancellationTokenSource? _pipeCts;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Start(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirst)
        {
            // Bring the running copy to the front instead of starting a second one.
            AskRunningInstanceToShow();
            Dispatcher.UIThread.Post(() => desktop.Shutdown());
            return;
        }

        // Every unexpected error is logged; the player sees a window (Open log / Continue) the first time a kind of error
        // happens, instead of a silent stop or nothing at all.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            if (CrashLog.Record("UI error", e.Exception))
            {
                CrashWindow.ShowAsync(e.Exception, fatal: false).Forget("Error window");
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new InvalidOperationException(e.ExceptionObject?.ToString());
            CrashLog.Record(e.IsTerminating ? "Fatal error" : "Unhandled error", ex);
            if (e.IsTerminating && !Dispatcher.UIThread.CheckAccess())
            {
                try
                {
                    // A background thread is dying and takes the process with it: say so before it goes.
                    Dispatcher.UIThread.InvokeAsync(() => CrashWindow.ShowAsync(ex, fatal: true)).Wait(TimeSpan.FromSeconds(30));
                }
                catch (Exception dialogError)
                {
                    Diag.Error("Error window could not be shown", dialogError);
                }
            }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Record("Background error", e.Exception);
            e.SetObserved();
        };

        var settings = AppSettings.Load();
        _host = new AppHost(settings) { ApplyAutostart = Autostart.Apply };
        // Before any window: started minimized there is no dashboard to apply it, and the overlay comes first.
        ThemeEngine.ApplySaved(this, settings);
        _releaseMain = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _releaseMain.Tick += (_, _) => ReleaseMain();
        // Started minimized, the dashboard is built the first time it is opened from the tray. A notification with no
        // dashboard goes to the log, as it would not show in a hidden one either.
        if (!settings.StartMinimized)
        {
            _mainLife.Open(CreateMain);
        }

        _host.Notification += (title, text) =>
        {
            if (_main is null)
            {
                Diag.Info($"Notification while the dashboard is let go: {title}. {text}");
            }
            else
            {
                _main.Notify(title, text);
            }
        };
        _host.ExitRequested += Quit;

        _platform = OverlayPlatform.Create();
        if (_platform.Supported)
        {
            _overlay = new OverlayWindow(_host, _platform);
            _overlay.Show();
            _overlay.Opened += (_, _) =>
            {
                if (OperatingSystem.IsWindows())
                {
                    _hotkeys ??= new WindowsHotkeys(_overlay, ToggleSuppressed, ToggleLayout);
                }
            };
        }
        else
        {
            Diag.Info("No overlay on this desktop session; the dashboard still works.");
        }

        _main?.Show();
        SyncWindowInUse();

        SetupTray();
        ListenForActivation();
        desktop.Exit += (_, _) => Shutdown();
        var startup = _host.InitializeAsync();
        if (SmokeTest)
        {
            ReportSmokeTestAsync(desktop, startup).Forget("Smoke test");
        }
        else
        {
            startup.Forget("Start-up");
        }
    }

    private async Task ReportSmokeTestAsync(IClassicDesktopStyleApplicationLifetime desktop, Task startup)
    {
        var finished = await Task.WhenAny(startup, Task.Delay(TimeSpan.FromMinutes(2))) == startup;
        var cards = _host!.Cards.Count;
        Console.WriteLine($"Smoke test: start-up {(finished ? "done" : "timed out")}, {cards} cards, "
                          + $"overlay {(_platform!.Supported ? _platform.GetType().Name : "unsupported")}, data {AppPaths.Root}");
        if (_main is not null)
        {
            _main.AllowClose = true;
        }

        desktop.Shutdown(finished && cards > 0 ? 0 : 1);
    }

    private void SetupTray()
    {
        _overlayItem = new NativeMenuItem(Loc.T("Host.Tray.Overlay"))
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _host!.Settings.OverlayEnabled,
        };
        _overlayItem.Click += (_, _) =>
        {
            _host.Settings.OverlayEnabled = !_host.Settings.OverlayEnabled;
            _main?.View.SyncOverlaySwitch();
            _host.OnSettingsChanged();
            _overlayItem.IsChecked = _host.Settings.OverlayEnabled;
        };

        var open = new NativeMenuItem(Loc.T("Host.Tray.Open"));
        open.Click += (_, _) => ShowMain();
        var arrange = new NativeMenuItem(Loc.T("Host.Tray.Arrange"));
        arrange.Click += (_, _) => ToggleLayout();
        var quit = new NativeMenuItem(Loc.T("Host.Tray.Quit"));
        quit.Click += (_, _) => Quit();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Hearthpulse.UI/Assets/hearthpulse.ico"))),
            ToolTipText = "Hearthpulse",
            Menu = new NativeMenu { open, _overlayItem, arrange, new NativeMenuItemSeparator(), quit },
        };
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, [tray]);

        // A language switch retitles the menu (the event fires on the UI thread).
        _trayTexts.AddRange([(open, "Host.Tray.Open"), (_overlayItem, "Host.Tray.Overlay"), (arrange, "Host.Tray.Arrange"), (quit, "Host.Tray.Quit")]);
        Loc.Instance.LanguageChanged += SetTrayTexts;
    }

    private void SetTrayTexts()
    {
        foreach (var (item, key) in _trayTexts)
        {
            item.Header = Loc.T(key);
        }
    }

    private void ShowMain()
    {
        if (_host is null)
        {
            return;
        }

        _releaseMain?.Stop();
        var built = _main is null;
        var main = _mainLife.Open(CreateMain);
        _mainLife.Shown(main);
        main.Show();
        if (built && _mainLife.Place is { Maximized: true })
        {
            main.WindowState = WindowState.Maximized;
        }
        else if (!built && main.WindowState == WindowState.Minimized)
        {
            main.WindowState = WindowState.Normal;
        }

        main.Activate();
        SyncWindowInUse();
    }

    /// <summary>A collection asked for by a meta load waits while the dashboard is on screen or waiting in the tray.</summary>
    private void SyncWindowInUse()
    {
        if (_host is not null)
        {
            _host.Session.MainWindowInUse = _main is { WindowState: not WindowState.Minimized };
        }
    }

    /// <summary>A new dashboard, on the page, tabs and bounds of <paramref name="place"/> when given.</summary>
    private MainWindow CreateMain(MainPlace? place)
    {
        var main = new MainWindow(_host!);
        main.HiddenToTray += () =>
        {
            // No collection of its own: the one after the release takes the pages and the rest.
            if (ReferenceEquals(main, _main))
            {
                _mainLife.Hidden(main, DateTime.UtcNow);
                _releaseMain?.Start();
            }
        };
        main.Closing += (_, e) =>
        {
            if (!e.Cancel)
            {
                _mainLife.Closing(main);
            }
        };
        main.Closed += (_, _) => OnMainClosed(main);
        main.PropertyChanged += (_, e) =>
        {
            if (e.Property != Window.WindowStateProperty || !ReferenceEquals(main, _main))
            {
                return;
            }

            SyncWindowInUse();
            // Building and leaving pages leaves garbage an idle app would never collect.
            if (main.WindowState == WindowState.Minimized)
            {
                _host!.Session.RequestMemoryTrim();
            }
        };
        if (place is not null)
        {
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Position = place.Position;
            main.Width = place.Width;
            main.Height = place.Height;
            main.View.ShowPage(place.Page);
            main.View.BattlegroundsTab = place.BattlegroundsTab;
            main.View.ConstructedTab = place.ConstructedTab;
        }

        return main;
    }

    /// <summary>
    /// Closed for good (no tray to hide it in): forgotten, so the tray's Open builds a new one instead of showing a closed
    /// window (which threw), and its view lets go of the view model, so the window and its pages are garbage now.
    /// </summary>
    private void OnMainClosed(MainWindow main)
    {
        if (!ReferenceEquals(main, _main))
        {
            return;
        }

        _releaseMain?.Stop();
        _mainLife.Closed(main);
        main.View.DataContext = null;
        SyncWindowInUse();
        _host?.Session.RequestMemoryTrim();
    }

    /// <summary>
    /// The dashboard stayed hidden in the tray for a minute, and no game is being played: it is let go with every page it
    /// built, and built again on the same page when opened.
    /// </summary>
    private void ReleaseMain()
    {
        if (!_mainLife.Waiting)
        {
            _releaseMain?.Stop();
            return;
        }

        if (!_mainLife.ReleaseDue(DateTime.UtcNow, _host?.Main.InGame == true) || _main is not { IsVisible: false })
        {
            return;
        }

        _releaseMain?.Stop();
        var main = _mainLife.Release()!;
        main.CloseForReal();
        Diag.Info("Main window let go while hidden in the tray");
        SyncWindowInUse();
        _host?.Session.RequestMemoryTrim();
    }

    // The normal bounds, not those of a maximized window or the parking spot of a minimized one.
    private static MainPlace PlaceOf(MainWindow main) =>
        new(main.View.CurrentPage, main.View.BattlegroundsTab, main.View.ConstructedTab, main.RestoreBounds.Position,
            main.RestoreBounds.Size.Width, main.RestoreBounds.Size.Height, main.RestoreBounds.Maximized);

    /// <summary>Where a dashboard is: rebuilt (back from the tray) on the same page, tabs and normal bounds.</summary>
    private sealed record MainPlace(string Page, string BattlegroundsTab, string ConstructedTab, Avalonia.PixelPoint Position, double Width, double Height, bool Maximized);

    private void ToggleSuppressed()
    {
        if (_overlay is not null)
        {
            _overlay.Suppressed = !_overlay.Suppressed;
        }
    }

    private void ToggleLayout()
    {
        if (_host is not null)
        {
            _host.Overlay.LayoutEditing = !_host.Overlay.LayoutEditing;
        }
    }

    private void Quit()
    {
        _releaseMain?.Stop();
        if (_mainLife.Release() is { } main)
        {
            main.AllowClose = true;
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void Shutdown()
    {
        Loc.Instance.LanguageChanged -= SetTrayTexts;
        _pipeCts?.Cancel();
        _hotkeys?.Dispose();
        _platform?.Dispose();
        _host?.Dispose();
        _instance?.Dispose();
    }

    /// <summary>A second launch connects here so the running copy shows its window.</summary>
    private void ListenForActivation()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(ActivatePipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    Dispatcher.UIThread.Post(ShowMain);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (IOException ex)
                {
                    Diag.Error("Activation pipe failed", ex);
                    await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                }
            }
        }, token).Forget("Activation pipe");
    }

    private static void AskRunningInstanceToShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivatePipe, PipeDirection.Out);
            client.Connect(1500);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Diag.Info("Hearthpulse is already running.");
        }
    }
}
