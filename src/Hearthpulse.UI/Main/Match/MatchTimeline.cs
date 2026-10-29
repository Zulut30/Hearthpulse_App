using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main.Match;

/// <summary>
/// The whole-game timeline of the match page's player: event ticks over the stretches (turns, or tavern and combats),
/// the position line and its diamond. Children are plain borders placed by the fractions of the view model; the panel
/// takes the pointer itself: a click on a tick goes to its step, on a stretch to its first step, a drag scrubs.
/// </summary>
public sealed class MatchTimeline : Panel
{
    public static readonly StyledProperty<IReadOnlyList<MatchTimelineSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<MatchTimeline, IReadOnlyList<MatchTimelineSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IReadOnlyList<MatchTimelineTick>?> TicksProperty =
        AvaloniaProperty.Register<MatchTimeline, IReadOnlyList<MatchTimelineTick>?>(nameof(Ticks));

    public static readonly StyledProperty<int> CurrentProperty = AvaloniaProperty.Register<MatchTimeline, int>(nameof(Current), -1);

    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<MatchTimeline, double>(nameof(Position));

    public static readonly StyledProperty<int> FrameMaxProperty = AvaloniaProperty.Register<MatchTimeline, int>(nameof(FrameMax));

    public static readonly StyledProperty<ICommand?> CommandProperty = AvaloniaProperty.Register<MatchTimeline, ICommand?>(nameof(Command));

    private readonly List<(MatchTimelineSegment Segment, Border Box, TextBlock Label, TextBlock Glyph, Border Bar)> _spans = [];
    private readonly List<(MatchTimelineTick Tick, Border Zone)> _ticks = [];
    private readonly Rectangle _line = new() { Width = 2, IsHitTestVisible = false };
    private readonly Border _diamond = new()
    {
        Width = 8,
        Height = 8,
        IsHitTestVisible = false,
        RenderTransformOrigin = RelativePoint.Center,
        RenderTransform = new RotateTransform(45),
    };

    private Point? _pressed;
    private bool _scrubbing;

    static MatchTimeline()
    {
        AffectsArrange<MatchTimeline>(PositionProperty);
    }

    public MatchTimeline()
    {
        Height = 40;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        _line.Fill = ThemeBrushes.Get("Accent");
        _diamond.Background = ThemeBrushes.Get("Accent");
        Rebuild();
    }

    public IReadOnlyList<MatchTimelineSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    public IReadOnlyList<MatchTimelineTick>? Ticks { get => GetValue(TicksProperty); set => SetValue(TicksProperty, value); }

    /// <summary>The index of the stretch holding the step on screen, or -1.</summary>
    public int Current { get => GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

    /// <summary>The step on screen as a fraction of the width.</summary>
    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    /// <summary>The last step: a drag maps the pointer to one of the FrameMax + 1 slots.</summary>
    public int FrameMax { get => GetValue(FrameMaxProperty); set => SetValue(FrameMaxProperty, value); }

    /// <summary>Goes to a step (its number is the parameter).</summary>
    public ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty || change.Property == TicksProperty)
        {
            Rebuild();
        }
        else if (change.Property == CurrentProperty)
        {
            Restyle();
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        _spans.Clear();
        _ticks.Clear();
        var display = Font("DisplayFont");
        var icons = Font("IconFont");
        foreach (var segment in Segments ?? [])
        {
            var bar = new Border
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = ThemeBrushes.Get("Accent"),
                IsVisible = false,
            };
            var label = new TextBlock
            {
                Text = segment.Label,
                FontFamily = display,
                FontSize = 10.5,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var glyph = new TextBlock
            {
                Text = "\U000F0787",
                FontFamily = icons,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = false,
            };
            var box = new Border { CornerRadius = new CornerRadius(4), Child = new Panel { Children = { bar, label, glyph } } };
            ToolTip.SetTip(box, segment.Tooltip);
            _spans.Add((segment, box, label, glyph, bar));
            Children.Add(box);
        }

        foreach (var tick in Ticks ?? [])
        {
            var mark = new Border
            {
                Width = 2,
                Height = 8,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = ThemeBrushes.Get(MatchTimelineLayout.TickBrush(tick.Look)),
            };
            var zone = new Border { Width = MatchTimelineLayout.TickZone, Height = MatchTimelineLayout.TickHeight, Background = Brushes.Transparent, Child = mark };
            ToolTip.SetTip(zone, tick.Tooltip);
            _ticks.Add((tick, zone));
            Children.Add(zone);
        }

        Children.Add(_line);
        Children.Add(_diamond);
        Restyle();
        InvalidateMeasure();
    }

