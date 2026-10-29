using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// A small line of values (e.g. combat win rate per turn): the player's in gold, an optional comparison (the
/// community's) as a dashed muted line, and a dashed midline. Missing values (null) break the line.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double?>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double?>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<double?>?> CompareProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double?>?>(nameof(Compare));

    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 1);

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, CompareProperty, MinimumProperty, MaximumProperty);
    }

    public IReadOnlyList<double?>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public IReadOnlyList<double?>? Compare { get => GetValue(CompareProperty); set => SetValue(CompareProperty, value); }

    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 8 || h < 8)
        {
            return;
        }

        var values = Values ?? [];
        var compare = Compare ?? [];
        var count = Math.Max(values.Count, compare.Count);
        var mid = Math.Round(h / 2) + 0.5;
        context.DrawLine(new Pen(ThemeBrushes.Get("Border"), 1, new DashStyle([2, 3], 0)), new Point(0, mid), new Point(w, mid));
        if (count == 0)
        {
            return;
        }

        Draw(context, compare, count, w, h, new Pen(ThemeBrushes.Get("Muted"), 1.3, new DashStyle([3, 2], 0), lineJoin: PenLineJoin.Round));
        Draw(context, values, count, w, h, new Pen(ThemeBrushes.Get("Accent"), 1.9, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round));
    }

    private void Draw(DrawingContext context, IReadOnlyList<double?> values, int count, double w, double h, Pen pen)
    {
        foreach (var run in SmallChartGeometry.SparkRuns(values, count, w, h, Minimum, Maximum))
        {
            if (run.Length == 1)
            {
                context.DrawEllipse(pen.Brush, null, new Point(run[0].X, run[0].Y), 1.6, 1.6);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(run[0].X, run[0].Y), false);
                foreach (var p in run.Skip(1))
                {
                    ctx.LineTo(new Point(p.X, p.Y));
                }

                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, pen, geometry);
        }
    }
}
