using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// Section heading: an optional glyph in a flame-tinted drop (the mark of the Hearth look) and a bold title. The centered
/// variant has a fading hairline on both sides.
/// </summary>
public sealed class Ornament : Control
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<Ornament, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Ornament, double>(nameof(Size), 11.0);

    public static readonly StyledProperty<bool> CenteredProperty = AvaloniaProperty.Register<Ornament, bool>(nameof(Centered));

    public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<Ornament, string>(nameof(Icon), string.Empty);

    static Ornament()
    {
        AffectsRender<Ornament>(TextProperty, SizeProperty, CenteredProperty, IconProperty);
        AffectsMeasure<Ornament>(TextProperty, SizeProperty, IconProperty);
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public bool Centered { get => GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }

    /// <summary>Material Design Icons glyph shown in a drop before the title (empty for none).</summary>
    public string Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

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

    private FontFamily Font(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is FontFamily family ? family : FontFamily.Default;

    private FormattedText Format() =>
        new(SentenceCase(Text ?? string.Empty), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Font(Large ? "DisplayFont" : "UiFont"), FontStyle.Normal, FontWeight.Bold),
            Large ? Size + 3 : Size + 0.5, ThemeBrushes.Get(Large ? "Text" : "Muted"));

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format();
        var iconWidth = Icon.Length > 0 ? IconSize + 10 : 0;
        var width = double.IsInfinity(availableSize.Width) ? iconWidth + text.Width + 48 : availableSize.Width;
        return new Size(width, Math.Max(Math.Ceiling(text.Height) + 4, Icon.Length > 0 ? IconSize : 0));
    }

    public override void Render(DrawingContext context)
    {
        var text = Format();
        var w = Bounds.Width;
        var mid = Bounds.Height / 2;
        var trim = ThemeBrushes.Get("GoldTrim");
        var gold = ThemeBrushes.Get("Accent");

        if (Centered)
        {
            var left = (w - text.Width) / 2;
            context.DrawText(text, new Point(left, mid - (text.Height / 2)));

            // Mirror the left rule so both lines start at the title and fade outwards.
            var leftEnd = left - 10;
            using (context.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(leftEnd, 0)))
            {
                Wave.Hairline(context, trim, 0, leftEnd, mid);
            }

            Wave.Hairline(context, trim, left + text.Width + 10, w, mid);
            return;
        }

        var x = 0.0;
        if (Icon.Length > 0)
        {
            var d = IconSize;
            using (context.PushTransform(Matrix.CreateTranslation(0, mid - (d / 2))))
            {
                context.DrawGeometry(ThemeBrushes.Get("FlameSoft"), new Pen(ThemeBrushes.Get("FlameLine"), 1), DropShape(d));
            }

            var glyph = new FormattedText(Icon, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Font("IconFont")), d * 0.5, gold);
            context.DrawText(glyph, new Point((d - glyph.Width) / 2, mid - (glyph.Height / 2)));
            x = d + 10;
        }

        context.DrawText(text, new Point(x, mid - (text.Height / 2)));
    }

    /// <summary>A circle of diameter <paramref name="d"/> with its bottom-left corner drawn in: a drop, like the flame's tip.</summary>
    private static StreamGeometry DropShape(double d)
    {
        var r = d / 2;
        var s = d * 0.18;
        var drop = new StreamGeometry();
        using (var ctx = drop.Open())
        {
            ctx.BeginFigure(new Point(r, 0.5), true);
            ctx.ArcTo(new Point(d - 0.5, r), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Clockwise);
            ctx.ArcTo(new Point(r, d - 0.5), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(s, d - 0.5));
            ctx.ArcTo(new Point(0.5, d - s), new Size(s - 0.5, s - 0.5), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(0.5, r));
            ctx.ArcTo(new Point(r, 0.5), new Size(r - 0.5, r - 0.5), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }

        return drop;
    }
}
