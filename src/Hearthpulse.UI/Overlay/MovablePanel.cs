using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core;

namespace Hearthpulse.UI.Overlay;

/// <summary>Marks overlay elements that should receive the mouse; everything else clicks through to the game.</summary>
public static class OverlayHit
{
    public static readonly AttachedProperty<bool> InteractiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Interactive", typeof(OverlayHit));

    public static bool GetInteractive(AvaloniaObject element) => element.GetValue(InteractiveProperty);

    public static void SetInteractive(AvaloniaObject element, bool value) => element.SetValue(InteractiveProperty, value);
}

/// <summary>
/// Overlay panel host the player can drag, resize (corner marker or mouse wheel), fade (Shift+wheel) and reset
/// (right-click) while the overlay is in layout mode, and the same during a game while holding Ctrl over it
/// (<see cref="IsDragArmed"/>). Its name tag carries buttons for size, opacity, compact lists and reset. Without a saved
/// placement it keeps the alignment and margin from XAML.
/// </summary>
public sealed class MovablePanel : ContentControl
{
    public static readonly StyledProperty<string> IdProperty = AvaloniaProperty.Register<MovablePanel, string>(nameof(Id), string.Empty);

    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<MovablePanel, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<string> HintProperty = AvaloniaProperty.Register<MovablePanel, string>(nameof(Hint), string.Empty);

    public static readonly StyledProperty<bool> ShownProperty = AvaloniaProperty.Register<MovablePanel, bool>(nameof(Shown));

    public static readonly StyledProperty<bool> IsEditingProperty = AvaloniaProperty.Register<MovablePanel, bool>(nameof(IsEditing));

    public static readonly StyledProperty<bool> IsDragArmedProperty = AvaloniaProperty.Register<MovablePanel, bool>(nameof(IsDragArmed));

    public static readonly StyledProperty<bool> InteractiveContentProperty =
        AvaloniaProperty.Register<MovablePanel, bool>(nameof(InteractiveContent));

    public static readonly StyledProperty<string> ScaleTextProperty = AvaloniaProperty.Register<MovablePanel, string>(nameof(ScaleText), string.Empty);

    public static readonly StyledProperty<HorizontalAlignment> PlaceholderAlignmentProperty =
        AvaloniaProperty.Register<MovablePanel, HorizontalAlignment>(nameof(PlaceholderAlignment), HorizontalAlignment.Stretch);

    public static readonly StyledProperty<double> PanelOpacityProperty = AvaloniaProperty.Register<MovablePanel, double>(nameof(PanelOpacity), 1);

    public static readonly StyledProperty<string> OpacityTextProperty = AvaloniaProperty.Register<MovablePanel, string>(nameof(OpacityText), string.Empty);

    public static readonly StyledProperty<bool> CanCompactProperty = AvaloniaProperty.Register<MovablePanel, bool>(nameof(CanCompact));

    /// <summary>How far below its spot a panel starts when it fades in, in overlay units.</summary>
    private const double RevealRise = 6;

    private static readonly TimeSpan RevealTime = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HideTime = TimeSpan.FromMilliseconds(110);

    private readonly TranslateTransform _rise = new();
    private readonly DispatcherTimer _fade = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _fadeClock = new();
    private double _fadeFrom;
    private bool _fadingIn;
    private bool _hiding;
    private bool _lastEditing;
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
    private double _scale = 1;
    private LayoutTransformControl? _scaler;

    // Saved top-left in surface units (null = XAML default). Kept apart from Margin so a panel that grows past the
    // screen edge is pulled back inside, then returns to its spot when it shrinks again.
    private Point? _saved;

    // Height of the content measured without the parent's limit (see MeasureOverride), for KeepOnScreen.
    private double _contentHeight;

    static MovablePanel()
    {
        ShownProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
        IsEditingProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
        IsDragArmedProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
        InteractiveContentProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
        HintProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
    }

