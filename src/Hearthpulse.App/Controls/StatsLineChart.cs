using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>A point of <see cref="StatsLineChart"/>: its value and the text shown for it (axis guides, latest tag).</summary>
public interface IChartValue
{
    double ChartValue { get; }

    string ChartLabel { get; }
}

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
        new FrameworkPropertyMetadata("No games in this time frame yet.", FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Right = 14;
    private const double Top = 16;
    private const double Bottom = 14;

    private (IReadOnlyList<IChartValue>? Points, double Width, double Height, Point[] At, double Left, int Lowest, int Highest) _layout = (null, 0, 0, [], 10, 0, 0);

    private readonly ContentPresenter _card = new() { IsHitTestVisible = false };

    public StatsLineChart()
    {
        AddLogicalChild(_card);
        AddVisualChild(_card);
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

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
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

        var at = Layout(points, finalSize.Width, finalSize.Height)[index];
        var size = _card.DesiredSize;
        var x = at.X + 18 + size.Width <= finalSize.Width ? at.X + 18 : Math.Max(0, at.X - 18 - size.Width);
        var y = Math.Clamp(at.Y - (size.Height / 2), 0, Math.Max(0, finalSize.Height - size.Height));
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

        var x = e.GetPosition(this).X;
        var layout = Layout(points, ActualWidth, ActualHeight);
        var nearest = 0;
        for (var i = 1; i < layout.Length; i++)
        {
            if (Math.Abs(layout[i].X - x) < Math.Abs(layout[nearest].X - x))
            {
                nearest = i;
            }
        }

        HoverIndex = nearest;
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
            var empty = Text(EmptyText, 12, Brush("Faint"), dpi);
            empty.MaxTextWidth = Math.Max(10, w - 20);
            empty.TextAlignment = TextAlignment.Center;
            dc.DrawText(empty, new Point(10, (h - empty.Height) / 2));
            return;
        }

        var at = Layout(points, w, h);
        var left = _layout.Left;
        var grid = new Pen(Brush("Border"), 1) { DashStyle = new DashStyle([3, 4], 0) };
        var (lowest, highest) = (_layout.Lowest, _layout.Highest);
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
        if (at.Length <= 1 || (at[^1].X - at[0].X) / (at.Length - 1) >= 9)
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
        var box = new Rect(0, 0, tag.Width + 14, tag.Height + 4);
        box.X = Math.Clamp(last.X - (box.Width / 2), left, Math.Max(left, w - Right - box.Width));
        box.Y = last.Y - box.Height - 14 < 0 ? last.Y + 14 : last.Y - box.Height - 14;
        dc.DrawRoundedRectangle(Brush("PrimaryFill"), null, box, 6, 6);
        dc.DrawText(tag, new Point(box.X + 7, box.Y + 2));
    }

    private void SyncCard()
    {
        var points = Points;
        var index = HoverIndex;
        _card.Content = points is not null && index >= 0 && index < points.Count ? points[index] : null;
        InvalidateMeasure();
    }

    /// <summary>
    /// Where each point sits, plus the plot's left edge and the lowest/highest point. Mouse moves, arrange and render
    /// all need it, so it is worked out once per set of points and size.
    /// </summary>
    private Point[] Layout(IReadOnlyList<IChartValue> points, double w, double h)
    {
        if (ReferenceEquals(_layout.Points, points) && _layout.Width == w && _layout.Height == h)
        {
            return _layout.At;
        }

        int lowest = 0, highest = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].ChartValue < points[lowest].ChartValue) lowest = i;
            if (points[i].ChartValue > points[highest].ChartValue) highest = i;
        }

        // Room for the widest axis label (the guides are at the lowest and highest point).
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var left = points.Count == 0
            ? 10
            : Math.Ceiling(new[] { lowest, highest }.Max(i => Text(points[i].ChartLabel, 11, Brushes.Transparent, dpi).Width)) + 16;
        var min = points.Count == 0 ? 0 : points[lowest].ChartValue;
        var max = points.Count == 0 ? 0 : points[highest].ChartValue;
        var pad = Math.Max((max - min) * 0.12, Math.Max(1, Math.Abs(max) * 0.004));
        var lo = min - pad;
        var hi = max + pad;
        var at = new Point[points.Count];
        for (var i = 0; i < at.Length; i++)
        {
            var x = points.Count == 1 ? (left + w - Right) / 2 : left + ((w - left - Right) * i / (points.Count - 1));
            at[i] = new Point(x, Top + ((h - Top - Bottom) * (1 - ((points[i].ChartValue - lo) / (hi - lo)))));
        }

        _layout = (points, w, h, at, left, lowest, highest);
        return at;
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private FormattedText Text(string text, double size, Brush brush, double dpi, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);
}
