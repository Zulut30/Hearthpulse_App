using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// A small bar chart of shares (e.g. games per final place): gold bars for the player, a muted tick per bar for the
/// comparison (the community's share), an optional marker at a fractional position (e.g. the average place, 1-based)
/// and optional 1..N labels under the bars.
/// </summary>
public sealed class MiniBars : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<MiniBars, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<double>?> CompareProperty =
        AvaloniaProperty.Register<MiniBars, IReadOnlyList<double>?>(nameof(Compare));

    public static readonly StyledProperty<double> MarkerProperty = AvaloniaProperty.Register<MiniBars, double>(nameof(Marker), double.NaN);

    public static readonly StyledProperty<bool> ShowLabelsProperty = AvaloniaProperty.Register<MiniBars, bool>(nameof(ShowLabels));

    static MiniBars()
    {
        AffectsRender<MiniBars>(ValuesProperty, CompareProperty, MarkerProperty, ShowLabelsProperty);
    }

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    public IReadOnlyList<double>? Compare { get => GetValue(CompareProperty); set => SetValue(CompareProperty, value); }

    /// <summary>1-based position of a marker line (NaN for none), e.g. 3.4 sits between bars 3 and 4.</summary>
    public double Marker { get => GetValue(MarkerProperty); set => SetValue(MarkerProperty, value); }

    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var values = Values ?? [];
        var compare = Compare ?? [];
        var count = Math.Max(values.Count, compare.Count);
        if (w < 8 || h < 8 || count == 0)
        {
            return;
        }

        var layout = SmallChartGeometry.MiniBars(values, compare, w, h, ShowLabels, Marker);
        var plot = layout.Plot;
        var accent = ThemeBrushes.Get("Accent");
        var soft = ThemeBrushes.Get("AccentContainer");
        var tick = new Pen(ThemeBrushes.Get("Muted"), 1.6, lineCap: PenLineCap.Round);
        var family = this.TryFindResource("UiFont", ActualThemeVariant, out var font) && font is FontFamily f ? f : FontFamily.Default;
        for (var i = 0; i < count; i++)
        {
            var (x, bw, bh, tickY) = layout.Bars[i];
            context.DrawRectangle(soft, null, new Rect(x, 0, bw, plot), 2, 2);
            if (bh > 0)
            {
                context.DrawRectangle(accent, null, new Rect(x, plot - bh, bw, bh), 2, 2);
            }

            if (tickY is { } y)
            {
                context.DrawLine(tick, new Point(x + 1, y), new Point(x + bw - 1, y));
            }

            if (ShowLabels)
            {
                var label = new FormattedText((i + 1).ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    new Typeface(family, FontStyle.Normal, FontWeight.SemiBold), 10.5, ThemeBrushes.Get("Muted"));
                context.DrawText(label, new Point(x + ((bw - label.Width) / 2), plot + 2));
            }
        }

        if (layout.MarkerX is { } mx)
        {
            var text = ThemeBrushes.Get("Text");
            context.DrawLine(new Pen(text, 1.6), new Point(mx, -2), new Point(mx, plot));
            context.DrawEllipse(text, null, new Point(mx, -2), 2.6, 2.6);
        }
    }
}
