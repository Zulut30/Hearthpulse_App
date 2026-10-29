using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hearthpulse.Core;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// Overlay panel host the player can drag, resize (corner marker or mouse wheel), fade (Shift+wheel) and reset
/// (right-click) while the overlay is in layout mode, and the same during a game while holding Ctrl over it
/// (<see cref="IsDragArmed"/>). Its name tag carries buttons for size, opacity, compact lists and reset. Without a saved
/// placement it keeps the alignment and margin from XAML.
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

    public static readonly DependencyProperty PanelOpacityProperty = DependencyProperty.Register(
        nameof(PanelOpacity), typeof(double), typeof(MovablePanel), new PropertyMetadata(1.0));

    public static readonly DependencyProperty OpacityTextProperty = DependencyProperty.Register(
        nameof(OpacityText), typeof(string), typeof(MovablePanel), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CanCompactProperty = DependencyProperty.Register(
        nameof(CanCompact), typeof(bool), typeof(MovablePanel));

    /// <summary>
    /// Inherited by everything in the panel: the card row templates shorten their rows and drop the art while it is set
    /// (a trigger on the row's root), so no row needs a binding for it.
    /// </summary>
    public static readonly DependencyProperty CompactProperty = DependencyProperty.RegisterAttached(
        "Compact", typeof(bool), typeof(MovablePanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetCompact(DependencyObject d) => (bool)d.GetValue(CompactProperty);

    public static void SetCompact(DependencyObject d, bool value) => d.SetValue(CompactProperty, value);

    /// <summary>Virtual-key code of either Shift key: held while scrolling over a panel, it fades the panel.</summary>
    private const int ShiftKey = 0x10;

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

    // A drag that started on the corner marker resizes from the size the panel had then (scale, on-screen size).
    private bool _resizing;
    private double _startScale;
    private Size _startSize;

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

    /// <summary>The player is moving or resizing the panel right now (the overlay keeps it armed until the button is released).</summary>
    public bool IsDragging => _dragging;

    private bool CanMove => IsEditing || IsDragArmed;

    public bool ShowPlaceholder { get => (bool)GetValue(ShowPlaceholderProperty); private set => SetValue(ShowPlaceholderProperty, value); }

    /// <summary>The player's size for the layout-mode tag ("115%"); empty at the default size.</summary>
    public string ScaleText { get => (string)GetValue(ScaleTextProperty); private set => SetValue(ScaleTextProperty, value); }

    /// <summary>The player's opacity for this panel; only the content fades, the layout-mode frame and tag stay solid.</summary>
    public double PanelOpacity { get => (double)GetValue(PanelOpacityProperty); private set => SetValue(PanelOpacityProperty, value); }

    /// <summary>The player's opacity for the tag ("70%"); empty when opaque.</summary>
    public string OpacityText { get => (string)GetValue(OpacityTextProperty); private set => SetValue(OpacityTextProperty, value); }

    /// <summary>The panel holds card lists that have a compact form: its tag shows the compact button.</summary>
    public bool CanCompact { get => (bool)GetValue(CanCompactProperty); set => SetValue(CanCompactProperty, value); }

    private void SetScale(double scale)
    {
        _scale.ScaleX = _scale.ScaleY = scale;
        ScaleText = PanelSnap.ScaleText(scale);
    }

    /// <summary>Raised when the player moves, resizes or resets the panel (null = reset).</summary>
    public event Action<MovablePanel, PanelPlacement?>? PlacementChanged;

    /// <summary>Raised when the player changes the panel's opacity or compact lists (null = back to the default look).</summary>
    public event Action<MovablePanel, PanelLook?>? LookChanged;

    /// <summary>Shows the panel with the player's look (null = opaque, full rows).</summary>
    public void ApplyLook(PanelLook? look)
    {
        var opacity = Math.Clamp(look?.Opacity ?? 1, PanelLook.MinOpacity, 1);
        PanelOpacity = opacity;
        OpacityText = PanelSnap.OpacityText(opacity);
        SetCompact(this, look?.Compact == true);
    }

    private void ChangeLook(double opacity, bool compact)
    {
        var look = new PanelLook { Opacity = opacity, Compact = compact };
        ApplyLook(look);
        LookChanged?.Invoke(this, look.IsDefault ? null : look);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Hook("PART_Smaller", () => StepScale(up: false));
        Hook("PART_Larger", () => StepScale(up: true));
        Hook("PART_Fade", () => ChangeLook(PanelSnap.NextOpacity(PanelOpacity), GetCompact(this)));
        Hook("PART_Compact", () => ChangeLook(PanelOpacity, !GetCompact(this)));
        Hook("PART_Reset", Reset);
        _chrome = GetTemplateChild("PART_Chrome") as FrameworkElement;
        _grip = GetTemplateChild("PART_Grip") as FrameworkElement;
        UpdateChrome();

        void Hook(string name, Action action)
        {
            if (GetTemplateChild(name) is ButtonBase button)
            {
                button.Click += (_, e) =>
                {
                    action();
                    e.Handled = true;
                };
            }
        }
    }

    // The name tag and its buttons, which follow a wide row's placeholder to its end of the row.
    private FrameworkElement? _chrome;

    // The corner marker that resizes the panel.
    private FrameworkElement? _grip;

    /// <summary>Back to the XAML spot and size, opaque with full rows.</summary>
    private void Reset()
    {
        ApplyLook(null);
        LookChanged?.Invoke(this, null);
        PlacementChanged?.Invoke(this, null);
    }

    /// <summary>Whether the mouse went down on one of the tag's buttons, which must click rather than start a drag.</summary>
    private bool OnTool(object source)
    {
        for (var node = source as DependencyObject; node is not null && node != this; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the mouse went down on the corner marker, which resizes the panel instead of moving it.</summary>
    private bool OnGrip(object source)
    {
        for (var node = source as DependencyObject; node is not null && node != this; node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node == _grip)
            {
                return true;
            }
        }

        return false;
    }

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
        UpdateChrome();
    }

    /// <summary>
    /// Where the mouse keeps an armed panel armed, in its own coordinates: the panel, the strip with its name tag and
    /// buttons, which can reach past a narrow panel's right edge, and the corner marker just outside its corner.
    /// </summary>
    public Rect ArmedArea()
    {
        var area = new Rect(RenderSize);
        Add(_chrome);
        Add(_grip);
        return area;

        void Add(FrameworkElement? part)
        {
            if (part is { Visibility: Visibility.Visible })
            {
                area.Union(new Rect(part.TranslatePoint(new Point(0, 0), this), part.RenderSize));
            }
        }
    }

    /// <summary>A placeholder in a wide row sits at one end of it: its tag buttons go with it.</summary>
    private void UpdateChrome()
    {
        if (_chrome is not null)
        {
            _chrome.HorizontalAlignment = ShowPlaceholder && PlaceholderAlignment != HorizontalAlignment.Stretch ? PlaceholderAlignment : HorizontalAlignment.Left;
        }
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
        if (!CanMove || Surface is not { } surface || OnTool(e.OriginalSource))
        {
            base.OnPreviewMouseLeftButtonDown(e);
            return;
        }

        CaptureDefaults();
        _dragging = true;
        _resizing = OnGrip(e.OriginalSource);
        _startScale = _scale.ScaleX;
        _startSize = new Size(ActualWidth * _scale.ScaleX, ActualHeight * _scale.ScaleY);
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
        if (_resizing)
        {
            Resize(position - _dragStart);
            e.Handled = true;
            return;
        }

        var width = ActualWidth * _scale.ScaleX;
        var height = ActualHeight * _scale.ScaleY;
        // Edges, the centre line and the other panels' edges pull the panel in, so panels line up without pixel hunting.
        var neighbours = _neighbours ??= Neighbours(surface);
        var left = Math.Clamp(PanelSnap.Snap(_originStart.X + (position.X - _dragStart.X), width, surface.ActualWidth, neighbours.Across), 0, Math.Max(0, surface.ActualWidth - width));
        var top = Math.Clamp(PanelSnap.Snap(_originStart.Y + (position.Y - _dragStart.Y), height, surface.ActualHeight, neighbours.Down), 0, Math.Max(0, surface.ActualHeight - height));
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

        EndDrag();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    /// <summary>
    /// The panel lost the mouse in the middle of a drag: it hid because the game moved on, or the player switched
    /// windows. The drag ends where the panel is; before, it stayed dragging, so the overlay kept it armed and taking
    /// clicks, and it followed the mouse the next time it showed.
    /// </summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            EndDrag();
        }
    }

    /// <summary>Ends a move or resize and saves where the panel is and how big, like the wheel does.</summary>
    private void EndDrag()
    {
        _dragging = false;
        _resizing = false;
        _neighbours = null;
        _saved = TopLeft();
        PlacementChanged?.Invoke(this, CurrentPlacement());
    }

    /// <summary>The corner marker: the size follows the pointer and the top-left corner stays where it was.</summary>
    private void Resize(Vector moved)
    {
        var next = PanelSnap.CornerScale(_startScale, _startSize.Width, _startSize.Height, moved.X, moved.Y);
        if (Math.Abs(next - _scale.ScaleX) < 0.001)
        {
            return;
        }

        SetScale(next);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(_originStart.X, _originStart.Y, 0, 0);
    }

    // The other shown panels' spans on the surface (left-right, top-bottom), taken once per drag.
    private ((double Start, double End)[] Across, (double Start, double End)[] Down)? _neighbours;

    private ((double Start, double End)[] Across, (double Start, double End)[] Down) Neighbours(FrameworkElement surface)
    {
        var across = new List<(double, double)>();
        var down = new List<(double, double)>();
        foreach (var other in (surface as Panel)?.Children.OfType<MovablePanel>() ?? [])
        {
            // A wide row's placeholder is only a label at one end of the row: its bounds are not where it shows.
            if (other != this && other is { IsVisible: true, ActualWidth: > 0 } && (other.Shown || (other.IsEditing && other.PlaceholderAlignment == HorizontalAlignment.Stretch)))
            {
                var origin = other.TranslatePoint(new Point(0, 0), surface);
                across.Add((origin.X, origin.X + (other.ActualWidth * other._scale.ScaleX)));
                down.Add((origin.Y, origin.Y + (other.ActualHeight * other._scale.ScaleY)));
            }
        }

        return (across.ToArray(), down.ToArray());
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (!CanMove)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        // The overlay never has the keyboard focus (the game has), so Shift is read from the keyboard itself.
        if (PoolInput.IsDown(ShiftKey))
        {
            ChangeLook(PanelSnap.WheelOpacity(PanelOpacity, e.Delta > 0), GetCompact(this));
        }
        else
        {
            StepScale(e.Delta > 0);
        }

        e.Handled = true;
    }

    private void StepScale(bool up)
    {
        CaptureDefaults();
        var next = Math.Clamp(Math.Round(_scale.ScaleX + (up ? 0.05 : -0.05), 2), PanelSnap.MinScale, PanelSnap.MaxScale);
        var origin = TopLeft();
        SetScale(next);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(origin.X, origin.Y, 0, 0);
        _saved = origin;
        PlacementChanged?.Invoke(this, CurrentPlacement());
    }

    /// <summary>Right-click resets the panel in layout mode, Ctrl+right-click during a game; otherwise it is the game's.</summary>
    protected override void OnPreviewMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (!CanMove)
        {
            base.OnPreviewMouseRightButtonUp(e);
            return;
        }

        Reset();
        e.Handled = true;
    }
}
