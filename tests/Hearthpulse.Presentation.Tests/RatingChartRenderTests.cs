using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearthpulse.App.Controls;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The WPF rating chart draws at every size it accepts (40 px and up): its latest-rating tag stays at the plot's left
/// edge when the plot is narrower than the tag (<c>ChartGeometry.Tag</c>), instead of failing the render.
/// </summary>
public sealed class RatingChartRenderTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(100)]
    [InlineData(400)]
    public void A_narrow_rating_chart_still_renders(double width) => OnSta(() =>
    {
        var chart = new RatingChart
        {
            Points = [new RatingPoint(new DateTime(2026, 10, 1), 6400), new RatingPoint(new DateTime(2026, 10, 2), 7250)],
        };
        chart.Measure(new Size(width, 120));
        chart.Arrange(new Rect(0, 0, width, 120));

        var bitmap = new RenderTargetBitmap((int)width, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(chart);

        Assert.Equal(width, chart.ActualWidth);
    });

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                if (app.Resources.MergedDictionaries.Count == 0)
                {
                    app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
                }

                test();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException(failure.Message, failure);
        }
    }
}
