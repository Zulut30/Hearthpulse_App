using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Hearthlens.App.Infrastructure;
using Hearthlens.Core;

namespace Hearthlens.App.Overlay;

/// <summary>Marks overlay elements that should receive the mouse; everything else clicks through to the game.</summary>
public static class OverlayHit
{
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.RegisterAttached(
        "Interactive", typeof(bool), typeof(OverlayHit), new PropertyMetadata(false));

    public static bool GetInteractive(DependencyObject d) => (bool)d.GetValue(InteractiveProperty);

    public static void SetInteractive(DependencyObject d, bool value) => d.SetValue(InteractiveProperty, value);
}

/// <summary>
/// Transparent, click-through, topmost window glued to Hearthstone's client area. Content is laid
/// out on a 1080-unit-high reference surface and scaled with the game window.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double ReferenceHeight = 1080;

    private readonly AppSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _toastTimer;
    private IntPtr _hwnd;
    private IntPtr _hearthstone;
    private DateTime _nextWindowLookup;
    private DateTime _nextTopmost;
    private Native.RECT _lastRect;
    private bool _clickThrough = true;
    private bool _shown;
    private double _scale = 1;
    private double _dpi = 1;
    private string _guideKey = string.Empty;
    private readonly List<MovablePanel> _panels = new();
    private Size _layoutSize;
    private int _hoverReadBusy;
    private DateTime _nextHoverRead;
    private volatile string? _memoryHover;

    public OverlayWindow(OverlayViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Tick();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ViewModel.ShowToast = false;
        };
        viewModel.ResetLayoutRequested += ResetLayout;
        Loaded += (_, _) =>
        {
            foreach (var panel in Surface.Children.OfType<MovablePanel>())
            {
                _panels.Add(panel);
                panel.PlacementChanged += OnPlacementChanged;
            }
        };
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    public OverlayViewModel ViewModel { get; }

    /// <summary>Raised when the player moved, resized or reset panels; settings should be saved.</summary>
    public event Action? LayoutChanged;

    /// <summary>Temporarily hides the overlay (hotkey) without changing settings.</summary>
    public bool Suppressed { get; set; }

    /// <summary>Off-screen render mode: never follow or cover the game window.</summary>
    public bool Offscreen { get; set; }

    /// <summary>Puts the overlay back on top and forces the next tick to reposition it onto the game window.</summary>
    public void ReassertTopmost()
    {
        if (!IsLoaded || Offscreen)
        {
            return;
        }

        _lastRect = default;
        _nextTopmost = DateTime.MinValue;
        _timer.Start();
    }

    /// <summary>
    /// Reads the hero card id under the mouse on the Battlegrounds leaderboard from the game (null when none).
    /// Called off the UI thread; when it yields nothing the overlay falls back to leaderboard geometry.
    /// </summary>
    public Func<string?>? HoveredHero { get; set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExStyle(_hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT, 0);
        if (!Offscreen)
        {
            _timer.Start();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _toastTimer.Stop();
        // A theme change replaces this window; the view model lives on.
        ViewModel.ResetLayoutRequested -= ResetLayout;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        base.OnClosed(e);
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.ShowToast) && ViewModel.ShowToast)
        {
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }

    private void Tick()
    {
        var now = DateTime.UtcNow;
        if (now >= _nextWindowLookup || !Native.IsWindow(_hearthstone))
        {
            _nextWindowLookup = now.AddSeconds(1.5);
            _hearthstone = Native.FindHearthstoneWindow();
        }

        var rect = Native.ClientRectOnScreen(_hearthstone);
        var foreground = Native.GetForegroundWindow();
        // Layout mode stays up while the player arranges panels from the dashboard as well.
        var visible = rect is not null && !Suppressed
                      && (ViewModel.LayoutEditing || (_settings.OverlayEnabled && (foreground == _hearthstone || foreground == _hwnd)));
        if (!visible)
        {
            if (_shown)
            {
                Root.Visibility = Visibility.Hidden;
                _shown = false;
                ViewModel.HoverLeaderboard(0, 0, 0);
            }

            return;
        }

        var r = rect!.Value;
        if (!_shown)
        {
            Root.Visibility = Visibility.Visible;
            _shown = true;
            _lastRect = default; // reposition even if the game window did not move
        }

        if (!r.Equals(_lastRect))
        {
            _lastRect = r;
            _nextTopmost = now.AddSeconds(3);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
        else if (now >= _nextTopmost)
        {
            // Another topmost window (a media overlay, an anti-cheat, the game itself) can take the top of the
            // Z-order; re-assert it so the overlay cannot silently disappear behind the game.
            _nextTopmost = now.AddSeconds(3);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var heightDip = r.Height / _dpi;
        var widthDip = r.Width / _dpi;
        _scale = heightDip / ReferenceHeight * Math.Clamp(_settings.OverlayScale, 0.6, 1.6);
        if (Math.Abs(SurfaceScale.ScaleX - _scale) > 0.0005)
        {
            SurfaceScale.ScaleX = _scale;
            SurfaceScale.ScaleY = _scale;
        }

        Surface.Width = widthDip / _scale;
        Surface.Height = heightDip / _scale;
        var size = new Size(Surface.Width, Surface.Height);
        if (size != _layoutSize)
        {
            _layoutSize = size;
            ApplyLayout();
        }

        Surface.Opacity = Math.Clamp(_settings.OverlayOpacity, 0.3, 1);

        Native.GetCursorPos(out var cursor);
        UpdateClickThrough(cursor, r);
        UpdateLeaderboardHover(cursor, r);
        UpdateGuides(r);
    }

    private void UpdateClickThrough(Native.POINT cursor, Native.RECT r)
    {
        var interactive = Mouse.Captured is not null;
        if (!interactive && cursor.X >= r.Left && cursor.X < r.Right && cursor.Y >= r.Top && cursor.Y < r.Bottom)
        {
            var point = new Point((cursor.X - r.Left) / _dpi, (cursor.Y - r.Top) / _dpi);
            var hit = Root.InputHitTest(point) as DependencyObject;
            for (var node = hit; node is not null; node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (OverlayHit.GetInteractive(node))
                {
                    interactive = node is UIElement { IsVisible: true };
                    break;
                }
            }
        }

        if (interactive == !_clickThrough)
        {
            return;
        }

        _clickThrough = !interactive;
        if (interactive)
        {
            Native.SetExStyle(_hwnd, 0, Native.WS_EX_TRANSPARENT);
        }
        else
        {
            Native.SetExStyle(_hwnd, Native.WS_EX_TRANSPARENT, 0);
        }
    }

    private void UpdateLeaderboardHover(Native.POINT cursor, Native.RECT r)
    {
        if (!ViewModel.ShowBattlegrounds || !_settings.ShowOpponentBoards || ViewModel.LayoutEditing)
        {
            _memoryHover = null;
            ViewModel.HoverLeaderboard(0, 0, 0);
            return;
        }

        PollHoveredHero();

        // Work in overlay units: the surface is Surface.Width x Surface.Height and scales with the client height.
        var x = (cursor.X - r.Left) / _dpi / _scale;
        var y = (cursor.Y - r.Top) / _dpi / _scale;
        var units = Surface.Height;
        var left = LeaderboardLeft() + ((_settings.LeaderboardWidth + 0.012) * units);
        var top = Math.Clamp(y - 40, 8, Math.Max(8, units - 240));

        if (_memoryHover is { } hero)
        {
            ViewModel.HoverLeaderboardHero(hero, top, left);
            return;
        }

        var slot = LeaderboardSlotAt(x, y);
        ViewModel.HoverLeaderboard(slot, top, left);
    }

    /// <summary>Starts a background read of the hovered leaderboard hero, at most ~10 times a second.</summary>
    private void PollHoveredHero()
    {
        var provider = HoveredHero;
        var now = DateTime.UtcNow;
        if (provider is null || now < _nextHoverRead || Interlocked.Exchange(ref _hoverReadBusy, 1) == 1)
        {
            return;
        }

        _nextHoverRead = now.AddMilliseconds(90);
        Task.Run(() =>
        {
            try
            {
                _memoryHover = provider();
            }
            catch (Exception ex)
            {
                _memoryHover = null;
                Diag.Error("leaderboard hover read failed", ex);
            }
            finally
            {
                Volatile.Write(ref _hoverReadBusy, 0);
            }
        });
    }

    /// <summary>
    /// Left edge of the leaderboard in overlay units. Hearthstone draws it at the edge of the centred 4:3 area,
    /// so on wide screens it sits well right of the window edge.
    /// </summary>
    private double LeaderboardLeft() => Math.Max(0, (Surface.Width - (Surface.Height * 4.0 / 3.0)) / 2);

    /// <summary>Top and height of leaderboard tile <paramref name="index"/> (0..7) in overlay units.</summary>
    private (double Top, double Height) LeaderboardTile(int index)
    {
        var units = Surface.Height;
        var slot = _settings.LeaderboardSlotHeight * units;
        var top = _settings.LeaderboardTop * units;
        if (ViewModel.IsDuos)
        {
            // Duos: teams of two tiles with a gap between teams (HDT: 13.7% of the column is spacing).
            const double spacingShare = 0.137;
            var tile = slot * (1 - spacingShare);
            var spacing = slot * 8 * spacingShare / 3;
            return (top + (tile * index) + (spacing * (index / 2)), tile);
        }

        return (top + (slot * index), slot);
    }

    /// <summary>Leaderboard place (1..8) under the given overlay point, or 0.</summary>
    private int LeaderboardSlotAt(double x, double y)
    {
        var left = LeaderboardLeft();
        var width = _settings.LeaderboardWidth * Surface.Height;
        if (x < left - 8 || x > left + width)
        {
            return 0;
        }

        for (var i = 0; i < 8; i++)
        {
            var (top, height) = LeaderboardTile(i);
            if (y >= top && y < top + height)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void ApplyLayout()
    {
        foreach (var panel in _panels)
        {
            panel.Apply(_settings.Panels.GetValueOrDefault(panel.Id), _layoutSize);
        }
    }

    private void OnPlacementChanged(MovablePanel panel, PanelPlacement? placement)
    {
        if (placement is null)
        {
            _settings.Panels.Remove(panel.Id);
        }
        else
        {
            _settings.Panels[panel.Id] = placement;
        }

        panel.Apply(placement, _layoutSize);
        LayoutChanged?.Invoke();
    }

    private void ResetLayout()
    {
        _settings.Panels.Clear();
        ApplyLayout();
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// Renders the overlay off-screen to a PNG (developer check of the layout; nothing is shown on screen).
    /// </summary>
    public async Task RenderToFileAsync(string path, int width, int height, int hoverSlot)
    {
        _timer.Stop();
        Left = -20000;
        Top = -20000;
        Width = width;
        Height = height;
        Root.Visibility = Visibility.Visible;
        _scale = height / ReferenceHeight * Math.Clamp(_settings.OverlayScale, 0.6, 1.6);
        SurfaceScale.ScaleX = SurfaceScale.ScaleY = _scale;
        Surface.Width = width / _scale;
        Surface.Height = height / _scale;
        _layoutSize = new Size(Surface.Width, Surface.Height);
        ApplyLayout();
        if (hoverSlot > 0)
        {
            var (top, _) = LeaderboardTile(hoverSlot - 1);
            ViewModel.HoverLeaderboard(hoverSlot, Math.Max(8, top - 20), LeaderboardLeft() + ((_settings.LeaderboardWidth + 0.012) * Surface.Height));
        }

        // Give card art time to download and decode.
        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(500);
            UpdateLayout();
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new RadialGradientBrush(Color.FromRgb(0x4A, 0x33, 0x1E), Color.FromRgb(0x14, 0x0D, 0x08)) { RadiusX = 0.8, RadiusY = 0.8 },
                null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(Root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private void UpdateGuides(Native.RECT r)
    {
        var key = _settings.ShowLeaderboardGuides
            ? $"{r.Width}x{r.Height}:{_settings.LeaderboardTop}:{_settings.LeaderboardSlotHeight}:{_settings.LeaderboardWidth}:{_scale}:{ViewModel.IsDuos}"
            : string.Empty;
        if (key == _guideKey)
        {
            return;
        }

        _guideKey = key;
        GuideLayer.Children.Clear();
        if (key.Length == 0)
        {
            return;
        }

        var left = LeaderboardLeft();
        for (var i = 0; i < 8; i++)
        {
            var (top, height) = LeaderboardTile(i);
            var box = new Rectangle
            {
                Width = _settings.LeaderboardWidth * Surface.Height,
                Height = Math.Max(4, height - 2),
                Stroke = (Brush)FindResource("Accent"),
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
                Fill = (Brush)FindResource("AccentSoft"),
            };
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top + 1);
            GuideLayer.Children.Add(box);
        }
    }
}