    public MovablePanel()
    {
        IsVisible = false;
        RenderTransform = _rise;
        _fade.Tick += (_, _) => StepFade();
        // The layout-mode frame and name tag draw just outside the panel.
        ClipToBounds = false;
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerCaptureLostEvent, OnCaptureLost);
        SizeChanged += (_, _) => KeepOnScreen();
    }

    public string Id { get => GetValue(IdProperty); set => SetValue(IdProperty, value); }

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>When the panel shows, for its layout-mode placeholder (menu and draft panels); empty for match panels.</summary>
    public string Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }

    /// <summary>
    /// Where the layout-mode placeholder sits inside a wide panel. The pick rows (heroes, trinkets, Discover, tavern) share
    /// the game's card row and are as wide as it: stretched, their placeholders would lie on top of each other.
    /// </summary>
    public HorizontalAlignment PlaceholderAlignment { get => GetValue(PlaceholderAlignmentProperty); set => SetValue(PlaceholderAlignmentProperty, value); }

    public bool Shown { get => GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    /// <summary>
    /// Set by the overlay while Ctrl is held over this panel outside layout mode: the panel takes the mouse, shows its
    /// frame and name tag, and moves or resizes like in layout mode. Without Ctrl it lets every click through as before.
    /// </summary>
    public bool IsDragArmed { get => GetValue(IsDragArmedProperty); set => SetValue(IsDragArmedProperty, value); }

    /// <summary>The player is moving or resizing the panel right now (the overlay keeps it armed until the button is released).</summary>
    public bool IsDragging => _dragging;

    private bool CanMove => IsEditing || IsDragArmed;

    /// <summary>Whether the content has its own buttons and should receive the mouse outside layout mode.</summary>
    public bool InteractiveContent { get => GetValue(InteractiveContentProperty); set => SetValue(InteractiveContentProperty, value); }

    /// <summary>The player's size for the layout-mode tag ("115%"); empty at the default size.</summary>
    public string ScaleText { get => GetValue(ScaleTextProperty); private set => SetValue(ScaleTextProperty, value); }

    /// <summary>The player's opacity for this panel; only the content fades, the layout-mode frame and tag stay solid.</summary>
    public double PanelOpacity { get => GetValue(PanelOpacityProperty); private set => SetValue(PanelOpacityProperty, value); }

    /// <summary>The player's opacity for the tag ("70%"); empty when opaque.</summary>
    public string OpacityText { get => GetValue(OpacityTextProperty); private set => SetValue(OpacityTextProperty, value); }

    /// <summary>The panel holds card lists that have a compact form: its tag shows the compact button.</summary>
    public bool CanCompact { get => GetValue(CanCompactProperty); set => SetValue(CanCompactProperty, value); }

    /// <summary>Card lists in the panel use short rows without art (the <c>compact</c> class, styled in OverlayView.axaml).</summary>
    public bool IsCompact => Classes.Contains("compact");

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
        Classes.Set("compact", look?.Compact == true);
    }

    private void ChangeLook(double opacity, bool compact)
    {
        var look = new PanelLook { Opacity = opacity, Compact = compact };
        ApplyLook(look);
        LookChanged?.Invoke(this, look.IsDefault ? null : look);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _scaler = e.NameScope.Find<LayoutTransformControl>("PART_Scaler");
        _chrome = e.NameScope.Find<Control>("PART_Chrome");
        _grip = e.NameScope.Find<Control>("PART_Grip");
        Hook("PART_Smaller", () => StepScale(up: false));
        Hook("PART_Larger", () => StepScale(up: true));
        Hook("PART_Fade", () => ChangeLook(PanelSnap.NextOpacity(PanelOpacity), IsCompact));
        Hook("PART_Compact", () => ChangeLook(PanelOpacity, !IsCompact));
        Hook("PART_Reset", Reset);
        ApplyScale();
        UpdateChrome();

        void Hook(string name, Action action)
        {
            if (e.NameScope.Find<Button>(name) is { } button)
            {
                button.Click += (_, args) =>
                {
                    action();
                    args.Handled = true;
                };
            }
        }
    }

    // The name tag and its buttons, which follow a wide row's placeholder to its end of the row.
    private Control? _chrome;

    // The corner marker that resizes the panel.
    private Control? _grip;

    /// <summary>Back to the XAML spot and size, opaque with full rows.</summary>
    private void Reset()
    {
        ApplyLook(null);
        LookChanged?.Invoke(this, null);
        PlacementChanged?.Invoke(this, null);
    }

    /// <summary>Whether the pointer went down on one of the tag's buttons, which must click rather than start a drag.</summary>
    private bool OnTool(object? source)
    {
        for (var node = source as Visual; node is not null && node != this; node = node.GetVisualParent())
        {
            if (node is Button)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the pointer went down on the corner marker, which resizes the panel instead of moving it.</summary>
    private bool OnGrip(object? source)
    {
        for (var node = source as Visual; node is not null && node != this; node = node.GetVisualParent())
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
        // Off with the Windows animation setting (and elsewhere), like the card pool's reveal.
        var animate = IsEditing == _lastEditing && !IsEditing && TopLevel.GetTopLevel(this) is not null && PoolInput.AnimationsOn();
        _lastEditing = IsEditing;
        SetVisible(Shown || IsEditing, animate);
        Classes.Set("editing", IsEditing);
        Classes.Set("placeholder", IsEditing && !Shown);
        Classes.Set("hinted", !string.IsNullOrEmpty(Hint));
        Classes.Set("armed", IsDragArmed && !IsEditing);
        OverlayHit.SetInteractive(this, CanMove || InteractiveContent);
        Cursor = CanMove ? new Cursor(StandardCursorType.SizeAll) : null;
        UpdateChrome();
    }

    /// <summary>
    /// Where the pointer keeps an armed panel armed, in its own coordinates: the panel, the strip with its name tag and
    /// buttons, which can reach past a narrow panel's right edge, and the corner marker just outside its corner.
    /// </summary>
    public Rect ArmedArea()
    {
        var area = new Rect(Bounds.Size);
        Add(_chrome);
        Add(_grip);
        return area;

        void Add(Control? part)
        {
            if (part is { IsVisible: true } && part.TranslatePoint(default, this) is { } origin)
            {
                area = area.Union(new Rect(origin, part.Bounds.Size));
            }
        }
    }

    /// <summary>A placeholder in a wide row sits at one end of it: its tag buttons go with it.</summary>
    private void UpdateChrome()
    {
        if (_chrome is not null)
        {
            _chrome.HorizontalAlignment = IsEditing && !Shown && PlaceholderAlignment != HorizontalAlignment.Stretch ? PlaceholderAlignment : HorizontalAlignment.Left;
        }
    }

    /// <summary>
    /// Shows or hides the panel; with <paramref name="animate"/> a short fade and rise in (ease-out), or a quicker fade
    /// out. The timer runs only during those few frames, never while the panel just sits there.
    /// </summary>
    private void SetVisible(bool visible, bool animate)
    {
        if (visible)
        {
            var appearing = !IsVisible || _hiding;
            // A panel shown again mid fade-out comes back from where it was.
            var from = _hiding ? Opacity : 0;
            _hiding = false;
            IsHitTestVisible = true;
            IsVisible = true;
            if (animate && appearing)
            {
                StartFade(from, fadingIn: true);
            }
            else if (!animate)
            {
                StopFade();
            }

            return;
        }

        if (!IsVisible || _hiding)
        {
            return;
        }

        if (!animate)
        {
            StopFade();
            IsVisible = false;
            return;
        }

        // The game moved on: the panel stops taking clicks at once and is gone a moment later.
        _hiding = true;
        IsHitTestVisible = false;
        StartFade(Opacity, fadingIn: false);
    }

    private void StartFade(double from, bool fadingIn)
    {
        _fadeFrom = from;
        _fadingIn = fadingIn;
        Opacity = from;
        _rise.Y = fadingIn ? RevealRise : 0;
        _fadeClock.Restart();
        _fade.Start();
    }

    private void StepFade()
    {
        var t = Math.Min(1, _fadeClock.Elapsed / (_fadingIn ? RevealTime : HideTime));
        var eased = 1 - Math.Pow(1 - t, 3);
        Opacity = _fadingIn ? _fadeFrom + ((1 - _fadeFrom) * eased) : _fadeFrom * (1 - eased);
        _rise.Y = _fadingIn ? RevealRise * (1 - eased) : 0;
        if (t < 1)
        {
            return;
        }

        if (_hiding)
        {
            _hiding = false;
            IsHitTestVisible = true;
            IsVisible = false;
        }

        StopFade();
    }

    private void StopFade()
    {
        _fade.Stop();
        Opacity = 1;
        _rise.Y = 0;
    }

    private void ApplyScale()
    {
        ScaleText = PanelSnap.ScaleText(_scale);
        if (_scaler is not null)
        {
            _scaler.LayoutTransform = Math.Abs(_scale - 1) < 0.001 ? null : new ScaleTransform(_scale, _scale);
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
            _scale = 1;
            ApplyScale();
            return;
        }

        _scale = Math.Clamp(placement.Scale, 0.6, 1.6);
        ApplyScale();
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
    protected override Size MeasureOverride(Size availableSize)
    {
        if (_saved is null)
        {
            return base.MeasureOverride(availableSize);
        }

        var size = base.MeasureOverride(availableSize.WithHeight(double.PositiveInfinity));
        if (Math.Abs(size.Height - _contentHeight) > 0.5)
        {
            _contentHeight = size.Height;
            // The margin is not changed in the middle of a layout pass.
            Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Render);
        }

        return size;
    }

    /// <summary>Pulls a saved panel back inside the screen when its content grows past an edge.</summary>
    private void KeepOnScreen()
    {
        if (_saved is not { } saved || _dragging || Surface is not { Bounds: { Width: > 0, Height: > 0 } } surface || Bounds.Width <= 0)
        {
            return;
        }

        // Past an edge the surface grid hands the panel only the space left, so Bounds shrink with it and the panel
        // would never come back; content with a fixed size says how much room it really needs.
        var content = Content as Control;
        var width = Math.Max(Bounds.Width, content is { Width: > 0 } ? (content.Width + content.Margin.Left + content.Margin.Right) * _scale : 0);
        var height = Math.Max(Math.Max(Bounds.Height, _contentHeight), content is { Height: > 0 } ? (content.Height + content.Margin.Top + content.Margin.Bottom) * _scale : 0);
        // Fit sets the surface size before the next layout pass: its Bounds are still the old size (a panel applied
        // right after an overlay-scale change was clamped to the smaller surface and stayed there).
        var surfaceWidth = double.IsNaN(surface.Width) ? surface.Bounds.Width : surface.Width;
        var surfaceHeight = double.IsNaN(surface.Height) ? surface.Bounds.Height : surface.Height;
        var left = Math.Clamp(saved.X, 0, Math.Max(0, surfaceWidth - width));
        var top = Math.Clamp(saved.Y, 0, Math.Max(0, surfaceHeight - height));
        var margin = new Thickness(left, top, 0, 0);
        if (Margin != margin)
        {
            Margin = margin;
        }
    }

    private Control? Surface => Parent as Control;

    private Point TopLeft() => Surface is { } surface && this.TranslatePoint(default, surface) is { } p ? p : new Point(Margin.Left, Margin.Top);

    private PanelPlacement CurrentPlacement()
    {
        var surface = Surface;
        var origin = TopLeft();
        return new PanelPlacement
        {
            X = surface is { Bounds.Width: > 0 } s ? origin.X / s.Bounds.Width : 0,
            Y = surface is { Bounds.Height: > 0 } h ? origin.Y / h.Bounds.Height : 0,
            Scale = _scale,
        };
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!CanMove || Surface is not { } surface || OnTool(e.Source))
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            // Right-click resets the panel (layout mode, or Ctrl held during a game); it is released below.
            e.Handled = true;
            return;
        }

        CaptureDefaults();
        _dragging = true;
        _resizing = OnGrip(e.Source);
        _startScale = _scale;
        _startSize = Bounds.Size;
        _dragStart = e.GetPosition(surface);
        _originStart = TopLeft();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Surface is not { } surface)
        {
            return;
        }

        var position = e.GetPosition(surface);
        if (_resizing)
        {
            Resize(position - _dragStart);
            e.Handled = true;
            return;
        }

        // Edges, the centre line and the other panels' edges pull the panel in, so panels line up without pixel hunting.
        var neighbours = _neighbours ??= Neighbours(surface);
        var left = Math.Clamp(PanelSnap.Snap(_originStart.X + (position.X - _dragStart.X), Bounds.Width, surface.Bounds.Width, neighbours.Across), 0, Math.Max(0, surface.Bounds.Width - Bounds.Width));
        var top = Math.Clamp(PanelSnap.Snap(_originStart.Y + (position.Y - _dragStart.Y), Bounds.Height, surface.Bounds.Height, neighbours.Down), 0, Math.Max(0, surface.Bounds.Height - Bounds.Height));
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(left, top, 0, 0);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (CanMove && e.InitialPressMouseButton == MouseButton.Right && !OnTool(e.Source))
        {
            Reset();
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            return;
        }

        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// The panel lost the pointer in the middle of a drag (the player switched windows, the platform took the capture
    /// back): the drag ends where the panel is, rather than leaving it dragging, armed and taking clicks.
    /// </summary>
    private void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e) => EndMissedDrag();

    /// <summary>
    /// Ends a move or resize whose release the panel never got (the pointer was lost, or the host read every mouse
    /// button up); it is saved where the panel is, as on the release.
    /// </summary>
    public void EndMissedDrag()
    {
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
        if (Math.Abs(next - _scale) < 0.001)
        {
            return;
        }

        _scale = next;
        ApplyScale();
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(_originStart.X, _originStart.Y, 0, 0);
    }

    // The other shown panels' spans on the surface (left-right, top-bottom), taken once per drag.
    private ((double Start, double End)[] Across, (double Start, double End)[] Down)? _neighbours;

    private ((double Start, double End)[] Across, (double Start, double End)[] Down) Neighbours(Control surface)
    {
        var across = new List<(double, double)>();
        var down = new List<(double, double)>();
        foreach (var other in (surface as Panel)?.Children.OfType<MovablePanel>() ?? [])
        {
            // A wide row's placeholder is only a label at one end of the row: its bounds are not where it shows.
            if (other != this && other is { IsVisible: true, Bounds.Width: > 0 } && (other.Shown || (other.IsEditing && other.PlaceholderAlignment == HorizontalAlignment.Stretch))
                && other.TranslatePoint(default, surface) is { } origin)
            {
                across.Add((origin.X, origin.X + other.Bounds.Width));
                down.Add((origin.Y, origin.Y + other.Bounds.Height));
            }
        }

        return (across.ToArray(), down.ToArray());
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!CanMove)
        {
            return;
        }

        // Shift+wheel may arrive as a sideways scroll.
        var up = (e.Delta.Y != 0 ? e.Delta.Y : e.Delta.X) > 0;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            ChangeLook(PanelSnap.WheelOpacity(PanelOpacity, up), IsCompact);
        }
        else
        {
            StepScale(up);
        }

        e.Handled = true;
    }

    private void StepScale(bool up)
    {
        CaptureDefaults();
        var origin = TopLeft();
        _scale = Math.Clamp(Math.Round(_scale + (up ? 0.05 : -0.05), 2), PanelSnap.MinScale, PanelSnap.MaxScale);
        ApplyScale();
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(origin.X, origin.Y, 0, 0);
        _saved = origin;
        PlacementChanged?.Invoke(this, CurrentPlacement());
    }
}
