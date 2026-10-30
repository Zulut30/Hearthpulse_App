using System.Windows;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// A small line of values (e.g. combat win rate per turn): the player's in gold, an optional comparison (the
/// community's) as a dashed muted line, and a dashed midline. Missing values (null) break the line.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double?>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompareProperty = DependencyProperty.Register(
        nameof(Compare), typeof(IReadOnlyList<double?>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double?>? Values
    {
        get => (IReadOnlyList<double?>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double?>? Compare
    {
        get => (IReadOnlyList<double?>?)GetValue(CompareProperty);
        set => SetValue(CompareProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 8 || h < 8)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var values = Values ?? [];
        var compare = Compare ?? [];
        var count = Math.Max(values.Count, compare.Count);
        var mid = Math.Round(h / 2) + 0.5;
        dc.DrawLine(new Pen(Brush("Border"), 1) { DashStyle = new DashStyle([2, 3], 0) }, new Point(0, mid), new Point(w, mid));
        if (count == 0)
        {
            return;
        }

        Draw(dc, compare, count, w, h, new Pen(Brush("Muted"), 1.3) { DashStyle = new DashStyle([3, 2], 0), LineJoin = PenLineJoin.Round });
        Draw(dc, values, count, w, h, new Pen(Brush("Accent"), 1.9) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    }

    private void Draw(DrawingContext dc, IReadOnlyList<double?> values, int count, double w, double h, Pen pen)
    {
        foreach (var run in SmallChartGeometry.SparkRuns(values, count, w, h, Minimum, Maximum))
        {
            if (run.Length == 1)
            {
                dc.DrawEllipse(pen.Brush, null, new Point(run[0].X, run[0].Y), 1.6, 1.6);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(run[0].X, run[0].Y), false, false);
                ctx.PolyLineTo(run.Skip(1).Select(p => new Point(p.X, p.Y)).ToList(), true, true);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
}
