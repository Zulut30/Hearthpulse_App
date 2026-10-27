using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Hearthpulse.Core;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// Overlay panel host the player can drag, resize (mouse wheel) and reset (right-click) while the
/// overlay is in layout mode. Without a saved placement it keeps the alignment and margin from XAML.
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

    public static readonly DependencyProperty ShowPlaceholderProperty = DependencyProperty.Register(
        nameof(ShowPlaceholder), typeof(bool), typeof(MovablePanel));

    private readonly ScaleTransform _scale = new(1, 1);
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
        Visibility = Visibility.Collapsed;
    }

    public string Id { get => (string)GetValue(IdProperty); set => SetValue(IdProperty, value); }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>When the panel shows, for its layout-mode placeholder; empty = "Appears during matches".</summary>
    public string Hint { get => (string)GetValue(HintProperty); set => SetValue(HintProperty, value); }

    public bool Shown { get => (bool)GetValue(ShownProperty); set => SetValue(ShownProperty, value); }

    public bool IsEditing { get => (bool)GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }

    public bool ShowPlaceholder { get => (bool)GetValue(ShowPlaceholderProperty); private set => SetValue(ShowPlaceholderProperty, value); }

    /// <summary>Raised when the player moves, resizes or resets the panel (null = reset).</summary>
    public event Action<MovablePanel, PanelPlacement?>? PlacementChanged;

    private void UpdateState()
    {
        Visibility = Shown || IsEditing ? Visibility.Visible : Visibility.Collapsed;
        ShowPlaceholder = IsEditing && !Shown;
        OverlayHit.SetInteractive(this, IsEditing || _interactiveContent);
        Cursor = IsEditing ? Cursors.SizeAll : null;
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
            _scale.ScaleX = _scale.ScaleY = 1;
            return;
        }

        _scale.ScaleX = _scale.ScaleY = Math.Clamp(placement.Scale, 0.6, 1.6);
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
        var left = Math.Clamp(saved.X, 0, Math.Max(0, surface.ActualWidth - (width * _scale.ScaleX)));
        var top = Math.Clamp(saved.Y, 0, Math.Max(0, surface.ActualHeight - (height * _scale.ScaleY)));
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
        if (!IsEditing || Surface is not { } surface)
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
        var left = Math.Clamp(_originStart.X + (position.X - _dragStart.X), 0, Math.Max(0, surface.ActualWidth - width));
        var top = Math.Clamp(_originStart.Y + (position.Y - _dragStart.Y), 0, Math.Max(0, surface.ActualHeight - height));
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
        if (!IsEditing)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        CaptureDefaults();
        var next = Math.Clamp(Math.Round(_scale.ScaleX + (e.Delta > 0 ? 0.05 : -0.05), 2), 0.6, 1.6);
        var origin = TopLeft();
        _scale.ScaleX = _scale.ScaleY = next;
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
