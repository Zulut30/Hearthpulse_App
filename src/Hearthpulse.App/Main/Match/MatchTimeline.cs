using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Hearthpulse.App.Main.Match;

/// <summary>
/// The whole-game timeline of the match page's player: event ticks over the stretches (turns, or tavern and combats),
/// the position line and its diamond. Children are plain borders placed by the fractions of the view model; the panel
/// takes the mouse itself: a click on a tick goes to its step, on a stretch to its first step, a drag scrubs.
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

    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(nameof(Segments), typeof(IReadOnlyList<MatchTimelineSegment>),
        typeof(MatchTimeline), new FrameworkPropertyMetadata(null, (d, _) => ((MatchTimeline)d).Rebuild()));

    public static readonly DependencyProperty TicksProperty = DependencyProperty.Register(nameof(Ticks), typeof(IReadOnlyList<MatchTimelineTick>),
        typeof(MatchTimeline), new FrameworkPropertyMetadata(null, (d, _) => ((MatchTimeline)d).Rebuild()));

    public static readonly DependencyProperty CurrentProperty = DependencyProperty.Register(nameof(Current), typeof(int), typeof(MatchTimeline),
        new FrameworkPropertyMetadata(-1, (d, _) => ((MatchTimeline)d).Restyle()));

    public static readonly DependencyProperty PositionProperty = DependencyProperty.Register(nameof(Position), typeof(double), typeof(MatchTimeline),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty FrameMaxProperty = DependencyProperty.Register(nameof(FrameMax), typeof(int), typeof(MatchTimeline),
        new FrameworkPropertyMetadata(0));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(nameof(Command), typeof(ICommand), typeof(MatchTimeline));

    private readonly List<(MatchTimelineSegment Segment, Border Box, TextBlock Label, TextBlock Glyph, Border Bar)> _spans = [];
    private readonly List<(MatchTimelineTick Tick, Border Zone)> _ticks = [];
    private readonly Rectangle _line = new() { Width = 2, IsHitTestVisible = false };
    private readonly Border _diamond = new()
    {
        Width = 8,
        Height = 8,
        IsHitTestVisible = false,
        RenderTransformOrigin = new Point(0.5, 0.5),
        RenderTransform = new RotateTransform(45),
    };

    private Point? _pressed;
    private bool _scrubbing;

    public MatchTimeline()
    {
        Height = 40;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;
        _line.SetResourceReference(Shape.FillProperty, "Accent");
        _diamond.SetResourceReference(Border.BackgroundProperty, "Accent");
        Rebuild();
    }

    public IReadOnlyList<MatchTimelineSegment>? Segments { get => (IReadOnlyList<MatchTimelineSegment>?)GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    public IReadOnlyList<MatchTimelineTick>? Ticks { get => (IReadOnlyList<MatchTimelineTick>?)GetValue(TicksProperty); set => SetValue(TicksProperty, value); }

    /// <summary>The index of the stretch holding the step on screen, or -1.</summary>
    public int Current { get => (int)GetValue(CurrentProperty); set => SetValue(CurrentProperty, value); }

    /// <summary>The step on screen as a fraction of the width.</summary>
    public double Position { get => (double)GetValue(PositionProperty); set => SetValue(PositionProperty, value); }

    /// <summary>The last step: a drag maps the pointer to one of the FrameMax + 1 slots.</summary>
    public int FrameMax { get => (int)GetValue(FrameMaxProperty); set => SetValue(FrameMaxProperty, value); }

    /// <summary>Goes to a step (its number is the parameter).</summary>
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    private void Rebuild()
    {
        Children.Clear();
        _spans.Clear();
        _ticks.Clear();
        foreach (var segment in Segments ?? [])
        {
            var bar = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(4, 4, 0, 0), Visibility = Visibility.Collapsed };
            bar.SetResourceReference(Border.BackgroundProperty, "Accent");
            var label = new TextBlock
            {
                Text = segment.Label,
                FontSize = 10.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
            var glyph = new TextBlock
            {
                Text = "\U000F0787",
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            glyph.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            var grid = new Grid();
            grid.Children.Add(bar);
            grid.Children.Add(label);
            grid.Children.Add(glyph);
            var box = new Border { CornerRadius = new CornerRadius(4), Child = grid, ToolTip = segment.Tooltip, SnapsToDevicePixels = true };
            _spans.Add((segment, box, label, glyph, bar));
            Children.Add(box);
        }

        foreach (var tick in Ticks ?? [])
        {
            var mark = new Border { Width = 2, Height = 8, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            mark.SetResourceReference(Border.BackgroundProperty, tick.Look switch
            {
                MatchTimelineLook.Mine => "Accent",
                MatchTimelineLook.Theirs => "Accent2",
                _ => "Muted",
            });
            var zone = new Border { Width = TickZone, Height = TickHeight, Background = Brushes.Transparent, Child = mark, ToolTip = tick.Tooltip };
            _ticks.Add((tick, zone));
            Children.Add(zone);
        }

        Children.Add(_line);
        Children.Add(_diamond);
        Restyle();
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
            box.SetResourceReference(Border.BackgroundProperty, fill);
            if (edge is null)
            {
                box.BorderThickness = new Thickness(0);
            }
            else
            {
                box.BorderThickness = new Thickness(1);
                box.SetResourceReference(Border.BorderBrushProperty, edge);
            }

            label.SetResourceReference(TextBlock.ForegroundProperty, text);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, current ? text : "Muted");
            bar.Visibility = segment.Look == MatchTimelineLook.Mine && !current ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        foreach (var (segment, box, label, glyph, _) in _spans)
        {
            // Labels and the combat glyph only where they fit.
            var pixels = (segment.End - segment.Start) * width - 2;
            label.Visibility = segment.Label.Length > 0 && !segment.IsCombat && pixels >= 18 ? Visibility.Visible : Visibility.Collapsed;
            glyph.Visibility = segment.IsCombat && pixels >= 16 ? Visibility.Visible : Visibility.Collapsed;
            box.Measure(new Size(Math.Max(0, pixels), SpanHeight));
        }

        foreach (UIElement child in Children)
        {
            if (child is not Border { Child: Grid })
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
        }

        return new Size(width, 40);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = finalSize.Width;
        foreach (var (segment, box, _, _, _) in _spans)
        {
            var left = segment.Start * width + 1;
            box.Arrange(new Rect(left, SpanTop, Math.Max(1, (segment.End - segment.Start) * width - 2), SpanHeight));
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _pressed = e.GetPosition(this);
        _scrubbing = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_pressed is not { } start || !IsMouseCaptured)
        {
            return;
        }

        var point = e.GetPosition(this);
        _scrubbing |= Math.Abs(point.X - start.X) > DragSlop;
        if (_scrubbing)
        {
            Go(MatchViewModel.FrameAt(point.X / Math.Max(1, ActualWidth), FrameMax + 1));
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pressed is null)
        {
            return;
        }

        if (!_scrubbing)
        {
            Go(FrameUnder(e.GetPosition(this)));
        }

        _pressed = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _pressed = null;
        _scrubbing = false;
    }

    /// <summary>A click: the nearest tick in the tick row, else the stretch under the pointer, else the slot there.</summary>
    private int FrameUnder(Point point)
    {
        var width = Math.Max(1, ActualWidth);
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
}
