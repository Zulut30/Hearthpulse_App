using System.Windows;
using System.Windows.Media;

namespace Hearthlens.App.Controls;

/// <summary>
/// A gold rule that fills its width: a small diamond at the left end and a hairline that fades out to the right.
/// (The name is kept from the old wavy divider so existing XAML keeps working.)
/// </summary>
public sealed class Wave : FrameworkElement
{
    /// <summary>Edge of the diamond before rotation; the rotated square spans about 7 px.</summary>
    private const double DiamondSide = 5;

    private static readonly double DiamondHalf = DiamondSide / Math.Sqrt(2);

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Wave),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AmplitudeProperty = DependencyProperty.Register(
        nameof(Amplitude), typeof(double), typeof(Wave),
        new FrameworkPropertyMetadata(2.2, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WavelengthProperty = DependencyProperty.Register(
        nameof(Wavelength), typeof(double), typeof(Wave),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Wave),
        new FrameworkPropertyMetadata(1.5, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colour of the rule at its left end (defaults to the theme's GoldTrim).</summary>
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Kept for compatibility; the rule is straight.</summary>
    public double Amplitude { get => (double)GetValue(AmplitudeProperty); set => SetValue(AmplitudeProperty, value); }

    /// <summary>Kept for compatibility; the rule is straight.</summary>
    public double Wavelength { get => (double)GetValue(WavelengthProperty); set => SetValue(WavelengthProperty, value); }

    /// <summary>Kept for compatibility; the rule is always a 1 px hairline.</summary>
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 40 : availableSize.Width, Math.Max((Amplitude * 2) + Thickness + 1, Math.Ceiling(DiamondHalf * 2) + 1));

    protected override void OnRender(DrawingContext dc)
    {
        var stroke = Stroke ?? TryFindResource("GoldTrim") as Brush;
        if (stroke is not null)
        {
            Draw(dc, stroke, TryFindResource("Accent") as Brush ?? stroke, 0, ActualWidth, ActualHeight / 2);
        }
    }

    /// <summary>Draws the rule from <paramref name="from"/> to <paramref name="to"/> centred on <paramref name="y"/>.</summary>
    /// <remarks>Signature kept from the wavy version; amplitude, wavelength and thickness are ignored.</remarks>
    public static void Draw(DrawingContext dc, Brush stroke, double from, double to, double y, double amplitude, double wavelength, double thickness) =>
        Draw(dc, stroke, stroke, from, to, y);

    /// <summary>Diamond in <paramref name="diamond"/> at the left end, then a hairline in <paramref name="stroke"/> fading to transparent.</summary>
    public static void Draw(DrawingContext dc, Brush stroke, Brush diamond, double from, double to, double y)
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
            ctx.BeginFigure(new Point(cx, y - DiamondHalf), true, true);
            ctx.LineTo(new Point(cx + DiamondHalf, y), false, false);
            ctx.LineTo(new Point(cx, y + DiamondHalf), false, false);
            ctx.LineTo(new Point(cx - DiamondHalf, y), false, false);
        }

        gem.Freeze();
        dc.DrawGeometry(diamond, null, gem);

        // A crisp 1 px line on the pixel grid, faded with an opacity mask so any brush works.
        var start = cx + DiamondHalf + 3;
        var line = Math.Floor(y) + 0.5;
        var fade = new LinearGradientBrush(Colors.Black, Color.FromArgb(0, 0, 0, 0), new Point(start, 0), new Point(to, 0))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        fade.Freeze();
        var pen = new Pen(stroke, 1);
        pen.Freeze();
        dc.PushOpacityMask(fade);
        dc.DrawLine(pen, new Point(start, line), new Point(to, line));
        dc.Pop();
    }
}
