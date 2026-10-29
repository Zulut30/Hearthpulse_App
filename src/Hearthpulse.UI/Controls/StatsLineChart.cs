using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Media;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// A value per game as a gold line, in the style of <see cref="RatingChart"/>, with a card for the point under the
/// pointer: <see cref="HoverTemplate"/> is applied to the hovered point and placed beside it.
/// </summary>
public sealed class StatsLineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<IChartValue>?> PointsProperty =
        AvaloniaProperty.Register<StatsLineChart, IReadOnlyList<IChartValue>?>(nameof(Points));

    public static readonly StyledProperty<IDataTemplate?> HoverTemplateProperty =
        AvaloniaProperty.Register<StatsLineChart, IDataTemplate?>(nameof(HoverTemplate));

    public static readonly StyledProperty<int> HoverIndexProperty =
        AvaloniaProperty.Register<StatsLineChart, int>(nameof(HoverIndex), -1);

    public static readonly StyledProperty<string?> EmptyTextProperty =
        AvaloniaProperty.Register<StatsLineChart, string?>(nameof(EmptyText));

    private const double Right = ChartGeometry.LineRight;
    private const double Top = ChartGeometry.LineTop;
    private const double Bottom = ChartGeometry.LineBottom;

    private (IReadOnlyList<IChartValue>? Points, double Width, double Height, Point[] At, LinePlot Plot) _layout = (null, 0, 0, [], LinePlot.Empty);

    private readonly ContentPresenter _card = new() { IsHitTestVisible = false };

    static StatsLineChart()
    {
        AffectsRender<StatsLineChart>(PointsProperty, HoverIndexProperty, EmptyTextProperty);
    }

    public StatsLineChart()
    {
        LogicalChildren.Add(_card);
        VisualChildren.Add(_card);
    }

    public IReadOnlyList<IChartValue>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public IDataTemplate? HoverTemplate { get => GetValue(HoverTemplateProperty); set => SetValue(HoverTemplateProperty, value); }

    /// <summary>Index of the hovered point, -1 for none (settable, e.g. to render a hover card off-screen).</summary>
    public int HoverIndex { get => GetValue(HoverIndexProperty); set => SetValue(HoverIndexProperty, value); }

    /// <summary>Shown without points; null for "No games in this time frame yet." in the interface language.</summary>
    public string? EmptyText { get => GetValue(EmptyTextProperty); set => SetValue(EmptyTextProperty, value); }

    // The default empty text is drawn here, so it follows a language switch only by redrawing.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.Instance.LanguageChanged += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Loc.Instance.LanguageChanged -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PointsProperty)
        {
            HoverIndex = -1;
        }
        else if (change.Property == HoverTemplateProperty)
        {
            _card.ContentTemplate = HoverTemplate;
        }

        if (change.Property == PointsProperty || change.Property == HoverIndexProperty)
        {
            var points = Points;
            var index = HoverIndex;
            _card.Content = points is not null && index >= 0 && index < points.Count ? points[index] : null;
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _card.Measure(new Size(340, double.PositiveInfinity));
        return new Size(double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 240 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var points = Points;
        var index = HoverIndex;
        if (points is null || index < 0 || index >= points.Count || _card.Content is null)
        {
            _card.Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }

        Layout(points, finalSize.Width, finalSize.Height);
        var size = _card.DesiredSize;
        var (x, y) = ChartGeometry.Card(_layout.Plot.At[index], size.Width, size.Height, finalSize.Width, finalSize.Height);
        _card.Arrange(new Rect(new Point(x, y), size));
        return finalSize;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Points is not { Count: > 0 } points)
        {
            return;
        }

        Layout(points, Bounds.Width, Bounds.Height);
        HoverIndex = ChartGeometry.Nearest(_layout.Plot.At, e.GetPosition(this).X);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoverIndex = -1;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40)
        {
            return;
        }

        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));

        var accent = ThemeBrushes.Get("Accent");
        var points = Points;
        if (points is null || points.Count == 0)
        {
            var empty = Text(EmptyText ?? Loc.T("Stats.Chart.NoGames"), 12, ThemeBrushes.Get("Faint"));
            empty.MaxTextWidth = Math.Max(10, w - 20);
            empty.TextAlignment = TextAlignment.Center;
            context.DrawText(empty, new Point(10, (h - empty.Height) / 2));
            return;
        }

        var at = Layout(points, w, h);
        var left = _layout.Plot.Left;
        var grid = new Pen(ThemeBrushes.Get("Border"), 1, new DashStyle([3, 4], 0));
        var (lowest, highest) = (_layout.Plot.Lowest, _layout.Plot.Highest);
        DrawFrame(context, points, at, left, w, h);
        foreach (var index in points[highest].ChartValue == points[lowest].ChartValue ? new[] { highest } : new[] { highest, lowest })
        {
            var guide = points[index];
            var y = Math.Round(at[index].Y) + 0.5;
            context.DrawLine(grid, new Point(left, y), new Point(w - Right, y));
            var label = Text(guide.ChartLabel, 11, ThemeBrushes.Get("Muted"));
            context.DrawText(label, new Point(left - label.Width - 8, y - (label.Height / 2)));
        }

        var gold = accent is ISolidColorBrush solid ? solid.Color : Colors.Goldenrod;
        Color Gold(byte alpha) => Color.FromArgb(alpha, gold.R, gold.G, gold.B);
        if (at.Length > 1)
        {
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(at[0], false);
                foreach (var p in at.Skip(1))
                {
                    ctx.LineTo(p);
                }

                ctx.EndFigure(false);
            }

            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(at[0].X, h - Bottom), true);
                foreach (var p in at)
                {
                    ctx.LineTo(p);
                }

                ctx.LineTo(new Point(at[^1].X, h - Bottom));
                ctx.EndFigure(true);
            }

            var fill = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Gold(0x40), 0), new GradientStop(Gold(0x00), 1) },
            };
            context.DrawGeometry(fill, null, area);
            context.DrawGeometry(null, new Pen(new SolidColorBrush(Gold(0x2E)), 7, lineJoin: PenLineJoin.Round), line);
            context.DrawGeometry(null, new Pen(accent, 2.2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        }

        // Dots only while they stay apart; a long history is just the line.
        var bg = ThemeBrushes.Get("Bg");
        if (ChartGeometry.DotsFit(_layout.Plot.At))
        {
            foreach (var p in at)
            {
                context.DrawEllipse(bg, new Pen(accent, 1.6), p, 2.8, 2.8);
            }
        }

        var hover = HoverIndex;
        if (hover >= 0 && hover < at.Length)
        {
            var p = at[hover];
            context.DrawLine(new Pen(ThemeBrushes.Get("AccentLine"), 1), new Point(Math.Round(p.X) + 0.5, Top), new Point(Math.Round(p.X) + 0.5, h - Bottom));
            context.DrawEllipse(new SolidColorBrush(Gold(0x30)), null, p, 10, 10);
            context.DrawEllipse(bg, new Pen(accent, 2.4), p, 4.5, 4.5);
            return;
        }

        var last = at[^1];
        context.DrawEllipse(new SolidColorBrush(Gold(0x30)), null, last, 10, 10);
        context.DrawEllipse(bg, new Pen(accent, 2.4), last, 4.5, 4.5);
        var tag = Text(points[^1].ChartLabel, 12, ThemeBrushes.Get("OnAccent"), FontWeight.Bold);
        var (bx, by, bw, bh) = ChartGeometry.Tag(_layout.Plot.At[^1], tag.Width, tag.Height, left, Right, w);
        context.DrawRectangle(ThemeBrushes.Get("PrimaryFill"), null, new Rect(bx, by, bw, bh), 6, 6);
        context.DrawText(tag, new Point(bx + ChartGeometry.TagPaddingX, by + ChartGeometry.TagPaddingY));
    }

    /// <summary>
    /// Where each point sits (<see cref="ChartGeometry.Line"/>), with the plot's left edge and the lowest/highest point in
    /// <c>_layout.Plot</c>. Pointer moves, arrange and render all need it, so it is worked out once per set of points and size.
    /// </summary>
    private Point[] Layout(IReadOnlyList<IChartValue> points, double w, double h)
    {
        if (ReferenceEquals(_layout.Points, points) && _layout.Width == w && _layout.Height == h)
        {
            return _layout.At;
        }

        var plot = ChartGeometry.Line(points, w, h, label => Text(label, 11, Brushes.Transparent).Width);
        var at = Array.ConvertAll(plot.At, p => new Point(p.X, p.Y));
        _layout = (points, w, h, at, plot);
        return at;
    }

    /// <summary>
    /// The plot's frame: faint grid lines in quarters, the y axis, the x axis and the game numbers under it (first,
    /// middle and last game).
    /// </summary>
    private void DrawFrame(DrawingContext context, IReadOnlyList<IChartValue> points, Point[] at, double left, double w, double h)
    {
        var bottom = Math.Round(h - Bottom) + 0.5;
        var faint = new Pen(ThemeBrushes.Get("Border"), 1);
        using (context.PushOpacity(0.35))
        {
            for (var q = 0; q < 4; q++)
            {
                var y = Math.Round(Top + ((bottom - Top) * q / 4)) + 0.5;
                context.DrawLine(faint, new Point(left, y), new Point(w - Right, y));
            }
        }

        var axis = new Pen(ThemeBrushes.Get("Outline"), 1);
        context.DrawLine(axis, new Point(Math.Round(left) + 0.5, Top), new Point(Math.Round(left) + 0.5, bottom));
        context.DrawLine(axis, new Point(left, bottom), new Point(w - Right, bottom));
        var faintText = ThemeBrushes.Get("Faint");
        foreach (var i in ChartGeometry.Ticks(points.Count))
        {
            var x = Math.Round(at[i].X) + 0.5;
            context.DrawLine(axis, new Point(x, bottom), new Point(x, bottom + 4));
            var label = Text((i + 1).ToString("N0", CultureInfo.CurrentCulture), 10.5, faintText);
            context.DrawText(label, new Point(ChartGeometry.TickLabelX(x, label.Width, left, w), bottom + 6));
        }
    }

    private FormattedText Text(string text, double size, IBrush brush, FontWeight weight = FontWeight.Normal)
    {
        var family = this.TryFindResource("UiFont", ActualThemeVariant, out var font) && font is FontFamily f ? f : FontFamily.Default;
        return new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), size, brush);
    }
}
