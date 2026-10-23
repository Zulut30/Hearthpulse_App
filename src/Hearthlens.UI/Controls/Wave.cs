using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthlens.UI.Infrastructure;

namespace Hearthlens.UI.Controls;

/// <summary>
/// A gold rule that fills its width: a small diamond at the left end and a hairline that fades out to the right.
/// (The name is kept from the old wavy divider so existing XAML keeps working.)
/// </summary>
public sealed class Wave : Control
{
    /// <summary>Edge of the diamond before rotation; the rotated square spans about 7 px.</summary>
    private const double DiamondSide = 5;

    private static readonly double DiamondHalf = DiamondSide / Math.Sqrt(2);

    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<Wave, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> AmplitudeProperty = AvaloniaProperty.Register<Wave, double>(nameof(Amplitude), 2.2);

    public static readonly StyledProperty<double> WavelengthProperty = AvaloniaProperty.Register<Wave, double>(nameof(Wavelength), 11.0);

    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<Wave, double>(nameof(Thickness), 1.5);

    static Wave()
    {
        AffectsRender<Wave>(StrokeProperty, AmplitudeProperty, WavelengthProperty, ThicknessProperty);
        AffectsMeasure<Wave>(AmplitudeProperty, ThicknessProperty);
    }

    /// <summary>Colour of the rule at its left end (defaults to the theme's GoldTrim).</summary>
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Kept for compatibility; the rule is straight.</summary>
    public double Amplitude { get => GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }

    /// <summary>Kept for compatibility; the rule is straight.</summary>
    public double Wavelength { get => GetValue(WavelengthProperty); set => SetValue(WavelengthProperty, value); }

    /// <summary>Kept for compatibility; the rule is always a 1 px hairline.</summary>
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 40 : availableSize.Width,
            Math.Max((Amplitude * 2) + Thickness + 1, Math.Ceiling(DiamondHalf * 2) + 1));

    public override void Render(DrawingContext context)
    {
        Draw(context, Stroke ?? ThemeBrushes.Get("GoldTrim"), ThemeBrushes.Get("Accent"), 0, Bounds.Width, Bounds.Height / 2);
    }

    /// <summary>Draws the rule from <paramref name="from"/> to <paramref name="to"/> centred on <paramref name="y"/>.</summary>
    /// <remarks>Signature kept from the wavy version; amplitude, wavelength and thickness are ignored.</remarks>
    public static void Draw(DrawingContext context, IBrush stroke, double from, double to, double y, double amplitude, double wavelength, double thickness) =>
        Draw(context, stroke, stroke, from, to, y);

    /// <summary>Diamond in <paramref name="diamond"/> at the left end, then a hairline in <paramref name="stroke"/> fading to transparent.</summary>
    public static void Draw(DrawingContext context, IBrush stroke, IBrush diamond, double from, double to, double y)
    {
        var length = to - from;
        if (length < (DiamondHalf * 2) + 8)
        {
            return;
        }

        var cx = from + DiamondHalf + 0.5;
        var gem = new StreamGeometry();
        using (var ctx = gem.Open())
        {
            ctx.BeginFigure(new Point(cx, y - DiamondHalf), true);
            ctx.LineTo(new Point(cx + DiamondHalf, y));
            ctx.LineTo(new Point(cx, y + DiamondHalf));
            ctx.LineTo(new Point(cx - DiamondHalf, y));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(diamond, null, gem);

        // A crisp 1 px line on the pixel grid, faded with an opacity mask so any brush works.
        var start = cx + DiamondHalf + 3;
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
        using (context.PushOpacityMask(fade, new Rect(start, line - 1, to - start, 2)))
        {
            context.DrawLine(new Pen(stroke, 1), new Point(start, line), new Point(to, line));
        }
    }
}
