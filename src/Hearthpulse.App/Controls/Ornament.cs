using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// Section heading: an optional glyph in a flame-tinted drop (the mark of the Hearth look) and a bold title. The centered
/// variant has a fading hairline on both sides.
/// </summary>
public sealed class Ornament : FrameworkElement
{
    private static readonly FontFamily FallbackFont = new("Segoe UI Variable Text, Segoe UI");

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Ornament),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Ornament),
        new FrameworkPropertyMetadata(11.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CenteredProperty = DependencyProperty.Register(
        nameof(Centered), typeof(bool), typeof(Ornament),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(Ornament),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public bool Centered { get => (bool)GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }

    /// <summary>Material Design Icons glyph shown in a drop before the title (empty for none).</summary>
    public string Icon { get => (string)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    private bool Large => Size >= 13;

    private double IconSize => Math.Round(Size * 2);

    /// <summary>"BATTLEGROUNDS RATING" reads as "Battlegrounds rating"; mixed-case text is kept.</summary>
    private static string SentenceCase(string text)
    {
        if (text.Length == 0 || text.Any(char.IsLower))
        {
            return text;
        }

        var lower = text.ToLower(CultureInfo.CurrentCulture);
        return char.ToUpper(lower[0], CultureInfo.CurrentCulture) + lower[1..];
    }

    private FormattedText Format()
    {
        var family = (Large ? TryFindResource("DisplayFont") : TryFindResource("UiFont")) as FontFamily ?? FallbackFont;
        var ink = TryFindResource(Large ? "Text" : "Muted") as Brush ?? Brushes.Gainsboro;
        var face = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        return new FormattedText(SentenceCase(Text ?? string.Empty), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            face, Large ? Size + 3 : Size + 0.5, ink, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format();
        var iconWidth = Icon.Length > 0 ? IconSize + 10 : 0;
        var width = double.IsInfinity(availableSize.Width) ? iconWidth + text.Width + 48 : availableSize.Width;
        return new Size(width, Math.Max(Math.Ceiling(text.Height) + 4, Icon.Length > 0 ? IconSize : 0));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = Format();
        var w = ActualWidth;
        var mid = ActualHeight / 2;
        var trim = TryFindResource("GoldTrim") as Brush ?? Brushes.DarkGoldenrod;
        var gold = TryFindResource("Accent") as Brush ?? Brushes.Goldenrod;

        if (Centered)
        {
            var left = (w - text.Width) / 2;
            dc.DrawText(text, new Point(left, mid - (text.Height / 2)));

            // Mirror the left rule so both lines start at the title and fade outwards.
            var leftEnd = left - 10;
            dc.PushTransform(new ScaleTransform(-1, 1, leftEnd / 2, 0));
            Wave.Hairline(dc, trim, 0, leftEnd, mid);
            dc.Pop();
            Wave.Hairline(dc, trim, left + text.Width + 10, w, mid);
            return;
        }

        var x = 0.0;
        if (Icon.Length > 0)
        {
            var d = IconSize;
            dc.PushTransform(new TranslateTransform(0, mid - (d / 2)));
            var edge = new Pen(TryFindResource("FlameLine") as Brush ?? trim, 1);
            edge.Freeze();
            dc.DrawGeometry(TryFindResource("FlameSoft") as Brush ?? Brushes.DimGray, edge, DropShape(d));
            dc.Pop();
            var glyph = new FormattedText(Icon, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface((TryFindResource("IconFont") as FontFamily) ?? FallbackFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                d * 0.5, gold, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(glyph, new Point((d - glyph.Width) / 2, mid - (glyph.Height / 2)));
            x = d + 10;
        }

        dc.DrawText(text, new Point(x, mid - (text.Height / 2)));
    }

    /// <summary>A circle of diameter <paramref name="d"/> with its bottom-left corner drawn in: a drop, like the flame's tip.</summary>
    private static StreamGeometry DropShape(double d)
    {
        var r = d / 2;
        var s = d * 0.18;
        var drop = new StreamGeometry();
        using (var ctx = drop.Open())
        {
            ctx.BeginFigure(new Point(r, 0.5), true, true);
            ctx.ArcTo(new Point(d - 0.5, r), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Clockwise, true, false);
            ctx.ArcTo(new Point(r, d - 0.5), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(s, d - 0.5), true, false);
            ctx.ArcTo(new Point(0.5, d - s), new Size(s - 0.5, s - 0.5), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(0.5, r), true, false);
            ctx.ArcTo(new Point(r, 0.5), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Clockwise, true, false);
        }

        drop.Freeze();
        return drop;
    }
}
