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
        const double pad = 3;
        var span = Math.Max(1e-9, Maximum - Minimum);
        Point At(int i, double v) => new(
            count == 1 ? w / 2 : pad + ((w - (2 * pad)) * i / (count - 1)),
            pad + ((h - (2 * pad)) * (1 - Math.Clamp((v - Minimum) / span, 0, 1))));

        var run = new List<Point>();
        void Flush()
        {
            if (run.Count == 1)
            {
                context.DrawEllipse(pen.Brush, null, run[0], 1.6, 1.6);
            }
            else if (run.Count > 1)
            {
                var geometry = new StreamGeometry();
                using (var ctx = geometry.Open())
                {
                    ctx.BeginFigure(run[0], false);
                    foreach (var p in run.Skip(1))
                    {
                        ctx.LineTo(p);
                    }

                    ctx.EndFigure(false);
                }

                context.DrawGeometry(null, pen, geometry);
            }

            run.Clear();
        }

        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is { } v)
            {
                run.Add(At(i, v));
            }
            else
            {
                Flush();
            }
        }

        Flush();
    }
}
