using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Desktop;

/// <summary>
/// One OBS capture window ("Hearthpulse — Deck", "— Lobby", "— Combat odds"; docs/streaming.md) around a
/// <see cref="StreamView"/> on the overlay's model. No OS frame (OBS would capture it): a press drags the window, a press
/// within 6 of an edge resizes it, everything else is in the context menu (a popup window of its own, never captured).
/// <see cref="StreamWindows"/> opens, places and closes it.
/// </summary>
internal sealed class StreamWindow : Window
{
    /// <summary>The band at each edge that resizes instead of moving.</summary>
    private const double Edge = 6;

    private readonly StreamView _view;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _placed;
    private readonly MenuItem _onTopItem;
    private bool _closingForReal;

    public StreamWindow(OverlayViewModel model, AppSettings settings, StreamWindowSpec spec)
    {
        _settings = settings;
        Spec = spec;
        WindowDecorations = WindowDecorations.None;
        ShowInTaskbar = true;
        ShowActivated = false;
        CanResize = true;
        MinWidth = spec.MinWidth;
        MinHeight = spec.MinHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Hearthpulse.UI/Assets/hearthpulse.ico")));

        _view = new StreamView { Spec = spec, DataContext = model };
        Content = _view;
        SetTitle();
        Loc.Instance.LanguageChanged += SetTitle;

        _onTopItem = new MenuItem { Header = Loc.T("Stream.OnTop"), ToggleType = MenuItemToggleType.CheckBox };
        _onTopItem.Click += (_, _) => OnTopToggled?.Invoke();
        var reset = new MenuItem { Header = Loc.T("Stream.Menu.ResetSize") };
        reset.Click += (_, _) => ResetSize();
        var close = new MenuItem { Header = Loc.T("Stream.Menu.Close") };
        close.Click += (_, _) => CloseRequested?.Invoke(Spec);
        ContextMenu = new ContextMenu { ItemsSource = new object[] { reset, _onTopItem, new Separator(), close } };
        ContextMenu.Opening += (_, _) =>
        {
            // Texts in the current language each time it opens.
            reset.Header = Loc.T("Stream.Menu.ResetSize");
            _onTopItem.Header = Loc.T("Stream.OnTop");
            close.Header = Loc.T("Stream.Menu.Close");
        };
        ApplyLook();

        // Moving and resizing raise these on every step: the place is saved once they stop.
        _placed = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _placed.Tick += (_, _) =>
        {
            _placed.Stop();
            Placed?.Invoke(Spec);
        };
        PositionChanged += (_, _) => Restart();
        Resized += (_, _) => Restart();
    }

    public StreamWindowSpec Spec { get; }

    /// <summary>The content (render scenes and tests).</summary>
    public StreamView View => _view;

    /// <summary>The player closed the window (its menu, Alt+F4, the taskbar): the host switches it off.</summary>
    public event Action<StreamWindowSpec>? CloseRequested;

    /// <summary>The window was moved or resized (once it stopped): the host saves its place.</summary>
    public event Action<StreamWindowSpec>? Placed;

    /// <summary>"Always on top" from the window's menu: the host flips the setting for all three.</summary>
    public event Action? OnTopToggled;

    /// <summary>The bounds to save, in device-independent pixels; null while minimized or maximized.</summary>
    public StreamRect? Place
    {
        get
        {
            if (WindowState != WindowState.Normal)
            {
                return null;
            }

            var scaling = DesktopScaling;
            return new StreamRect(Position.X / scaling, Position.Y / scaling, Bounds.Width > 0 ? Bounds.Width : Width, Bounds.Height > 0 ? Bounds.Height : Height);
        }
    }

    /// <summary>Puts the window at <paramref name="place"/> (device-independent pixels).</summary>
    public void MoveTo(StreamRect place, double scaling)
    {
        Position = new PixelPoint((int)Math.Round(place.X * scaling), (int)Math.Round(place.Y * scaling));
        Width = place.Width;
        Height = place.Height;
    }

    /// <summary>Background (theme or key colour) and "on top", from the settings; at once, without reopening.</summary>
    public void ApplyLook()
    {
        _view.SetBackground(_settings.StreamWindowBackground);
        Topmost = _settings.StreamWindowsOnTop;
        _onTopItem.IsChecked = _settings.StreamWindowsOnTop;
    }

    /// <summary>Closes the window without switching it off (the host switched it off, or the app quits).</summary>
    public void CloseForReal()
    {
        _closingForReal = true;
        Close();
    }

    private void SetTitle()
    {
        Title = Spec.Title;
        AutomationProperties.SetName(this, Title);
    }

    private void Restart()
    {
        _placed.Stop();
        _placed.Start();
    }

    private void ResetSize()
    {
        WindowState = WindowState.Normal;
        Width = Spec.Width;
        Height = Spec.Height;
        Placed?.Invoke(Spec);
    }

    // The edge cursors, made once: a new Cursor on every pointer move was never disposed.
    private static readonly Cursor WestEast = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor NorthSouth = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomLeft = new(StandardCursorType.BottomLeftCorner);
    private static readonly Cursor BottomRight = new(StandardCursorType.BottomRightCorner);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = EdgeAt(e.GetPosition(this)) switch
        {
            WindowEdge.West or WindowEdge.East => WestEast,
            WindowEdge.North or WindowEdge.South => NorthSouth,
            WindowEdge.NorthWest => TopLeft,
            WindowEdge.NorthEast => TopRight,
            WindowEdge.SouthWest => BottomLeft,
            WindowEdge.SouthEast => BottomRight,
            _ => Cursor.Default,
        };
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (EdgeAt(e.GetPosition(this)) is { } edge)
        {
            BeginResizeDrag(edge, e);
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

    /// <summary>The edge or corner within <see cref="Edge"/> of <paramref name="point"/>; null inside.</summary>
    private WindowEdge? EdgeAt(Point point)
    {
        var size = Bounds.Size;
        var west = point.X < Edge;
        var east = point.X > size.Width - Edge;
        var north = point.Y < Edge;
        var south = point.Y > size.Height - Edge;
        return (north, south, west, east) switch
        {
            (true, _, true, _) => WindowEdge.NorthWest,
            (true, _, _, true) => WindowEdge.NorthEast,
            (_, true, true, _) => WindowEdge.SouthWest,
            (_, true, _, true) => WindowEdge.SouthEast,
            (true, _, _, _) => WindowEdge.North,
            (_, true, _, _) => WindowEdge.South,
            (_, _, true, _) => WindowEdge.West,
            (_, _, _, true) => WindowEdge.East,
            _ => null,
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Alt+F4 or the taskbar's "Close window": switched off, which closes it. Quitting the app closes it too, but keeps
        // it switched on for the next start.
        if (!_closingForReal && !e.IsProgrammatic && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            CloseRequested?.Invoke(Spec);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _placed.Stop();
        Loc.Instance.LanguageChanged -= SetTitle;
        base.OnClosed(e);
    }
}
