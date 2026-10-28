using System.IO.Pipes;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Desktop.Platform;
using Hearthpulse.UI;

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
    private MainWindow? _main;
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
        _main = new MainWindow(_host);
        _host.Notification += (title, text) => _main.Notify(title, text);
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

        if (!settings.StartMinimized)
        {
            _main.Show();
        }

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
        _main!.AllowClose = true;
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
            _main!.View.SyncOverlaySwitch();
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
        if (_main is null)
        {
            return;
        }

        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

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
        if (_main is not null)
        {
            _main.AllowClose = true;
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
