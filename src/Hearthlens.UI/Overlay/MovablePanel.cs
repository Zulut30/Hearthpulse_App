using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Hearthlens.Core;

namespace Hearthlens.UI.Overlay;

/// <summary>Marks overlay elements that should receive the mouse; everything else clicks through to the game.</summary>
public static class OverlayHit
{
    public static readonly AttachedProperty<bool> InteractiveProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Interactive", typeof(OverlayHit));

    public static bool GetInteractive(AvaloniaObject element) => element.GetValue(InteractiveProperty);

    public static void SetInteractive(AvaloniaObject element, bool value) => element.SetValue(InteractiveProperty, value);
}

/// <summary>
/// Overlay panel host the player can drag, resize (mouse wheel) and reset (right-click) while the overlay is in
/// layout mode. Without a saved placement it keeps the alignment and margin from XAML.
/// </summary>
public sealed class MovablePanel : ContentControl
{
    public static readonly StyledProperty<string> IdProperty = AvaloniaProperty.Register<MovablePanel, string>(nameof(Id), string.Empty);

    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<MovablePanel, string>(nameof(Title), string.Empty);

    public static readonly StyledProperty<bool> ShownProperty = AvaloniaProperty.Register<MovablePanel, bool>(nameof(Shown));

    public static readonly StyledProperty<bool> IsEditingProperty = AvaloniaProperty.Register<MovablePanel, bool>(nameof(IsEditing));

    public static readonly StyledProperty<bool> InteractiveContentProperty =
        AvaloniaProperty.Register<MovablePanel, bool>(nameof(InteractiveContent));

    private HorizontalAlignment _defaultHorizontal;
    private VerticalAlignment _defaultVertical;
    private Thickness _defaultMargin;
    private bool _defaultsCaptured;
    private Point _dragStart;
    private Point _originStart;
    private bool _dragging;
    private double _scale = 1;
    private LayoutTransformControl? _scaler;

    // Saved top-left in surface units (null = XAML default). Kept apart from Margin so a panel that grows past the
    // screen edge is pulled back inside, then returns to its spot when it shrinks again.
    private Point? _saved;

    static MovablePanel()
    {
        ShownProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
        IsEditingProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
        InteractiveContentProperty.Changed.AddClassHandler<MovablePanel>((p, _) => p.UpdateState());
    }

    public MovablePanel()
    {
        IsVisible = false;
        // The layout-mode frame and name tag draw just outside the panel.
        ClipToBounds = false;
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => KeepOnScreen();
    }

    public string Id { get => GetValue(IdProperty); set => SetValue(IdProperty, value); }

    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public bool Shown { get => GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    /// <summary>Whether the content has its own buttons and should receive the mouse outside layout mode.</summary>
    public bool InteractiveContent { get => GetValue(InteractiveContentProperty); set => SetValue(InteractiveContentProperty, value); }

    /// <summary>Raised when the player moves, resizes or resets the panel (null = reset).</summary>
    public event Action<MovablePanel, PanelPlacement?>? PlacementChanged;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _scaler = e.NameScope.Find<LayoutTransformControl>("PART_Scaler");
        ApplyScale();
    }

    private void UpdateState()
    {
        IsVisible = Shown || IsEditing;
        Classes.Set("editing", IsEditing);
        Classes.Set("placeholder", IsEditing && !Shown);
        OverlayHit.SetInteractive(this, IsEditing || InteractiveContent);
        Cursor = IsEditing ? new Cursor(StandardCursorType.SizeAll) : null;
    }

    private void ApplyScale()
    {
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

    /// <summary>Places the panel from settings for the current surface size.</summary>
    public void Apply(PanelPlacement? placement, Size surface)
    {
        CaptureDefaults();
        if (placement is null)
        {
            _saved = null;
            HorizontalAlignment = _defaultHorizontal;
            VerticalAlignment = _defaultVertical;
            Margin = _defaultMargin;
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

    /// <summary>Pulls a saved panel back inside the screen when its content grows past an edge.</summary>
    private void KeepOnScreen()
    {
        if (_saved is not { } saved || _dragging || Surface is not { Bounds: { Width: > 0, Height: > 0 } } surface || Bounds.Width <= 0)
        {
            return;
        }

        var left = Math.Clamp(saved.X, 0, Math.Max(0, surface.Bounds.Width - Bounds.Width));
        var top = Math.Clamp(saved.Y, 0, Math.Max(0, surface.Bounds.Height - Bounds.Height));
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
        if (!IsEditing || Surface is not { } surface)
        {
            return;
        }

        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
            return;
        }

        CaptureDefaults();
        _dragging = true;
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
        var left = Math.Clamp(_originStart.X + (position.X - _dragStart.X), 0, Math.Max(0, surface.Bounds.Width - Bounds.Width));
        var top = Math.Clamp(_originStart.Y + (position.Y - _dragStart.Y), 0, Math.Max(0, surface.Bounds.Height - Bounds.Height));
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(left, top, 0, 0);
        e.Handled = true;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (IsEditing && e.InitialPressMouseButton == MouseButton.Right)
        {
            PlacementChanged?.Invoke(this, null);
            e.Handled = true;
            return;
        }

        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        _saved = TopLeft();
        PlacementChanged?.Invoke(this, CurrentPlacement());
        e.Handled = true;
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!IsEditing)
        {
            return;
        }

        CaptureDefaults();
        var origin = TopLeft();
        _scale = Math.Clamp(Math.Round(_scale + (e.Delta.Y > 0 ? 0.05 : -0.05), 2), 0.6, 1.6);
        ApplyScale();
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        Margin = new Thickness(origin.X, origin.Y, 0, 0);
        _saved = origin;
        PlacementChanged?.Invoke(this, CurrentPlacement());
        e.Handled = true;
    }
}
