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

        const double top = 18;
        var bottom = labels.Count > 0 ? 20.0 : 4.0;
        var plot = Math.Max(1, h - top - bottom);
        var slot = w / count;
        var bar = Math.Min(44, slot * 0.56);
        var max = Math.Max(1, Enumerable.Range(0, count).Max(i => At(wins, i) + At(losses, i)));
        var baseline = Math.Round(h - bottom) + 0.5;
        context.DrawLine(new Pen(ThemeBrushes.Get("Border"), 1), new Point(0, baseline), new Point(w, baseline));
        for (var i = 0; i < count; i++)
        {
            var (won, lost) = (At(wins, i), At(losses, i));
            var x = (slot * i) + ((slot - bar) / 2);
            var y = h - bottom;
            var winHeight = plot * won / max;
            var lossHeight = plot * lost / max;
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
                context.DrawText(label, new Point((slot * i) + ((slot - label.Width) / 2), h - bottom + 4));
            }
        }
    }

    private static int At(IReadOnlyList<int> list, int index) => index < list.Count ? list[index] : 0;
}
