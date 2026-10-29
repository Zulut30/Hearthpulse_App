using Hearthpulse.Core;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Hearthpulse.UI;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Desktop;

public partial class MainWindow : Window
{
    private readonly AppHost? _host;
    private WindowNotificationManager? _notifications;

    public MainWindow()
    {
        InitializeComponent();
        RestoreBounds = new WindowRestoreBounds(this);
    }

    /// <summary>The normal bounds and "maximized", for a dashboard rebuilt after the tray.</summary>
    public WindowRestoreBounds RestoreBounds { get; }

    public MainWindow(AppHost host) : this()
    {
        _host = host;
        View.DataContext = host.Main;
        View.SettingsChanged += host.OnSettingsChanged;
        View.ReimportRequested += () => host.ImportLogsAsync(force: true).Forget("Log import");
        View.ArrangeOverlayRequested += () => host.Overlay.LayoutEditing = !host.Overlay.LayoutEditing;
    }

    /// <summary>When false, closing only hides the window (the app keeps running in the tray).</summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when closing only hid the window in the tray.</summary>
    public event Action? HiddenToTray;

    /// <summary>
    /// Closes for good and lets go of the dashboard's view model, which lives on with the app: the view stops listening
    /// to it, so nothing keeps this window and its pages.
    /// </summary>
    public void CloseForReal()
    {
        AllowClose = true;
        Close();
        View.DataContext = null;
    }

    public void Notify(string title, string text)
    {
        _notifications ??= new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 3 };
        _notifications.Show(new Notification(title, text, NotificationType.Information, TimeSpan.FromSeconds(8)));
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AllowClose && _host?.Settings.MinimizeToTray == true)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        }

        base.OnClosing(e);
    }
}
