using System.Windows;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// A rule that fills its width: a hairline that fades out to the right. With <see cref="Pulse"/> it starts with a
/// heartbeat in the flame of the logo (the page rule under titles). (The name is kept from the old wavy divider so
/// existing XAML keeps working.)
/// </summary>
public sealed class Wave : FrameworkElement
{
    /// <summary>The beat's corners from its start: x and y offsets from the line.</summary>
    private static readonly (double X, double Y)[] Beat = [(0, 0), (12, 0), (15, -4), (19, 6), (23, -7), (26, 0), (30, 0)];

    private const double BeatHeight = 16;

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Wave),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        nameof(Pulse), typeof(bool), typeof(Wave),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Colour of the hairline at its left end (defaults to the theme's GoldTrim, FlameLine after a beat).</summary>
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Start with a heartbeat in the theme's flame.</summary>
    public bool Pulse { get => (bool)GetValue(PulseProperty); set => SetValue(PulseProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 40 : availableSize.Width, Pulse ? BeatHeight : 3);

    protected override void OnRender(DrawingContext dc)
    {
        var y = ActualHeight / 2;
        if (!Pulse)
        {
            if ((Stroke ?? TryFindResource("GoldTrim") as Brush) is { } stroke)
            {
                Hairline(dc, stroke, 0, ActualWidth, y);
            }

            return;
        }

        if (TryFindResource("FlameRule") is not Brush flame || ActualWidth < Beat[^1].X + 8)
        {
            return;
        }

        var beat = new StreamGeometry();
        using (var ctx = beat.Open())
        {
            ctx.BeginFigure(new Point(Beat[0].X + 0.5, y + Beat[0].Y), false, false);
            foreach (var (x, dy) in Beat.Skip(1))
            {
                ctx.LineTo(new Point(x + 0.5, y + dy), true, true);
            }
        }

        beat.Freeze();
        var pen = new Pen(flame, 1.6) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, beat);
        Hairline(dc, Stroke ?? TryFindResource("FlameLine") as Brush ?? flame, Beat[^1].X, ActualWidth, y);
    }

    /// <summary>A crisp 1 px line from <paramref name="from"/> to <paramref name="to"/> on <paramref name="y"/>, fading to transparent.</summary>
    public static void Hairline(DrawingContext dc, Brush stroke, double from, double to, double y)
    {
        if (to - from < 8)
        {
            return;
        }

        var line = Math.Floor(y) + 0.5;
        var fade = new LinearGradientBrush(Colors.Black, Color.FromArgb(0, 0, 0, 0), new Point(from, 0), new Point(to, 0))
        {
            MappingMode = BrushMappingMode.Absolute,
        };
        fade.Freeze();
        var pen = new Pen(stroke, 1);
        pen.Freeze();
        dc.PushOpacityMask(fade);
        dc.DrawLine(pen, new Point(from, line), new Point(to, line));
        dc.Pop();
    }
}
