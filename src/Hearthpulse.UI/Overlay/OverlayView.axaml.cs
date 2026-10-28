using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The overlay surface. Content is laid out on a 1080-unit-high reference surface and scaled to the game's client
/// area; the hosting window (or Android overlay service) tells it the size and where the pointer is.
/// </summary>
public partial class OverlayView : UserControl
{
    public const double ReferenceHeight = 1080;

    /// <summary>Ctrl+Shift+L / Ctrl+Shift+H exist only where the host registers them (Windows), for the layout banner.</summary>
    public static bool GlobalShortcuts => OperatingSystem.IsWindows();

    private readonly List<MovablePanel> _panels = new();
    private readonly DispatcherTimer _toastTimer;
    private OverlayViewModel? _viewModel;
    private Size _layoutSize;
    private double _scale = 1;
    private string _guideKey = string.Empty;

    public OverlayView()
    {
        InitializeComponent();
        // The deck row flash (OverlayLook.axaml) runs only when the system allows animations, like the panel fades.
        Classes.Set("motion", PoolInput.AnimationsOn());
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
            panel.LookChanged += OnLookChanged;
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

    /// <summary>Hides the card hover preview (the overlay went click-through, so no pointer-exit will come).</summary>
    public void HidePreview() => PreviewHost.Hide();

    /// <summary>Shows the hover preview for the first control that passes <paramref name="filter"/> (off-screen renders).</summary>
    public bool ShowPreviewFor(Func<Control, bool> filter) => PreviewHost.ShowFirst(this, filter);

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
    /// Ctrl held over a panel during a game arms that panel (the topmost one under the pointer, view coordinates; null
    /// when outside): it takes the mouse and can be dragged or resized as in layout mode, and the placement is saved
    /// the same way. Without Ctrl, or over empty space, clicks keep going to the game. A panel being dragged stays armed
    /// until the button comes up.
    /// </summary>
    /// <returns>Whether a panel is under the pointer with Ctrl held, so the overlay must take the mouse.</returns>
    public bool UpdateDragArm(Point? pointer, bool controlDown)
    {
        var editing = _viewModel?.LayoutEditing == true;
        MovablePanel? target = null;
        if (pointer is { } p && controlDown && !editing)
        {
            // Later panels draw over earlier ones.
            for (var i = _panels.Count - 1; i >= 0 && target is null; i--)
            {
                var panel = _panels[i];
                // An armed panel also owns the strip above it where its name tag and buttons show.
                var area = panel.IsDragArmed ? panel.ArmedArea() : new Rect(panel.Bounds.Size);
                if (panel is { Shown: true, IsVisible: true, Bounds.Width: > 0 } && this.TranslatePoint(p, panel) is { } local
                    && area.Contains(local))
                {
                    target = panel;
                }
            }
        }

        foreach (var panel in _panels)
        {
            var armed = panel == target || (panel.IsDragging && !editing);
            if (panel.IsDragArmed != armed)
            {
                panel.IsDragArmed = armed;
            }
        }

        return target is not null;
    }

    /// <summary>
    /// Updates the opponent-board popup from the pointer position (view coordinates, null when outside) and the hero the
    /// game reports under the mouse (null when unknown: then the leaderboard calibration decides).
    /// </summary>
    /// <summary>
    /// The card pool panel lets clicks through on every desktop: the host passes the pointer (view coordinates, null
    /// when outside) and the mouse buttons it reads globally, for hover and right-click on the panel.
    /// </summary>
    public void UpdateCardPoolPointer(Point? pointer, (bool Left, bool Right) buttons) =>
        (CardPoolPopup.IsEffectivelyVisible ? CardPoolPopup : CardPool).UpdatePointer(this, pointer, buttons);

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

    /// <summary>
    /// Whether the game should be asked which leaderboard hero is under <paramref name="pointer"/>: Battlegrounds with
    /// opponent boards on, and the pointer in the left part of the window where Hearthstone draws the leaderboard
    /// (generous: the hero is read from the game precisely because the tile boxes may not be lined up).
    /// </summary>
    public bool WantsLeaderboardHero(Point? pointer)
    {
        if (_viewModel is not { } vm || pointer is not { } p || !vm.ShowBattlegrounds || !Settings.ShowOpponentBoards || vm.LayoutEditing)
        {
            return false;
        }

        var units = Surface.Height;
        var x = p.X / _scale;
        return x >= 0 && x <= LeaderboardLeft() + Math.Max(Settings.LeaderboardWidth * units * 2, units * 0.2);
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
            panel.Apply(Settings.Panels.GetValueOrDefault(panel.Id), _layoutSize, Math.Clamp(Settings.OverlayScale, 0.6, 1.6));
            panel.ApplyLook(Settings.PanelLooks.GetValueOrDefault(panel.Id));
        }
    }

    private void OnLookChanged(MovablePanel panel, PanelLook? look)
    {
        if (look is null)
        {
            Settings.PanelLooks.Remove(panel.Id);
        }
        else
        {
            Settings.PanelLooks[panel.Id] = look;
        }

        LayoutChanged?.Invoke();
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

        panel.Apply(placement, _layoutSize, Math.Clamp(Settings.OverlayScale, 0.6, 1.6));
        LayoutChanged?.Invoke();
    }

    private void ResetLayout()
    {
        Settings.Panels.Clear();
        Settings.PanelLooks.Clear();
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
}
