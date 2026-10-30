using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Controls;

/// <summary>
/// A value per game as a gold line, in the style of <see cref="RatingChart"/>, with a card for the point under the
/// mouse: <see cref="HoverTemplate"/> is applied to the hovered point and placed beside it.
/// </summary>
public sealed class StatsLineChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<IChartValue>), typeof(StatsLineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((StatsLineChart)d).HoverIndex = -1));

    public static readonly DependencyProperty HoverTemplateProperty = DependencyProperty.Register(
        nameof(HoverTemplate), typeof(DataTemplate), typeof(StatsLineChart),
        new FrameworkPropertyMetadata(null, (d, e) => ((StatsLineChart)d)._card.ContentTemplate = (DataTemplate?)e.NewValue));

    public static readonly DependencyProperty HoverIndexProperty = DependencyProperty.Register(
        nameof(HoverIndex), typeof(int), typeof(StatsLineChart),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsArrange,
            (d, _) => ((StatsLineChart)d).SyncCard()));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(StatsLineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Right = ChartGeometry.LineRight;
    private const double Top = ChartGeometry.LineTop;
    private const double Bottom = ChartGeometry.LineBottom;

    private (IReadOnlyList<IChartValue>? Points, double Width, double Height, Point[] At, LinePlot Plot) _layout = (null, 0, 0, [], LinePlot.Empty);

    private readonly ContentPresenter _card = new() { IsHitTestVisible = false };

    public StatsLineChart()
    {
        AddLogicalChild(_card);
        AddVisualChild(_card);

        // The default empty text is drawn here, so it follows a language switch only by redrawing. WPF may raise Loaded
        // again without an Unloaded in between: one subscription at most, or the app-wide event keeps a closed window.
        Loaded += (_, _) =>
        {
            Loc.Instance.LanguageChanged -= InvalidateVisual;
            Loc.Instance.LanguageChanged += InvalidateVisual;
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= InvalidateVisual;
    }

    public IReadOnlyList<IChartValue>? Points
    {
        get => (IReadOnlyList<IChartValue>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public DataTemplate? HoverTemplate
    {
        get => (DataTemplate?)GetValue(HoverTemplateProperty);
        set => SetValue(HoverTemplateProperty, value);
    }

    /// <summary>Index of the hovered point, -1 for none (settable, e.g. to render a hover card off-screen).</summary>
    public int HoverIndex
    {
        get => (int)GetValue(HoverIndexProperty);
        set => SetValue(HoverIndexProperty, value);
    }

    /// <summary>Shown without points; null for "No games in this time frame yet." in the interface language.</summary>
    public string? EmptyText
    {
        get => (string?)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override IEnumerator LogicalChildren => new[] { _card }.GetEnumerator();

    protected override Visual GetVisualChild(int index) => _card;

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

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Points is not { Count: > 0 } points)
        {
            return;
        }

        Layout(points, ActualWidth, ActualHeight);
        HoverIndex = ChartGeometry.Nearest(_layout.Plot.At, e.GetPosition(this).X);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverIndex = -1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40)
        {
            return;
        }

        var accent = Brush("Accent");
        var muted = Brush("Muted");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var points = Points;
        if (points is null || points.Count == 0)
        {
            var empty = Text(EmptyText ?? Loc.T("Stats.Chart.NoGames"), 12, Brush("Faint"), dpi);
            empty.MaxTextWidth = Math.Max(10, w - 20);
            empty.TextAlignment = TextAlignment.Center;
            dc.DrawText(empty, new Point(10, (h - empty.Height) / 2));
            return;
        }

        var at = Layout(points, w, h);
        var left = _layout.Plot.Left;
        var grid = new Pen(Brush("Border"), 1) { DashStyle = new DashStyle([3, 4], 0) };
        var (lowest, highest) = (_layout.Plot.Lowest, _layout.Plot.Highest);
        DrawFrame(dc, points, at, left, w, h, dpi);
        foreach (var index in points[highest].ChartValue == points[lowest].ChartValue ? new[] { highest } : new[] { highest, lowest })
        {
            var guide = points[index];
            var y = Math.Round(at[index].Y) + 0.5;
            dc.DrawLine(grid, new Point(left, y), new Point(w - Right, y));
            var label = Text(guide.ChartLabel, 11, muted, dpi);
            dc.DrawText(label, new Point(left - label.Width - 8, y - (label.Height / 2)));
        }

        var gold = accent is SolidColorBrush solid ? solid.Color : Colors.Goldenrod;
        Color Gold(byte alpha) => Color.FromArgb(alpha, gold.R, gold.G, gold.B);
        if (at.Length > 1)
        {
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(at[0], false, false);
                ctx.PolyLineTo(at[1..], true, true);
            }

            line.Freeze();
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(at[0].X, h - Bottom), true, true);
                ctx.LineTo(at[0], false, true);
                ctx.PolyLineTo(at[1..], false, true);
                ctx.LineTo(new Point(at[^1].X, h - Bottom), false, false);
            }

            area.Freeze();
            dc.DrawGeometry(new LinearGradientBrush(Gold(0x40), Gold(0x00), 90), null, area);
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Gold(0x2E)), 7) { LineJoin = PenLineJoin.Round }, line);
            dc.DrawGeometry(null, new Pen(accent, 2.2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        }

        // Dots only while they stay apart; a long history is just the line.
        var bg = Brush("Bg");
        if (ChartGeometry.DotsFit(_layout.Plot.At))
        {
            foreach (var p in at)
            {
                dc.DrawEllipse(bg, new Pen(accent, 1.6), p, 2.8, 2.8);
            }
        }

        var hover = HoverIndex;
        if (hover >= 0 && hover < at.Length)
        {
            var p = at[hover];
            dc.DrawLine(new Pen(Brush("AccentLine"), 1), new Point(Math.Round(p.X) + 0.5, Top), new Point(Math.Round(p.X) + 0.5, h - Bottom));
            dc.DrawEllipse(new SolidColorBrush(Gold(0x30)), null, p, 10, 10);
            dc.DrawEllipse(bg, new Pen(accent, 2.4), p, 4.5, 4.5);
            return;
        }

        var last = at[^1];
        dc.DrawEllipse(new SolidColorBrush(Gold(0x30)), null, last, 10, 10);
        dc.DrawEllipse(bg, new Pen(accent, 2.4), last, 4.5, 4.5);
        var tag = Text(points[^1].ChartLabel, 12, Brush("OnAccent"), dpi, FontWeights.Bold);
        var (bx, by, bw, bh) = ChartGeometry.Tag(_layout.Plot.At[^1], tag.Width, tag.Height, left, Right, w);
        dc.DrawRoundedRectangle(Brush("PrimaryFill"), null, new Rect(bx, by, bw, bh), 6, 6);
        dc.DrawText(tag, new Point(bx + ChartGeometry.TagPaddingX, by + ChartGeometry.TagPaddingY));
    }

    private void SyncCard()
    {
        var points = Points;
        var index = HoverIndex;
        _card.Content = points is not null && index >= 0 && index < points.Count ? points[index] : null;
        InvalidateMeasure();
    }

    /// <summary>
    /// Where each point sits (<see cref="ChartGeometry.Line"/>), with the plot's left edge and the lowest/highest point in
    /// <c>_layout.Plot</c>. Mouse moves, arrange and render all need it, so it is worked out once per set of points and size.
    /// </summary>
    private Point[] Layout(IReadOnlyList<IChartValue> points, double w, double h)
    {
        if (ReferenceEquals(_layout.Points, points) && _layout.Width == w && _layout.Height == h)
        {
            return _layout.At;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var plot = ChartGeometry.Line(points, w, h, label => Text(label, 11, Brushes.Transparent, dpi).Width);
        var at = Array.ConvertAll(plot.At, p => new Point(p.X, p.Y));
        _layout = (points, w, h, at, plot);
        return at;
    }

    /// <summary>
    /// The plot's frame: faint grid lines in quarters, the y axis, the x axis and the game numbers under it (first,
    /// middle and last game).
    /// </summary>
    private void DrawFrame(DrawingContext dc, IReadOnlyList<IChartValue> points, Point[] at, double left, double w, double h, double dpi)
    {
        var bottom = Math.Round(h - Bottom) + 0.5;
        var faint = new Pen(Brush("Border"), 1);
        dc.PushOpacity(0.35);
        for (var q = 0; q < 4; q++)
        {
            var y = Math.Round(Top + ((bottom - Top) * q / 4)) + 0.5;
            dc.DrawLine(faint, new Point(left, y), new Point(w - Right, y));
        }

        dc.Pop();
        var axis = new Pen(Brush("Outline"), 1);
        dc.DrawLine(axis, new Point(Math.Round(left) + 0.5, Top), new Point(Math.Round(left) + 0.5, bottom));
        dc.DrawLine(axis, new Point(left, bottom), new Point(w - Right, bottom));
        var faintText = Brush("Faint");
        foreach (var i in ChartGeometry.Ticks(points.Count))
        {
            var x = Math.Round(at[i].X) + 0.5;
            dc.DrawLine(axis, new Point(x, bottom), new Point(x, bottom + 4));
            var label = Text((i + 1).ToString("N0", CultureInfo.CurrentCulture), 10.5, faintText, dpi);
            dc.DrawText(label, new Point(ChartGeometry.TickLabelX(x, label.Width, left, w), bottom + 6));
        }
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private FormattedText Text(string text, double size, Brush brush, double dpi, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);
}
