using System.Windows;
using System.Windows.Media;

namespace Hearthlens.App.Controls;

/// <summary>
/// Material 3 Expressive "cookie": a circle with soft scalloped lobes. Used behind icons and as
/// decorative background shapes.
/// </summary>
public sealed class Scallop : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Scallop),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LobesProperty = DependencyProperty.Register(
        nameof(Lobes), typeof(int), typeof(Scallop),
        new FrameworkPropertyMetadata(9, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DepthProperty = DependencyProperty.Register(
        nameof(Depth), typeof(double), typeof(Scallop),
        new FrameworkPropertyMetadata(0.07, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(
        nameof(Angle), typeof(double), typeof(Scallop),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Number of lobes around the edge (9 = cookie, 4 = clover, 12 = sunny).</summary>
    public int Lobes { get => (int)GetValue(LobesProperty); set => SetValue(LobesProperty, value); }

    /// <summary>How deep the dents between lobes are, as a fraction of the radius.</summary>
    public double Depth { get => (double)GetValue(DepthProperty); set => SetValue(DepthProperty, value); }

    /// <summary>Rotation in degrees.</summary>
    public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        if (Fill is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        dc.DrawGeometry(Fill, null, Build(ActualWidth, ActualHeight, Math.Max(2, Lobes), Math.Clamp(Depth, 0, 0.45), Angle));
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
                    ctx.BeginFigure(point, true, true);
                }
                else
                {
                    ctx.LineTo(point, true, true);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
