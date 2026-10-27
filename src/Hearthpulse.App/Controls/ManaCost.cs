using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Hearthpulse.App.Controls;

/// <summary>
/// Card cost badge: the mana gem picture (Assets/mana-gem.png, original artwork from tools/mana/draw_mana_gem.py)
/// with the cost as a white Montserrat digit and a thin dark edge so it reads on the blue. An empty <see cref="Text"/>
/// draws the gem alone.
/// The gem fills the smaller side of the control and the digit scales with it.
/// </summary>
public sealed class ManaCost : FrameworkElement
{
    private static readonly ImageSource Gem = LoadGem();
    private static readonly FontFamily FallbackFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly Brush Outline = Frozen(new SolidColorBrush(Color.FromRgb(0x0A, 0x14, 0x30)));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ManaCost),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public ManaCost() => RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    private static ImageSource LoadGem()
    {
        var gem = new BitmapImage(new Uri("pack://application:,,,/Hearthpulse;component/Assets/mana-gem.png"));
        gem.Freeze();
        return gem;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0)
        {
            return;
        }

        var x = (ActualWidth - d) / 2;
        var y = (ActualHeight - d) / 2;
        dc.DrawImage(Gem, new Rect(x, y, d, d));
        if (string.IsNullOrEmpty(Text))
        {
            return;
        }

        var family = TryFindResource("DisplayFont") as FontFamily ?? FallbackFont;
        var digit = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            d * (Text.Length > 1 ? 0.54 : 0.62), Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var glyphs = digit.BuildGeometry(new Point(x + ((d - digit.Width) / 2), y + ((d - digit.Height) / 2)));

        // Stroke first, then fill on top, so the outline sits outside the glyph instead of eating into it.
        var pen = new Pen(Outline, Math.Max(1, d * 0.08)) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, glyphs);
        dc.DrawGeometry(Brushes.White, null, glyphs);
    }
}
