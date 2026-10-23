using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Hearthlens.UI.Controls;

/// <summary>
/// Material 3 Expressive "cookie": a circle with soft scalloped lobes. Used behind icons and as decorative
/// background shapes.
/// </summary>
public sealed class Scallop : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty = AvaloniaProperty.Register<Scallop, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<int> LobesProperty = AvaloniaProperty.Register<Scallop, int>(nameof(Lobes), 9);

    public static readonly StyledProperty<double> DepthProperty = AvaloniaProperty.Register<Scallop, double>(nameof(Depth), 0.07);

    public static readonly StyledProperty<double> AngleProperty = AvaloniaProperty.Register<Scallop, double>(nameof(Angle));

    static Scallop()
    {
        AffectsRender<Scallop>(FillProperty, LobesProperty, DepthProperty, AngleProperty);
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Number of lobes around the edge (9 = cookie, 4 = clover, 12 = sunny).</summary>
    public int Lobes { get => GetValue(LobesProperty); set => SetValue(LobesProperty, value); }

    /// <summary>How deep the dents between lobes are, as a fraction of the radius.</summary>
    public double Depth { get => GetValue(DepthProperty); set => SetValue(DepthProperty, value); }

    /// <summary>Rotation in degrees.</summary>
    public double Angle { get => GetValue(AngleProperty); set => SetValue(AngleProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Fill is null || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        context.DrawGeometry(Fill, null, Build(Bounds.Width, Bounds.Height, Math.Max(2, Lobes), Math.Clamp(Depth, 0, 0.45), Angle));
    }

    /// <summary>Closed outline r(θ) = R·(1 − d·(1 − cos nθ)/2), sampled finely enough to look smooth.</summary>
    public static Geometry Build(double width, double height, int lobes, double depth, double angle)
    {
        var rx = width / 2;
        var ry = height / 2;
        var steps = lobes * 18;
        var start = angle * Math.PI / 180;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i <= steps; i++)
            {
                var theta = start + (2 * Math.PI * i / steps);
                var r = 1 - (depth * (1 - Math.Cos(lobes * (theta - start))) / 2);
                var point = new Point(rx + (rx * r * Math.Cos(theta)), ry + (ry * r * Math.Sin(theta)));
                if (i == 0)
                {
                    ctx.BeginFigure(point, true);
                }
                else
                {
                    ctx.LineTo(point);
                }
            }

            ctx.EndFigure(true);
        }

        return geometry;
    }
}
