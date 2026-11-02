using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// One OBS capture window ("Hearthpulse — Deck", "— Lobby", "— Combat odds"; docs/streaming.md): the overlay's panel
/// control on <see cref="OverlayViewModel"/>, so it shows exactly what the overlay shows. <see cref="StreamWindows"/> opens,
/// places and closes it.
/// </summary>
public partial class StreamWindow : Window
{
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private readonly OverlayViewModel _model;
    private readonly AppSettings _settings;
    private bool _closingForReal;

    public StreamWindow(OverlayViewModel model, AppSettings settings, StreamWindowSpec spec)
    {
        InitializeComponent();
        _model = model;
        _settings = settings;
        Spec = spec;
        DataContext = model;
        MinWidth = spec.MinWidth;
        MinHeight = spec.MinHeight;
        PanelHost.Content = spec.Id switch
        {
            "deck" => new DeckPanel(),
            "lobby" => new LobbyPanel(),
            _ => new OddsPanel(),
        };
        SetTitle();
        ApplyLook();
        ApplyState();
        Loc.Instance.LanguageChanged += SetTitle;
        model.PropertyChanged += OnModelChanged;
        SourceInitialized += (_, _) => Attach();
    }

    public StreamWindowSpec Spec { get; }

    /// <summary>The player closed the window (its menu, Alt+F4, the taskbar): the host switches it off.</summary>
    public event Action<StreamWindowSpec>? CloseRequested;

    /// <summary>The window was moved or resized (once, at the end): the host saves its place.</summary>
    public event Action<StreamWindowSpec>? Placed;

    /// <summary>"Always on top" from the window's menu: the host flips the setting for all three.</summary>
    public event Action? OnTopToggled;

    /// <summary>The bounds to save; null while minimized or maximized (those are not a place).</summary>
    public StreamRect? Place => WindowState == WindowState.Normal ? new StreamRect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height) : null;

    /// <summary>Closes the window without switching it off (the theme rebuilds it, the host switched it off, the app quits).</summary>
    public void CloseForReal()
    {
        _closingForReal = true;
        if (!IsClosed)
        {
            Close();
        }
    }

    /// <summary>The window has closed (it cannot be shown again).</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Background (theme or key colour) and "on top", from the settings; at once, without reopening.</summary>
    public void ApplyLook()
    {
        Background = StreamBackdrop.KeyColor(_settings.StreamWindowBackground) is { } key
            ? Frozen(Color.FromRgb(key.R, key.G, key.B))
            : (Brush)FindResource("Bg");
        Topmost = _settings.StreamWindowsOnTop;
        OnTopItem.IsChecked = _settings.StreamWindowsOnTop;
        OnTopCheck.Visibility = _settings.StreamWindowsOnTop ? Visibility.Visible : Visibility.Hidden;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// <c>--render-ui</c>: the window at <paramref name="width"/> × <paramref name="height"/>, off screen and out of the
    /// taskbar, written as OBS would capture it (background and content), after the card art had time to load.
    /// </summary>
    public async Task RenderToFileAsync(string path, int width, int height)
    {
        ShowInTaskbar = false;
        Left = -20000;
        Top = -20000;
        Width = width;
        Height = height;
        ApplyLook();
        ApplyState();
        Show();
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            UpdateLayout();
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(Root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("wrote " + path);
    }

    private void SetTitle()
    {
        Title = Spec.Title;
        AutomationProperties.SetName(this, Title);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == OverlayViewModel.StreamStateProperty(Spec.Id))
        {
            ApplyState();
        }
    }

    /// <summary>The panel, the background alone, or the quiet tile; switched at once, no fade.</summary>
    private void ApplyState()
    {
        var state = _model.StreamState(Spec.Id);
        Scaler.Visibility = state == StreamWindowState.Shown ? Visibility.Visible : Visibility.Collapsed;
        IdleTile.Visibility = state == StreamWindowState.Idle ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Attach()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // Windows 11 rounds the corners and draws a frame line; both would show in the capture. No effect on Windows 10.
        var corners = DWMWCP_DONOTROUND;
        _ = Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        var border = DWMWA_COLOR_NONE;
        _ = Native.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // A move or resize ends (dragging the window or an edge): saved once, not on every pixel.
        if (message == WM_EXITSIZEMOVE)
        {
            Placed?.Invoke(Spec);
        }

        return IntPtr.Zero;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnResetSize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Normal;
        var size = StreamWindowLayout.OriginalSize(Spec, new StreamRect(Left, Top, Width, Height));
        Width = size.Width;
        Height = size.Height;
        Placed?.Invoke(Spec);
    }

    private void OnToggleOnTop(object sender, RoutedEventArgs e) => OnTopToggled?.Invoke();

    private void OnCloseWindow(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(Spec);

    protected override void OnClosed(EventArgs e)
    {
        IsClosed = true;
        Loc.Instance.LanguageChanged -= SetTitle;
        _model.PropertyChanged -= OnModelChanged;
        base.OnClosed(e);
        if (!_closingForReal)
        {
            // Alt+F4 or the taskbar's "Close window". Asked after this message: when the app is quitting, the dispatcher
            // stops first and never runs it, so quitting keeps the window switched on for the next start.
            _ = Dispatcher.BeginInvoke(() => CloseRequested?.Invoke(Spec));
        }
    }
}
