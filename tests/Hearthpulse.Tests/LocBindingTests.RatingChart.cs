using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The Avalonia rating chart draws its curve and latest-rating tag (as the WPF one) at every size it accepts, the tag
    /// kept at the plot's left edge when the plot is narrower than it; and with fewer than two points its empty text.
    /// The headless platform has no rasterizer, so what the chart draws is read back from a DrawingGroup: the glow under
    /// the line, the gradient stroke, and the PrimaryFill tag where ChartGeometry.Tag puts it over the latest point.
    /// The WPF twin is RatingChartRenderTests in Hearthpulse.Presentation.Tests.
    /// </summary>
    [Fact]
    public void The_avalonia_rating_chart_renders_at_any_width() => Headless.Run(() =>
    {
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        try
        {
            IReadOnlyList<RatingPoint> series = [new(new DateTime(2026, 10, 1), 6400), new(new DateTime(2026, 10, 2), 7250), new(new DateTime(2026, 10, 3), 7100)];
            foreach (var width in new[] { 40, 60, 100, 400 })
            {
                var chart = Arranged(series, width);
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, 120));
                bitmap.Render(chart);
                bitmap.Render(Arranged([series[0]], width));

                var drawn = Drawn(chart);
                Assert.Contains(drawn, d => d.Pen is { Thickness: 7 });
                Assert.Contains(drawn, d => d.Pen is { Thickness: 2.4, Brush: LinearGradientBrush });
                var tag = Assert.Single(drawn, d => ReferenceEquals(d.Brush, ThemeBrushes.Get("PrimaryFill"))).Geometry!.Bounds;
                var last = ChartGeometry.Rating(series, width, 120).At[^1];
                var (x, y, _, _) = ChartGeometry.Tag(last, tag.Width - (2 * ChartGeometry.TagPaddingX), tag.Height - (2 * ChartGeometry.TagPaddingY),
                    ChartGeometry.RatingLeft, ChartGeometry.RatingRight, width);
                Assert.Equal(x, tag.X, 3);
                Assert.Equal(y, tag.Y, 3);
                Assert.True(tag.Bottom < last.Y, $"{width}px: the tag sits above the latest point");
            }

            var single = Drawn(Arranged([new(new DateTime(2026, 10, 1), 6400)], 400));
            Assert.DoesNotContain(single, d => ReferenceEquals(d.Brush, ThemeBrushes.Get("PrimaryFill")) || d.Pen is { Thickness: 7 });
        }
        finally
        {
            app.Styles.Remove(theme);
        }
    });

    private static RatingChart Arranged(IReadOnlyList<RatingPoint> points, int width)
    {
        var chart = new RatingChart { Points = points };
        chart.Measure(new Size(width, 120));
        chart.Arrange(new Rect(0, 0, width, 120));
        Assert.Equal(width, chart.Bounds.Width);
        return chart;
    }

    /// <summary>The geometries <paramref name="chart"/> draws, in order (text left out).</summary>
    private static List<GeometryDrawing> Drawn(RatingChart chart)
    {
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            chart.Render(context);
        }

        var drawn = new List<GeometryDrawing>();
        void Walk(DrawingGroup g)
        {
            foreach (var child in g.Children)
            {
                if (child is GeometryDrawing geometry)
                {
                    drawn.Add(geometry);
                }
                else if (child is DrawingGroup inner)
                {
                    Walk(inner);
                }
            }
        }

        Walk(group);
        return drawn;
    }
}
