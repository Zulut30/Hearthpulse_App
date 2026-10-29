using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Controls;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The Avalonia rating chart draws its curve and latest-rating tag (as the WPF one) at every size it accepts, the tag
    /// kept at the plot's left edge when the plot is narrower than it; and with fewer than two points its empty text.
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
            IReadOnlyList<RatingPoint>[] series =
            [
                [new(new DateTime(2026, 10, 1), 6400), new(new DateTime(2026, 10, 2), 7250), new(new DateTime(2026, 10, 3), 7100)],
                [new(new DateTime(2026, 10, 1), 6400)],
            ];
            foreach (var points in series)
            {
                foreach (var width in new[] { 40, 60, 100, 400 })
                {
                    var chart = new RatingChart { Points = points };
                    chart.Measure(new Size(width, 120));
                    chart.Arrange(new Rect(0, 0, width, 120));
                    using var bitmap = new RenderTargetBitmap(new PixelSize(width, 120));
                    bitmap.Render(chart);
                    Assert.Equal(width, chart.Bounds.Width);
                }
            }
        }
        finally
        {
            app.Styles.Remove(theme);
        }
    });
}
