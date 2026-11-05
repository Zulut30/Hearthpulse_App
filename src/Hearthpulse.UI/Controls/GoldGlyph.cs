using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>The navigation's game-icons.net glyphs (<see cref="GameIconData"/>), parsed once.</summary>
public static class GameIcons
{
    public static Geometry Home { get; } = StreamGeometry.Parse(GameIconData.Home);
    public static Geometry Battlegrounds { get; } = StreamGeometry.Parse(GameIconData.Battlegrounds);
    public static Geometry Standard { get; } = StreamGeometry.Parse(GameIconData.Standard);
    public static Geometry Wild { get; } = StreamGeometry.Parse(GameIconData.Wild);
    public static Geometry Arena { get; } = StreamGeometry.Parse(GameIconData.Arena);
    public static Geometry Brawl { get; } = StreamGeometry.Parse(GameIconData.Brawl);
    public static Geometry History { get; } = StreamGeometry.Parse(GameIconData.History);
    public static Geometry Settings { get; } = StreamGeometry.Parse(GameIconData.Settings);
    public static Geometry Account { get; } = StreamGeometry.Parse(GameIconData.Account);
}

/// <summary>
/// A glyph in engraved gold (V3 icons I1), no plate behind it. From 24 px: a dark drop, the gold gradient, a light top
/// edge and a dark bottom edge clipped to the glyph, and a gloss on its upper part. Smaller, the bevel only blurs: a flat
/// gold silhouette in a dark outline. Brushes are the theme's <c>Glyph*</c> keys, so every theme recolours the gold.
/// Same as the WPF GoldGlyph.
/// </summary>
public sealed class GoldGlyph : Control
{
    /// <summary>The glyph's box: game-icons.net draws in 512 x 512.</summary>
    private const double Box = 512;

    /// <summary>Below this size the bevel only blurs and is dropped.</summary>
    public const double EngravedFrom = 24;

    public static readonly StyledProperty<Geometry?> GlyphProperty = AvaloniaProperty.Register<GoldGlyph, Geometry?>(nameof(Glyph));

    public static readonly StyledProperty<bool> LitProperty = AvaloniaProperty.Register<GoldGlyph, bool>(nameof(Lit));

    static GoldGlyph() => AffectsRender<GoldGlyph>(GlyphProperty, LitProperty);

    /// <summary>The glyph in a 512 x 512 box (<see cref="GameIcons"/>).</summary>
    public Geometry? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    /// <summary>The brighter gold of the open page's item.</summary>
    public bool Lit { get => GetValue(LitProperty); set => SetValue(LitProperty, value); }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsNaN(Width) ? 20 : Width, double.IsNaN(Height) ? 20 : Height);

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var engraved = size >= EngravedFrom;
        if (Glyph is not { } glyph || size <= 0)
        {
            return;
        }

        // Pens are in the box's units: one screen pixel is Box / size of them.
        var px = Box / size;
        var outline = ThemeBrushes.Get("GlyphOutline");
        using var place = context.PushTransform(Matrix.CreateScale(size / Box, size / Box)
                                                * Matrix.CreateTranslation((Bounds.Width - size) / 2, (Bounds.Height - size) / 2));
        if (!engraved)
        {
            context.DrawGeometry(outline, Pen(outline, 1.7 * px), glyph);
            context.DrawGeometry(ThemeBrushes.Get(Lit ? "GlyphGoldLit" : "GlyphGoldFlat"), Pen(ThemeBrushes.Get("GlyphGoldEdge"), 0.5 * px), glyph);
            return;
        }

        var bevel = Math.Min(1.2, size / 30) * px;
        using (context.PushTransform(Matrix.CreateTranslation(0, 0.9 * px)))
        {
            context.DrawGeometry(outline, Pen(outline, Math.Min(2.0, size / 16) * px), glyph);
        }

        context.DrawGeometry(ThemeBrushes.Get(Lit ? "GlyphGoldLit" : "GlyphGold"), null, glyph);
        using (context.PushGeometryClip(glyph))
        {
            using (context.PushTransform(Matrix.CreateTranslation(0, bevel * 0.85)))
            {
                context.DrawGeometry(null, Pen(ThemeBrushes.Get("GlyphBevelLight"), bevel), glyph);
            }

            using (context.PushTransform(Matrix.CreateTranslation(0, -bevel * 0.85)))
            {
                context.DrawGeometry(null, Pen(ThemeBrushes.Get("GlyphBevelDark"), bevel), glyph);
            }

            context.DrawRectangle(ThemeBrushes.Get("GlyphGloss"), null, new Rect(0, 0, Box, Box * 0.42));
        }
    }

    private static Pen Pen(IBrush brush, double thickness) => new(brush, thickness, lineJoin: PenLineJoin.Round);
}
