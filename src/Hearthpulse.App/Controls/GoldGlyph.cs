using System.Windows;
using System.Windows.Media;
using Hearthpulse.App.Infrastructure;

namespace Hearthpulse.App.Controls;

/// <summary>The navigation's game-icons.net glyphs (<see cref="GameIconData"/>), parsed once and frozen.</summary>
public static class GameIcons
{
    public static Geometry Home { get; } = Parse(GameIconData.Home);
    public static Geometry Battlegrounds { get; } = Parse(GameIconData.Battlegrounds);
    public static Geometry Standard { get; } = Parse(GameIconData.Standard);
    public static Geometry Wild { get; } = Parse(GameIconData.Wild);
    public static Geometry Arena { get; } = Parse(GameIconData.Arena);
    public static Geometry Brawl { get; } = Parse(GameIconData.Brawl);
    public static Geometry History { get; } = Parse(GameIconData.History);
    public static Geometry Settings { get; } = Parse(GameIconData.Settings);
    public static Geometry Account { get; } = Parse(GameIconData.Account);

    private static Geometry Parse(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// A glyph in engraved gold (V3 icons I1), no plate behind it. From 24 px: a dark drop, the gold gradient, a light top
/// edge and a dark bottom edge clipped to the glyph, and a gloss on its upper part. Smaller, the bevel only blurs: a flat
/// gold silhouette in a dark outline. Brushes are the theme's <c>Glyph*</c> keys, so every theme recolours the gold.
/// Same as the Avalonia GoldGlyph.
/// </summary>
public sealed class GoldGlyph : FrameworkElement
{
    /// <summary>The glyph's box: game-icons.net draws in 512 x 512.</summary>
    private const double Box = 512;

    /// <summary>Below this size the bevel only blurs and is dropped.</summary>
    public const double EngravedFrom = 24;

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(Geometry), typeof(GoldGlyph), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LitProperty = DependencyProperty.Register(
        nameof(Lit), typeof(bool), typeof(GoldGlyph), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The glyph in a 512 x 512 box (<see cref="GameIcons"/>).</summary>
    public Geometry? Glyph { get => (Geometry?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>The brighter gold of the open page's item.</summary>
    public bool Lit { get => (bool)GetValue(LitProperty); set => SetValue(LitProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 20 : Width, double.IsNaN(Height) ? 20 : Height);

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var engraved = size >= EngravedFrom;
        if (Glyph is not { } glyph || size <= 0)
        {
            return;
        }

        // Pens are in the box's units: one screen pixel is Box / size of them.
        var px = Box / size;
        var outline = Find("GlyphOutline");
        dc.PushTransform(new TranslateTransform((ActualWidth - size) / 2, (ActualHeight - size) / 2));
        dc.PushTransform(new ScaleTransform(size / Box, size / Box));
        if (!engraved)
        {
            dc.DrawGeometry(outline, Pen(outline, 1.7 * px), glyph);
            dc.DrawGeometry(Find(Lit ? "GlyphGoldLit" : "GlyphGoldFlat"), Pen(Find("GlyphGoldEdge"), 0.5 * px), glyph);
        }
        else
        {
            var bevel = Math.Min(1.2, size / 30) * px;
            dc.PushTransform(new TranslateTransform(0, 0.9 * px));
            dc.DrawGeometry(outline, Pen(outline, Math.Min(2.0, size / 16) * px), glyph);
            dc.Pop();
            dc.DrawGeometry(Find(Lit ? "GlyphGoldLit" : "GlyphGold"), null, glyph);
            dc.PushClip(glyph);
            dc.PushTransform(new TranslateTransform(0, bevel * 0.85));
            dc.DrawGeometry(null, Pen(Find("GlyphBevelLight"), bevel), glyph);
            dc.Pop();
            dc.PushTransform(new TranslateTransform(0, -bevel * 0.85));
            dc.DrawGeometry(null, Pen(Find("GlyphBevelDark"), bevel), glyph);
            dc.Pop();
            dc.DrawRectangle(Find("GlyphGloss"), null, new Rect(0, 0, Box, Box * 0.42));
            dc.Pop();
        }

        dc.Pop();
        dc.Pop();
    }

    private Brush? Find(string key) => TryFindResource(key) as Brush;

    private static Pen? Pen(Brush? brush, double thickness) =>
        brush is null ? null : new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
}
