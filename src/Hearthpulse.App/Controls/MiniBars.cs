using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// A small bar chart of shares (e.g. games per final place): gold bars for the player, a muted tick per bar for the
/// comparison (the community's share), an optional marker at a fractional position (e.g. the average place, 1-based)
/// and optional 1..N labels under the bars.
/// </summary>
public sealed class MiniBars : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(MiniBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CompareProperty = DependencyProperty.Register(
        nameof(Compare), typeof(IReadOnlyList<double>), typeof(MiniBars),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MarkerProperty = DependencyProperty.Register(
        nameof(Marker), typeof(double), typeof(MiniBars), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(MiniBars), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IReadOnlyList<double>? Compare
    {
        get => (IReadOnlyList<double>?)GetValue(CompareProperty);
        set => SetValue(CompareProperty, value);
    }

    /// <summary>1-based position of a marker line (NaN for none), e.g. 3.4 sits between bars 3 and 4.</summary>
    public double Marker
    {
        get => (double)GetValue(MarkerProperty);
        set => SetValue(MarkerProperty, value);
    }

    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var values = Values ?? [];
        var compare = Compare ?? [];
        var count = Math.Max(values.Count, compare.Count);
        if (w < 8 || h < 8 || count == 0)
        {
            return;
        }

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var layout = SmallChartGeometry.MiniBars(values, compare, w, h, ShowLabels, Marker);
        var plot = layout.Plot;
        var accent = Brush("Accent");
        var soft = Brush("AccentContainer");
        var tick = new Pen(Brush("Muted"), 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        for (var i = 0; i < count; i++)
        {
            var (x, bw, bh, tickY) = layout.Bars[i];
            dc.DrawRoundedRectangle(soft, null, new Rect(x, 0, bw, plot), 2, 2);
            if (bh > 0)
            {
                dc.DrawRoundedRectangle(accent, null, new Rect(x, plot - bh, bw, bh), 2, 2);
            }

            if (tickY is { } y)
            {
                dc.DrawLine(tick, new Point(x + 1, y), new Point(x + bw - 1, y));
            }

            if (ShowLabels)
            {
                var label = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    10.5, Brush("Muted"), dpi);
                dc.DrawText(label, new Point(x + ((bw - label.Width) / 2), plot + 2));
            }
        }

        if (layout.MarkerX is { } mx)
        {
            var pen = new Pen(Brush("Text"), 1.6);
            dc.DrawLine(pen, new Point(mx, -2), new Point(mx, plot));
            dc.DrawEllipse(Brush("Text"), null, new Point(mx, -2), 2.6, 2.6);
        }
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
}
