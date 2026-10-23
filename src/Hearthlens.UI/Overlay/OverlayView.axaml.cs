using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthlens.Core;
using Hearthlens.UI.Infrastructure;

namespace Hearthlens.UI.Overlay;

/// <summary>
/// The overlay surface. Content is laid out on a 1080-unit-high reference surface and scaled to the game's client
/// area; the hosting window (or Android overlay service) tells it the size and where the pointer is.
/// </summary>
public partial class OverlayView : UserControl
{
    public const double ReferenceHeight = 1080;

    private readonly List<MovablePanel> _panels = new();
    private readonly DispatcherTimer _toastTimer;
    private OverlayViewModel? _viewModel;
    private Size _layoutSize;
    private double _scale = 1;
    private string _guideKey = string.Empty;

    public OverlayView()
    {
        InitializeComponent();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (_viewModel is not null)
            {
                _viewModel.ShowToast = false;
            }
        };

        foreach (var panel in Surface.Children.OfType<MovablePanel>())
        {
            _panels.Add(panel);
            panel.PlacementChanged += OnPlacementChanged;
        }
    }

    /// <summary>Settings that hold panel placements, scale, opacity and leaderboard calibration.</summary>
    public AppSettings Settings { get; set; } = new();

    /// <summary>Raised when the player moved, resized or reset panels; settings should be saved.</summary>
    public event Action? LayoutChanged;

    /// <summary>Surface size in overlay units.</summary>
    public Size SurfaceSize => _layoutSize;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.ResetLayoutRequested -= ResetLayout;
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as OverlayViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ResetLayoutRequested += ResetLayout;
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
    }

    /// <summary>Scales the surface to a client area of <paramref name="width"/> x <paramref name="height"/> device-independent pixels.</summary>
    public void Fit(double width, double height)
    {
        if (width < 1 || height < 1)
        {
            return;
        }

        var scale = height / ReferenceHeight * Math.Clamp(Settings.OverlayScale, 0.6, 1.6);
        if (Math.Abs(scale - _scale) > 0.0005 || SurfaceHost.LayoutTransform is null)
        {
            _scale = scale;
            SurfaceHost.LayoutTransform = new ScaleTransform(scale, scale);
        }

        Surface.Width = width / _scale;
        Surface.Height = height / _scale;
        var size = new Size(Surface.Width, Surface.Height);
        if (size != _layoutSize)
        {
            _layoutSize = size;
            ApplyLayout();
        }

        Surface.Opacity = Math.Clamp(Settings.OverlayOpacity, 0.3, 1);
        UpdateGuides();
    }

    /// <summary>True when the point (in this view's coordinates) is over a panel that takes the mouse.</summary>
    public bool IsInteractiveAt(Point point)
    {
        if (point.X < 0 || point.Y < 0 || point.X > Bounds.Width || point.Y > Bounds.Height)
        {
            return false;
        }

        for (Visual? node = this.InputHitTest(point) as Visual; node is not null && node != this; node = node.GetVisualParent())
        {
            if (OverlayHit.GetInteractive(node))
            {
                return node.IsEffectivelyVisible;
            }
        }

        return false;
    }

    /// <summary>
    /// Updates the opponent-board popup from the pointer position (view coordinates, null when outside) and the hero the
    /// game reports under the mouse (null when unknown: then the leaderboard calibration decides).
    /// </summary>
    public void UpdateLeaderboardHover(Point? pointer, string? hoveredHero)
    {
        if (_viewModel is not { } vm)
        {
            return;
        }

        if (pointer is not { } p || !vm.ShowBattlegrounds || !Settings.ShowOpponentBoards || vm.LayoutEditing)
        {
            vm.HoverLeaderboard(0, 0, 0);
            return;
        }

        // Work in overlay units: the surface scales with the client height.
        var x = p.X / _scale;
        var y = p.Y / _scale;
        var units = Surface.Height;
        var left = LeaderboardLeft() + ((Settings.LeaderboardWidth + 0.012) * units);
        var top = Math.Clamp(y - 40, 8, Math.Max(8, units - 240));
        if (hoveredHero is not null)
        {
            vm.HoverLeaderboardHero(hoveredHero, top, left);
            return;
        }

        vm.HoverLeaderboard(LeaderboardSlotAt(x, y), top, left);
    }

    /// <summary>Shows the popup for one leaderboard place (off-screen rendering).</summary>
    public void ShowLeaderboardSlot(int slot)
    {
        if (_viewModel is null || slot <= 0)
        {
            return;
        }

        var (top, _) = LeaderboardTile(slot - 1);
        _viewModel.HoverLeaderboard(slot, Math.Max(8, top - 20), LeaderboardLeft() + ((Settings.LeaderboardWidth + 0.012) * Surface.Height));
    }

    /// <summary>
    /// Left edge of the leaderboard in overlay units. Hearthstone draws it at the edge of the centred 4:3 area, so on
    /// wide screens it sits well right of the window edge.
    /// </summary>
    private double LeaderboardLeft() => Math.Max(0, (Surface.Width - (Surface.Height * 4.0 / 3.0)) / 2);

    /// <summary>Top and height of leaderboard tile <paramref name="index"/> (0..7) in overlay units.</summary>
    private (double Top, double Height) LeaderboardTile(int index)
    {
        var units = Surface.Height;
        var slot = Settings.LeaderboardSlotHeight * units;
        var top = Settings.LeaderboardTop * units;
        if (_viewModel?.IsDuos == true)
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
        var width = Settings.LeaderboardWidth * Surface.Height;
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

    public void ApplyLayout()
    {
        foreach (var panel in _panels)
        {
            panel.Apply(Settings.Panels.GetValueOrDefault(panel.Id), _layoutSize);
        }
    }

    private void OnPlacementChanged(MovablePanel panel, PanelPlacement? placement)
    {
        if (placement is null)
        {
            Settings.Panels.Remove(panel.Id);
        }
        else
        {
            Settings.Panels[panel.Id] = placement;
        }

        panel.Apply(placement, _layoutSize);
        LayoutChanged?.Invoke();
    }

    private void ResetLayout()
    {
        Settings.Panels.Clear();
        ApplyLayout();
        LayoutChanged?.Invoke();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.ShowToast) && _viewModel?.ShowToast == true)
        {
            _toastTimer.Stop();
            _toastTimer.Start();
        }
    }

    private void UpdateGuides()
    {
        var key = Settings.ShowLeaderboardGuides
            ? $"{Surface.Width:0}x{Surface.Height:0}:{Settings.LeaderboardTop}:{Settings.LeaderboardSlotHeight}:{Settings.LeaderboardWidth}:{_viewModel?.IsDuos}"
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
                Width = Settings.LeaderboardWidth * Surface.Height,
                Height = Math.Max(4, height - 2),
                Stroke = ThemeBrushes.Get("Accent"),
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
                Fill = ThemeBrushes.Get("AccentSoft"),
            };
            Canvas.SetLeft(box, left);
            Canvas.SetTop(box, top + 1);
            GuideLayer.Children.Add(box);
        }
    }

    private void OnTierClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TierButton tier })
        {
            _viewModel?.SelectTier.Execute(tier.Tier);
        }
    }

    private void OnAlternativeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CompRow comp })
        {
            _viewModel?.Pin.Execute(comp.Id);
        }
    }
}
