using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hearthpulse.Core;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// Overlay panel host the player can drag, resize (mouse wheel) and reset (right-click) while the
/// overlay is in layout mode, and drag or resize during a game while holding Ctrl over it (<see cref="IsDragArmed"/>).
/// Without a saved placement it keeps the alignment and margin from XAML.
/// </summary>
public sealed class MovablePanel : ContentControl
{
    public static readonly DependencyProperty IdProperty = DependencyProperty.Register(nameof(Id), typeof(string), typeof(MovablePanel));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(MovablePanel));

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(nameof(Hint), typeof(string), typeof(MovablePanel),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(bool), typeof(MovablePanel), new PropertyMetadata(false, (d, _) => ((MovablePanel)d).UpdateState()));

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing), typeof(bool), typeof(MovablePanel), new PropertyMetadata(false, (d, _) => ((MovablePanel)d).UpdateState()));

    public static readonly DependencyProperty IsDragArmedProperty = DependencyProperty.Register(
        nameof(IsDragArmed), typeof(bool), typeof(MovablePanel), new PropertyMetadata(false, (d, _) => ((MovablePanel)d).UpdateState()));

    public static readonly DependencyProperty ShowPlaceholderProperty = DependencyProperty.Register(
        nameof(ShowPlaceholder), typeof(bool), typeof(MovablePanel));

    public static readonly DependencyProperty ScaleTextProperty = DependencyProperty.Register(
        nameof(ScaleText), typeof(string), typeof(MovablePanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PlaceholderAlignmentProperty = DependencyProperty.Register(
        nameof(PlaceholderAlignment), typeof(HorizontalAlignment), typeof(MovablePanel), new PropertyMetadata(HorizontalAlignment.Stretch));

    /// <summary>How far below its spot a panel starts when it fades in, in overlay units.</summary>
    private const double RevealRise = 6;

    private static readonly Duration RevealTime = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration HideTime = new(TimeSpan.FromMilliseconds(110));

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly TranslateTransform _rise = new();
    private bool _lastEditing;
    private bool _hiding;
    private HorizontalAlignment _defaultHorizontal;
    private VerticalAlignment _defaultVertical;
    private Thickness _defaultMargin;
    private bool _defaultsCaptured;
    private Point _dragStart;
    private Point _originStart;
    private bool _dragging;

    // Saved top-left in surface units (null = XAML default). Kept apart from Margin so a panel that grows
    // past the screen edge is pulled back inside, then returns to its spot when it shrinks again.
    private Point? _saved;

    // Height of the content measured without the parent's limit (see MeasureOverride), for KeepOnScreen.
    private double _contentHeight;

    public MovablePanel()
    {
        LayoutTransform = _scale;
        RenderTransform = _rise;
        Visibility = Visibility.Collapsed;
    }

    public string Id { get => (string)GetValue(IdProperty); set => SetValue(IdProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>When the panel shows, for its layout-mode placeholder (menu and draft panels); empty for match panels.</summary>
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    /// <summary>
    /// Where the layout-mode placeholder sits inside a wide panel. The pick rows (heroes, trinkets, Discover, tavern) share
    /// the game's card row and are as wide as it: stretched, their placeholders would lie on top of each other.
    /// </summary>
    public HorizontalAlignment PlaceholderAlignment
    {
        get => (HorizontalAlignment)GetValue(PlaceholderAlignmentProperty);
        set => SetValue(PlaceholderAlignmentProperty, value);
    }

    public bool Shown { get => (bool)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public bool IsEditing { get => (bool)GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    /// <summary>
    /// Set by the overlay while Ctrl is held over this panel outside layout mode: the panel takes the mouse, shows its
    /// frame and name tag, and moves or resizes like in layout mode. Without Ctrl it lets every click through as before.
    /// </summary>
    public bool IsDragArmed { get => (bool)GetValue(IsDragArmedProperty); set => SetValue(IsDragArmedProperty, value); }

    /// <summary>The player is dragging the panel right now (the overlay keeps it armed until the button is released).</summary>
    public bool IsDragging => _dragging;

    private bool CanMove => IsEditing || IsDragArmed;

    public bool ShowPlaceholder { get => (bool)GetValue(ShowPlaceholderProperty); private set => SetValue(ShowPlaceholderProperty, value); }

    /// <summary>The player's size for the layout-mode tag ("115%"); empty at the default size.</summary>
    public string ScaleText { get => (string)GetValue(ScaleTextProperty); private set => SetValue(ScaleTextProperty, value); }

    private void SetScale(double scale)
    {
        _scale.ScaleX = _scale.ScaleY = scale;
        ScaleText = PanelSnap.ScaleText(scale);
    }

    /// <summary>Raised when the player moves, resizes or resets the panel (null = reset).</summary>
    public event Action<MovablePanel, PanelPlacement?>? PlacementChanged;

    private void UpdateState()
    {
        // Entering or leaving layout mode switches every panel at once: no fades there, only for a panel the game shows.
        // Off with the Windows animation setting, like the main window's motion.
        var animate = IsEditing == _lastEditing && !IsEditing && IsLoaded && SystemParameters.ClientAreaAnimation;
        _lastEditing = IsEditing;
        SetVisible(Shown || IsEditing, animate);
        ShowPlaceholder = IsEditing && !Shown;
        OverlayHit.SetInteractive(this, CanMove || _interactiveContent);
        Cursor = CanMove ? Cursors.SizeAll : null;
    }

    /// <summary>
    /// Shows or hides the panel; with <paramref name="animate"/> a short fade and rise in, or a quicker fade out. The
    /// animations run once per change, never while the panel just sits there.
    /// </summary>
    private void SetVisible(bool visible, bool animate)
    {
        if (visible)
        {
            var appearing = Visibility != Visibility.Visible || _hiding;
            // A panel shown again mid fade-out comes back from where it was.
            var from = _hiding ? Opacity : 0;
            _hiding = false;
            IsHitTestVisible = true;
            Visibility = Visibility.Visible;
            if (animate && appearing)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(from, 1, RevealTime) { EasingFunction = new CubicEase(), FillBehavior = FillBehavior.Stop });
                _rise.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(RevealRise, 0, RevealTime) { EasingFunction = new CubicEase(), FillBehavior = FillBehavior.Stop });
            }
            else if (!animate)
            {
                StopMotion();
            }

            return;
        }

        if (Visibility != Visibility.Visible || _hiding)
        {
            return;
        }

        if (!animate)
        {
            StopMotion();
            Visibility = Visibility.Collapsed;
            return;
        }

        // The game moved on: the panel stops taking clicks at once and is gone a moment later.
        _hiding = true;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, HideTime);
        fade.Completed += (_, _) =>
        {
            if (!_hiding)
            {
                return;
            }

            _hiding = false;
            StopMotion();
            IsHitTestVisible = true;
            Visibility = Visibility.Collapsed;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void StopMotion()
    {
        BeginAnimation(OpacityProperty, null);
        _rise.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private bool _interactiveContent;

    /// <summary>Whether the content has its own buttons and should receive the mouse outside layout mode.</summary>
    public bool InteractiveContent
    {
        get => _interactiveContent;
        set
        {
            _interactiveContent = value;
            UpdateState();
        }
    }

    private void CaptureDefaults()
    {
        if (_defaultsCaptured)
        {
            return;
        }

        _defaultsCaptured = true;
        _defaultHorizontal = HorizontalAlignment;
        _defaultVertical = VerticalAlignment;
        _defaultMargin = Margin;
    }

    /// <summary>
    /// The XAML margin is a spot on the game's own screen (the mulligan strip under the offered cards), in units of a
    /// 1080-high screen: it must not grow or shrink with the player's overlay scale, only the panel's content does.
    /// </summary>
    public bool GameAnchored { get; set; }

    /// <summary>Places the panel from settings for the current surface size.</summary>
    /// <param name="overlayScale">The player's overlay scale, which a <see cref="GameAnchored"/> default margin undoes.</param>
    public void Apply(PanelPlacement? placement, Size surface, double overlayScale = 1)
    {
        CaptureDefaults();
        if (placement is null)
        {
            _saved = null;
            HorizontalAlignment = _defaultHorizontal;
            VerticalAlignment = _defaultVertical;
            Margin = GameAnchored && overlayScale > 0
                ? new Thickness(_defaultMargin.Left / overlayScale, _defaultMargin.Top / overlayScale, _defaultMargin.Right / overlayScale, _defaultMargin.Bottom / overlayScale)
                : _defaultMargin;
            SetScale(1);
            return;
        }

        SetScale(Math.Clamp(placement.Scale, 0.6, 1.6));
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        _saved = new Point(placement.X * surface.Width, placement.Y * surface.Height);
        Margin = new Thickness(_saved.Value.X, _saved.Value.Y, 0, 0);
        KeepOnScreen();
    }

    /// <summary>
    /// A saved panel is measured without a height limit: the parent only offers the room down to the screen edge, so a
    /// panel that grows past it was cut there and never looked too tall for <see cref="KeepOnScreen"/> to move it up.
    /// </summary>
    protected override Size MeasureOverride(Size constraint)
    {
        if (_saved is null)
        {
            return base.MeasureOverride(constraint);
        }

        var size = base.MeasureOverride(new Size(constraint.Width, double.PositiveInfinity));
        if (Math.Abs(size.Height - _contentHeight) > 0.5)
        {
            _contentHeight = size.Height;
            // The margin is not changed in the middle of a layout pass.
            Dispatcher.BeginInvoke(KeepOnScreen, DispatcherPriority.Render);
        }

        return size;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        KeepOnScreen();
    }

    /// <summary>Pulls a saved panel back inside the screen when its content grows past an edge.</summary>
    private void KeepOnScreen()
    {
        if (_saved is not { } saved || _dragging || Surface is not { ActualWidth: > 0, ActualHeight: > 0 } surface || ActualWidth <= 0)
        {
            return;
        }

        // Past an edge the surface grid hands the panel only the space left, so ActualWidth shrinks with it and the
        // panel would never come back; content with a fixed size says how much room it really needs.
        var content = Content as FrameworkElement;
        var width = Math.Max(ActualWidth, content is { Width: > 0 } ? content.Width + content.Margin.Left + content.Margin.Right : 0);
        var height = Math.Max(Math.Max(ActualHeight, _contentHeight), content is { Height: > 0 } ? content.Height + content.Margin.Top + content.Margin.Bottom : 0);
        // Fit sets the surface size before the next layout pass: ActualWidth is still the old size (a panel applied
        // right after an overlay-scale change was clamped to the smaller surface and stayed there).
        var surfaceWidth = double.IsNaN(surface.Width) ? surface.ActualWidth : surface.Width;
        var surfaceHeight = double.IsNaN(surface.Height) ? surface.ActualHeight : surface.Height;
        var left = Math.Clamp(saved.X, 0, Math.Max(0, surfaceWidth - (width * _scale.ScaleX)));
        var top = Math.Clamp(saved.Y, 0, Math.Max(0, surfaceHeight - (height * _scale.ScaleY)));
        var margin = new Thickness(left, top, 0, 0);
        if (Margin != margin)
        {
            Margin = margin;
        }
    }

    private FrameworkElement? Surface => Parent as FrameworkElement;

    private Point TopLeft() => Surface is { } surface ? TranslatePoint(new Point(0, 0), surface) : new Point(Margin.Left, Margin.Top);

    private PanelPlacement CurrentPlacement()
    {
        var surface = Surface;
        var origin = TopLeft();
        return new PanelPlacement
        {
            X = surface is { ActualWidth: > 0 } s ? origin.X / s.ActualWidth : 0,
            Y = surface is { ActualHeight: > 0 } h ? origin.Y / h.ActualHeight : 0,
            Scale = _scale.ScaleX,
        };
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!CanMove || Surface is not { } surface)
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        CaptureDefaults();
        _dragging = true;
        _dragStart = e.GetPosition(surface);
        _originStart = TopLeft();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (!_dragging || Surface is not { } surface)
        {
            base.OnPreviewMouseMove(e);
            return;
        }

        var position = e.GetPosition(surface);
        var width = ActualWidth * _scale.ScaleX;
        var height = ActualHeight * _scale.ScaleY;
        // Edges and the centre line pull the panel in, so panels line up without pixel hunting.
        var left = Math.Clamp(PanelSnap.Snap(_originStart.X + (position.X - _dragStart.X), width, surface.ActualWidth), 0, Math.Max(0, surface.ActualWidth - width));
        var top = Math.Clamp(PanelSnap.Snap(_originStart.Y + (position.Y - _dragStart.Y), height, surface.ActualHeight), 0, Math.Max(0, surface.ActualHeight - height));
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(left, top, 0, 0);
        e.Handled = true;
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging)
        {
            base.OnPreviewMouseLeftButtonUp(e);
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        _saved = TopLeft();
        PlacementChanged?.Invoke(this, CurrentPlacement());
        e.Handled = true;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (!CanMove)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        CaptureDefaults();
        var next = Math.Clamp(Math.Round(_scale.ScaleX + (e.Delta > 0 ? 0.05 : -0.05), 2), 0.6, 1.6);
        var origin = TopLeft();
        SetScale(next);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(origin.X, origin.Y, 0, 0);
        _saved = origin;
        PlacementChanged?.Invoke(this, CurrentPlacement());
        e.Handled = true;
    }

    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (!IsEditing)
        {
            base.OnPreviewMouseRightButtonUp(e);
            return;
        }

        PlacementChanged?.Invoke(this, null);
        e.Handled = true;
    }
}
