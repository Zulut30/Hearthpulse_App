using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// Wins and losses per group (e.g. game length) as columns: wins at the bottom, losses stacked on top, the column's
/// height its share of the busiest group. "W–L" above each column, the group's label under it.
/// </summary>
public sealed class ResultBars : Control
{
    public static readonly StyledProperty<IReadOnlyList<int>?> WinsProperty =
        AvaloniaProperty.Register<ResultBars, IReadOnlyList<int>?>(nameof(Wins));

    public static readonly StyledProperty<IReadOnlyList<int>?> LossesProperty =
        AvaloniaProperty.Register<ResultBars, IReadOnlyList<int>?>(nameof(Losses));

    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<ResultBars, IReadOnlyList<string>?>(nameof(Labels));

    static ResultBars()
    {
        AffectsRender<ResultBars>(WinsProperty, LossesProperty, LabelsProperty);
    }

    public IReadOnlyList<int>? Wins { get => GetValue(WinsProperty); set => SetValue(WinsProperty, value); }

    public IReadOnlyList<int>? Losses { get => GetValue(LossesProperty); set => SetValue(LossesProperty, value); }

    public IReadOnlyList<string>? Labels { get => GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var wins = Wins ?? [];
        var losses = Losses ?? [];
        var labels = Labels ?? [];
        var count = Math.Max(wins.Count, losses.Count);
        if (w < 20 || h < 40 || count == 0)
        {
            return;
        }

        var family = this.TryFindResource("UiFont", ActualThemeVariant, out var font) && font is FontFamily f ? f : FontFamily.Default;
        FormattedText Text(string text, IBrush brush, FontWeight weight) =>
            new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, weight), 11, brush);

        var layout = SmallChartGeometry.ResultBars(wins, losses, labels.Count > 0, w, h);
        var (y, slot, bar) = (layout.Baseline, layout.Slot, layout.Bar);
        var baseline = Math.Round(y) + 0.5;
        context.DrawLine(new Pen(ThemeBrushes.Get("Border"), 1), new Point(0, baseline), new Point(w, baseline));
        for (var i = 0; i < count; i++)
        {
            var (x, won, lost, winHeight, lossHeight) = layout.Columns[i];
            if (won > 0)
            {
                context.DrawRectangle(ThemeBrushes.Get("Positive"), null, new Rect(x, y - winHeight, bar, winHeight), 3, 3);
            }

            if (lost > 0)
            {
                context.DrawRectangle(ThemeBrushes.Get("Negative"), null,
                    new Rect(x, y - winHeight - lossHeight, bar, Math.Max(0, lossHeight - (won > 0 ? 1 : 0))), 3, 3);
            }

            if (won + lost > 0)
            {
                var record = Text($"{won}–{lost}", ThemeBrushes.Get("Muted"), FontWeight.SemiBold);
                context.DrawText(record, new Point(x + ((bar - record.Width) / 2), y - winHeight - lossHeight - record.Height - 2));
            }

            if (i < labels.Count)
            {
                var label = Text(labels[i], ThemeBrushes.Get("Faint"), FontWeight.Normal);
                context.DrawText(label, new Point((slot * i) + ((slot - label.Width) / 2), y + 4));
            }
        }
    }
}
