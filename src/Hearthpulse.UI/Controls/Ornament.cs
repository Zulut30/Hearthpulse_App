using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// Section heading: an optional gold icon inside a bronze-edged cookie shape and a bold title, as in WPF; the centered
/// variant keeps the gold rules on both sides. A title longer than the width (compact) ends in an ellipsis, with the
/// whole title as the tooltip.
/// </summary>
public sealed class Ornament : Control
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<Ornament, string>(nameof(Text), string.Empty);

    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Ornament, double>(nameof(Size), 11.0);

    public static readonly StyledProperty<bool> CenteredProperty = AvaloniaProperty.Register<Ornament, bool>(nameof(Centered));

    public static readonly StyledProperty<string> IconProperty = AvaloniaProperty.Register<Ornament, string>(nameof(Icon), string.Empty);

    /// <summary>The tooltip is the cut title, set here (not from XAML).</summary>
    private bool _ownTip;

    // The whole title and what it was made from; the title cut to _cutWidth (null until a narrow width asks for it).
    private FormattedText? _full;
    private string _fullText = string.Empty;
    private Typeface _fullFace;
    private double _fullSize;
    private IBrush? _fullBrush;
    private FormattedText? _cut;
    private double _cutWidth;

    static Ornament()
    {
        AffectsRender<Ornament>(TextProperty, SizeProperty, CenteredProperty, IconProperty);
        AffectsMeasure<Ornament>(TextProperty, SizeProperty, IconProperty);
    }

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    public bool Centered { get => GetValue(CenteredProperty); set => SetValue(CenteredProperty, value); }

    /// <summary>Material Design Icons glyph shown in a cookie before the title (empty for none).</summary>
    public string Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    private bool Large => Size >= 13;

    private double IconSize => Math.Round(Size * 2.3);

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

    /// <summary>
    /// The whole title, built again only when its text, typeface, size or brush (a theme change) differs: measure,
    /// arrange and render ask for it on every layout pass.
    /// </summary>
    private FormattedText Format()
    {
        var text = Text ?? string.Empty;
        var face = new Typeface(Font(Large ? "DisplayFont" : "UiFont"), FontStyle.Normal, FontWeight.Bold);
        var size = Large ? Size + 3 : Size + 0.5;
        var brush = ThemeBrushes.Get(Large ? "Text" : "Muted");
        if (_full is null || !string.Equals(text, _fullText, StringComparison.Ordinal) || face != _fullFace || !size.Equals(_fullSize)
            || !ReferenceEquals(brush, _fullBrush))
        {
            (_fullText, _fullFace, _fullSize, _fullBrush) = (text, face, size, brush);
            _full = New();
            _cut = null;
        }

        return _full;
    }

    private FormattedText New() =>
        new(SentenceCase(_fullText), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _fullFace, _fullSize, _fullBrush);

    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format();
        var iconWidth = Icon.Length > 0 ? IconSize + 10 : 0;
        var width = double.IsInfinity(availableSize.Width) ? iconWidth + text.Width + 48 : availableSize.Width;
        return new Size(width, Math.Max(Math.Ceiling(text.Height) + 4, Icon.Length > 0 ? IconSize : 0));
    }

    /// <summary>The title cut to <paramref name="width"/> with an ellipsis (one line) when it is wider.</summary>
    private FormattedText Format(double width)
    {
        var text = Format();
        if (!(width > 0 && text.Width > width))
        {
            return text;
        }

        if (_cut is null || !width.Equals(_cutWidth))
        {
            _cut = New();
            _cut.MaxTextWidth = width;
            _cut.MaxLineCount = 1;
            _cut.Trimming = TextTrimming.CharacterEllipsis;
            _cutWidth = width;
        }

        return _cut;
    }

    /// <summary>The room the title has: the width less the icon, and the rule's diamonds when centered.</summary>
    private double TitleRoom(double width) => Centered ? width - 40 : width - (Icon.Length > 0 ? IconSize + 10 : 0);

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The tooltip says the whole title only while it is cut; a tooltip set from XAML is left alone.
        var cut = Format().Width > TitleRoom(finalSize.Width);
        if (cut && (_ownTip || ToolTip.GetTip(this) is null))
        {
            ToolTip.SetTip(this, SentenceCase(Text ?? string.Empty));
            _ownTip = true;
        }
        else if (!cut && _ownTip)
        {
            ToolTip.SetTip(this, null);
            _ownTip = false;
        }

        return base.ArrangeOverride(finalSize);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var text = Format(TitleRoom(w));
        var mid = Bounds.Height / 2;
        var trim = ThemeBrushes.Get("GoldTrim");
        var gold = ThemeBrushes.Get("Accent");

        if (Centered)
        {
            var left = (w - text.Width) / 2;
            context.DrawText(text, new Point(left, mid - (text.Height / 2)));

            // Mirror the left rule so both diamonds sit next to the title and the lines fade outwards.
            var leftEnd = left - 10;
            using (context.PushTransform(Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(leftEnd, 0)))
            {
                Wave.Draw(context, trim, gold, 0, leftEnd, mid);
            }

            Wave.Draw(context, trim, gold, left + text.Width + 10, w, mid);
            return;
        }

        var x = 0.0;
        if (Icon.Length > 0)
        {
            var d = IconSize;
            using (context.PushTransform(Matrix.CreateTranslation(0, mid - (d / 2))))
            {
                context.DrawGeometry(ThemeBrushes.Get("Surface3"), new Pen(trim, 1), Scallop.Build(d, d, 9, 0.09, 0));
            }

            var glyph = new FormattedText(Icon, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(Font("IconFont")), d * 0.5, gold);
            context.DrawText(glyph, new Point((d - glyph.Width) / 2, mid - (glyph.Height / 2)));
            x = d + 10;
        }

        context.DrawText(text, new Point(x, mid - (text.Height / 2)));
    }
}