    /// <summary>Each stretch in its look; the current one in the selected look (as the turn chip on screen used to be).</summary>
    private void Restyle()
    {
        for (var i = 0; i < _spans.Count; i++)
        {
            var (segment, box, label, glyph, bar) = _spans[i];
            var current = i == Current;
            var (fill, edge, text) = MatchTimelineLayout.SpanLook(segment.Look, current);
            box.Background = ThemeBrushes.Get(fill);
            box.BorderThickness = new Thickness(edge is null ? 0 : 1);
            box.BorderBrush = edge is null ? null : ThemeBrushes.Get(edge);
            label.Foreground = ThemeBrushes.Get(text);
            glyph.Foreground = ThemeBrushes.Get(current ? text : "Muted");
            bar.IsVisible = segment.Look == MatchTimelineLook.Mine && !current;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        foreach (var (segment, box, label, glyph, _) in _spans)
        {
            // Labels and the combat glyph only where they fit.
            var pixels = MatchTimelineLayout.SpanPixels(segment, width);
            label.IsVisible = MatchTimelineLayout.ShowsLabel(segment, pixels);
            glyph.IsVisible = MatchTimelineLayout.ShowsGlyph(segment, pixels);
            box.Measure(new Size(Math.Max(0, pixels), MatchTimelineLayout.SpanHeight));
        }

        foreach (var (_, zone) in _ticks)
        {
            zone.Measure(Size.Infinity);
        }

        _line.Measure(Size.Infinity);
        _diamond.Measure(Size.Infinity);
        return new Size(width, MatchTimelineLayout.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        foreach (var (segment, box, _, _, _) in _spans)
        {
            box.Arrange(Rect(MatchTimelineLayout.Span(segment, width)));
        }

        foreach (var (tick, zone) in _ticks)
        {
            zone.Arrange(Rect(MatchTimelineLayout.Tick(tick, width)));
        }

        _line.Arrange(Rect(MatchTimelineLayout.Line(Position, width)));
        _diamond.Arrange(Rect(MatchTimelineLayout.Diamond(Position, width)));
        return finalSize;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressed = e.GetPosition(this);
        _scrubbing = false;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressed is not { } start)
        {
            return;
        }

        var point = e.GetPosition(this);
        _scrubbing |= Math.Abs(point.X - start.X) > MatchTimelineLayout.DragSlop;
        if (_scrubbing)
        {
            Go(MatchTimelineLayout.FrameAt(point.X, Bounds.Width, FrameMax));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressed is null)
        {
            return;
        }

        if (!_scrubbing)
        {
            Go(FrameUnder(e.GetPosition(this)));
        }

        _pressed = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = null;
        _scrubbing = false;
    }

    /// <summary>A click: the nearest tick in the tick row, else the stretch under the pointer, else the slot there.</summary>
    private int FrameUnder(Point point) =>
        MatchTimelineLayout.FrameUnder(_ticks.Select(t => t.Tick), _spans.Select(s => s.Segment), point.X, point.Y, Bounds.Width, FrameMax);

    private static Rect Rect((double X, double Y, double Width, double Height) r) => new(r.X, r.Y, r.Width, r.Height);

    private void Go(int frame)
    {
        if (Command is { } command && command.CanExecute(frame))
        {
            command.Execute(frame);
        }
    }

    private FontFamily Font(string key) =>
        Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is FontFamily family ? family : FontFamily.Default;
}
