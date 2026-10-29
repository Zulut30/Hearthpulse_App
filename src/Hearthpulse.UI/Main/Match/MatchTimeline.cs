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
    private const double TickTop = 4;
    private const double TickHeight = 10;
    private const double SpanTop = 16;
    private const double SpanHeight = 22;
    private const double TickZone = 8;

    /// <summary>The pointer moves this far with the button down before a press scrubs instead of clicking.</summary>
    private const double DragSlop = 4;

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
                Background = ThemeBrushes.Get(tick.Look switch
                {
                    MatchTimelineLook.Mine => "Accent",
                    MatchTimelineLook.Theirs => "Accent2",
                    _ => "Muted",
                }),
            };
            var zone = new Border { Width = TickZone, Height = TickHeight, Background = Brushes.Transparent, Child = mark };
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
            var (fill, edge, text) = current ? ("AccentContainer", "Accent", "OnAccentContainer") : segment.Look switch
            {
                MatchTimelineLook.Mine => ("AccentSoft", null, "Text"),
                MatchTimelineLook.Theirs => ("Accent2Soft", null, "Muted"),
                MatchTimelineLook.Shop => ("Surface3", "Border", "Muted"),
                MatchTimelineLook.Won => ("PositiveSoft", "Positive", "Muted"),
                MatchTimelineLook.Lost => ("NegativeSoft", "Negative", "Muted"),
                MatchTimelineLook.Combat => ("Surface4", "Border", "Muted"),
                _ => ("Surface4", (string?)null, "Muted"),
            };
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
            var pixels = (segment.End - segment.Start) * width - 2;
            label.IsVisible = segment.Label.Length > 0 && !segment.IsCombat && pixels >= 18;
            glyph.IsVisible = segment.IsCombat && pixels >= 16;
            box.Measure(new Size(Math.Max(0, pixels), SpanHeight));
        }

        foreach (var (_, zone) in _ticks)
        {
            zone.Measure(Size.Infinity);
        }

        _line.Measure(Size.Infinity);
        _diamond.Measure(Size.Infinity);
        return new Size(width, 40);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        foreach (var (segment, box, _, _, _) in _spans)
        {
            box.Arrange(new Rect(segment.Start * width + 1, SpanTop, Math.Max(1, (segment.End - segment.Start) * width - 2), SpanHeight));
        }

        foreach (var (tick, zone) in _ticks)
        {
            zone.Arrange(new Rect(tick.At * width - TickZone / 2, TickTop, TickZone, TickHeight));
        }

        var x = Position * width;
        _line.Arrange(new Rect(x - 1, TickTop, 2, SpanTop + SpanHeight - TickTop));
        _diamond.Arrange(new Rect(x - 4, 0, 8, 8));
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
        _scrubbing |= Math.Abs(point.X - start.X) > DragSlop;
        if (_scrubbing)
        {
            Go(MatchViewModel.FrameAt(point.X / Math.Max(1, Bounds.Width), FrameMax + 1));
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
    private int FrameUnder(Point point)
    {
        var width = Math.Max(1, Bounds.Width);
        if (point.Y < SpanTop)
        {
            var near = _ticks.Select(t => t.Tick).Where(t => Math.Abs(t.At * width - point.X) <= TickZone / 2)
                .OrderBy(t => Math.Abs(t.At * width - point.X)).FirstOrDefault();
            if (near is not null)
            {
                return near.Frame;
            }
        }
        else if (_spans.Select(s => s.Segment).FirstOrDefault(s => point.X >= s.Start * width && point.X < s.End * width) is { } segment)
        {
            return segment.FirstFrame;
        }

        return MatchViewModel.FrameAt(point.X / width, FrameMax + 1);
    }

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
