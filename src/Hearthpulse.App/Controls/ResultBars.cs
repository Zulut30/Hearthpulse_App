using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// Wins and losses per group (e.g. game length) as columns: wins at the bottom, losses stacked on top, the column's
/// height its share of the busiest group. "W–L" above each column, the group's label under it.
/// </summary>
public sealed class ResultBars : FrameworkElement
{
    public static readonly DependencyProperty WinsProperty = DependencyProperty.Register(
        nameof(Wins), typeof(IReadOnlyList<int>), typeof(ResultBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LossesProperty = DependencyProperty.Register(
        nameof(Losses), typeof(IReadOnlyList<int>), typeof(ResultBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels), typeof(IReadOnlyList<string>), typeof(ResultBars), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<int>? Wins
    {
        get => (IReadOnlyList<int>?)GetValue(WinsProperty);
        set => SetValue(WinsProperty, value);
    }

    public IReadOnlyList<int>? Losses
    {
        get => (IReadOnlyList<int>?)GetValue(LossesProperty);
        set => SetValue(LossesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => (IReadOnlyList<string>?)GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var wins = Wins ?? [];
        var losses = Losses ?? [];
        var labels = Labels ?? [];
        var count = Math.Max(wins.Count, losses.Count);
        if (w < 20 || h < 40 || count == 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var muted = Brush("Muted");
        var faint = Brush("Faint");
        var layout = SmallChartGeometry.ResultBars(wins, losses, labels.Count > 0, w, h);
        var (y, slot, bar) = (layout.Baseline, layout.Slot, layout.Bar);
        dc.DrawLine(new Pen(Brush("Border"), 1), new Point(0, Math.Round(y) + 0.5), new Point(w, Math.Round(y) + 0.5));
        for (var i = 0; i < count; i++)
        {
            var (x, won, lost, winHeight, lossHeight) = layout.Columns[i];
            if (won > 0)
            {
                dc.DrawRoundedRectangle(Brush("Positive"), null, new Rect(x, y - winHeight, bar, winHeight), 3, 3);
            }

            if (lost > 0)
            {
                dc.DrawRoundedRectangle(Brush("Negative"), null, new Rect(x, y - winHeight - lossHeight, bar, Math.Max(0, lossHeight - (won > 0 ? 1 : 0))), 3, 3);
            }

            if (won + lost > 0)
            {
                var record = Text($"{won}–{lost}", 11, muted, dpi, FontWeights.SemiBold);
                dc.DrawText(record, new Point(x + ((bar - record.Width) / 2), y - winHeight - lossHeight - record.Height - 2));
            }

            if (i < labels.Count)
            {
                var label = Text(labels[i], 11, faint, dpi);
                dc.DrawText(label, new Point((slot * i) + ((slot - label.Width) / 2), y + 4));
            }
        }
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static FormattedText Text(string text, double size, Brush brush, double dpi, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);
}
