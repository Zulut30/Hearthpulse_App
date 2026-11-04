using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// A rule that fills its width: a hairline that fades out to the right. With <see cref="Pulse"/> it starts with a
/// heartbeat in the flame of the logo (the page rule under titles). (The name is kept from the old wavy divider so
/// existing XAML keeps working.)
/// </summary>
public sealed class Wave : Control
{
    /// <summary>The beat's corners from its start: x and y offsets from the line.</summary>
    private static readonly (double X, double Y)[] Beat = [(0, 0), (12, 0), (15, -4), (19, 6), (23, -7), (26, 0), (30, 0)];

    private const double BeatHeight = 16;

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Wave, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<bool> PulseProperty = AvaloniaProperty.Register<Wave, bool>(nameof(Pulse));

    static Wave()
    {
        AffectsRender<Wave>(StrokeProperty, PulseProperty);
        AffectsMeasure<Wave>(PulseProperty);
    }

    /// <summary>Colour of the hairline at its left end (defaults to the theme's GoldTrim, FlameLine after a beat).</summary>
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Start with a heartbeat in the theme's flame.</summary>
    public bool Pulse { get => GetValue(PulseProperty); set => SetValue(PulseProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 40 : availableSize.Width, Pulse ? BeatHeight : 3);

    public override void Render(DrawingContext context)
    {
        var y = Bounds.Height / 2;
        if (!Pulse)
        {
            Hairline(context, Stroke ?? ThemeBrushes.Get("GoldTrim"), 0, Bounds.Width, y);
            return;
        }

        if (Bounds.Width < Beat[^1].X + 8)
        {
            return;
        }

        var beat = new StreamGeometry();
        using (var ctx = beat.Open())
        {
            ctx.BeginFigure(new Point(Beat[0].X + 0.5, y + Beat[0].Y), false);
            foreach (var (x, dy) in Beat.Skip(1))
            {
                ctx.LineTo(new Point(x + 0.5, y + dy));
            }

            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(ThemeBrushes.Get("FlameRule"), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), beat);
        Hairline(context, Stroke ?? ThemeBrushes.Get("FlameLine"), Beat[^1].X, Bounds.Width, y);
    }

    /// <summary>A crisp 1 px line from <paramref name="from"/> to <paramref name="to"/> on <paramref name="y"/>, fading to transparent.</summary>
    public static void Hairline(DrawingContext context, IBrush stroke, double from, double to, double y)
    {
        if (to - from < 8)
        {
            return;
        }

        var line = Math.Floor(y) + 0.5;
        var fade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
            },
        };
        using (context.PushOpacityMask(fade, new Rect(from, line - 1, to - from, 2)))
        {
            context.DrawLine(new Pen(stroke, 1), new Point(from, line), new Point(to, line));
        }
    }
}
