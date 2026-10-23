using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Hearthlens.UI;

namespace Hearthlens.Desktop;

public partial class MainWindow : Window
{
    private readonly AppHost? _host;
    private WindowNotificationManager? _notifications;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(AppHost host) : this()
    {
        _host = host;
        View.DataContext = host.Main;
        View.SettingsChanged += host.OnSettingsChanged;
        View.ReimportRequested += () => _ = host.ImportLogsAsync(force: true);
        View.ArrangeOverlayRequested += () => host.Overlay.LayoutEditing = !host.Overlay.LayoutEditing;
    }

    /// <summary>When false, closing only hides the window (the app keeps running in the tray).</summary>
    public bool AllowClose { get; set; }

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
        }

        base.OnClosing(e);
    }
}
