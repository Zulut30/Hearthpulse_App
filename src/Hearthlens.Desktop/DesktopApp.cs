using System.IO.Pipes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using Hearthlens.Core;
using Hearthlens.Desktop.Platform;
using Hearthlens.UI;

namespace Hearthlens.Desktop;

/// <summary>Desktop shell: dashboard window, overlay window, tray icon, hotkeys and autostart.</summary>
internal sealed class DesktopApp : HearthlensApp
{
    private const string ActivatePipe = "Hearthlens.Activate";

    private SingleInstance? _instance;
    private AppHost? _host;
    private MainWindow? _main;
    private OverlayWindow? _overlay;
    private IOverlayPlatform? _platform;
    private NativeMenuItem? _overlayItem;
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

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Diag.Error("UI error", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diag.Error("Background error", e.Exception);
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
        _ = _host.InitializeAsync();
    }

    private void SetupTray()
    {
        _overlayItem = new NativeMenuItem("Overlay")
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

        var open = new NativeMenuItem("Open Hearthlens");
        open.Click += (_, _) => ShowMain();
        var arrange = new NativeMenuItem("Arrange overlay panels");
        arrange.Click += (_, _) => ToggleLayout();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Hearthlens.UI/Assets/hearthlens.ico"))),
            ToolTipText = "Hearthlens",
            Menu = new NativeMenu { open, _overlayItem, arrange, new NativeMenuItemSeparator(), quit },
        };
        tray.Clicked += (_, _) => ShowMain();
        TrayIcon.SetIcons(this, [tray]);
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
        _ = Task.Run(async () =>
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
        }, token);
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
            Diag.Info("Hearthlens is already running.");
        }
    }
}
