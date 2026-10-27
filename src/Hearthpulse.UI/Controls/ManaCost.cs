using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// Card cost badge: Hearthstone's mana gem (Assets/mana-gem.png, cut from a card render by
/// tools/mana/extract_mana_gem.py) with the cost as a white digit in a thick dark outline, as on a card. An empty
/// <see cref="Text"/> draws the gem alone.
/// The gem fills the smaller side of the control and the digit scales with it.
/// </summary>
public sealed class ManaCost : Control
{
    private static readonly Lazy<Bitmap> Gem = new(() => new Bitmap(AssetLoader.Open(new Uri("avares://Hearthpulse.UI/Assets/mana-gem.png"))));
    private static readonly IBrush Outline = new ImmutableSolidColorBrush(Color.FromRgb(0x05, 0x05, 0x08));

    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<ManaCost, string>(nameof(Text), string.Empty);

    static ManaCost()
    {
        AffectsRender<ManaCost>(TextProperty);
    }

    public ManaCost() => RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);

    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private FontFamily Font(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is FontFamily family ? family : FontFamily.Default;

    public override void Render(DrawingContext context)
    {
        var d = Math.Min(Bounds.Width, Bounds.Height);
        if (d <= 0)
        {
            return;
        }

        var x = (Bounds.Width - d) / 2;
        var y = (Bounds.Height - d) / 2;
        context.DrawImage(Gem.Value, new Rect(x, y, d, d));
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var digit = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Font("DisplayFont"), FontStyle.Normal, FontWeight.Bold),
            d * (Text.Length > 1 ? 0.54 : 0.62), Brushes.White);
        if (digit.BuildGeometry(new Point(x + ((d - digit.Width) / 2), y + ((d - digit.Height) / 2))) is not { } glyphs)
        {
            return;
        }

        // Stroke first, then fill on top, so the outline sits outside the glyph instead of eating into it.
        context.DrawGeometry(null, new Pen(Outline, Math.Max(2, d * 0.2), lineJoin: PenLineJoin.Round), glyphs);
        context.DrawGeometry(Brushes.White, null, glyphs);
    }
}
