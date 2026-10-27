namespace Hearthpulse.Core;

/// <summary>
/// A colour theme: the hue of the accents, the hue of the background and how strongly the background is tinted, plus
/// the character of a preset - the hue of its second accent, how far its accents and surfaces are lightened (negative
/// values darken the surfaces) and the name of its background motif (a <c>Motif&lt;Name&gt;</c> geometry in the theme).
/// </summary>
public sealed record ThemeSpec(
    string Name,
    double AccentHue,
    double BaseHue,
    double BaseTint,
    double SecondaryHue = ThemePalette.DefaultSecondaryHue,
    double AccentLift = 0,
    double SurfaceLift = 0,
    string Motif = ThemePalette.DefaultName);

/// <summary>
/// Every theme is a recolouring of the built-in palette: gold accents (hue 42) and a patina-teal second accent
/// (hue 168) on warm brown wood and parchment (hue 28). Greens, reds, blues and greys are never touched, so wins,
/// losses, tiers and rarities keep their meaning.
/// </summary>
public static class ThemePalette
{
    public const string DefaultName = "Tavern";
    public const string CustomName = "Custom";
    public const double DefaultAccentHue = 42;
    public const double DefaultBaseHue = 28;
    public const double DefaultSecondaryHue = 168;

    /// <summary>Second-accent colours of the built-in palette: patina teals around <see cref="DefaultSecondaryHue"/>.</summary>
    private const double SecondaryFrom = 150, SecondaryTo = 186;

    public static IReadOnlyList<ThemeSpec> Presets { get; } =
    [
        // Gold and patina on dark oak: a carved hearth rosette.
        new(DefaultName, DefaultAccentHue, DefaultBaseHue, 1.0),
        // Glacier blue and aurora violet on slate, lit a little brighter: a snow crystal.
        new("Frost", 198, 214, 0.8, 255, 0.10, 0.10, "Frost"),
        // Fel green and void violet on deep moss: a watching sigil.
        new("Fel", 105, 150, 0.75, 290, 0, -0.04, "Fel"),
        // Lilac and ley cyan on midnight, accents lifted for contrast: crossing orbits.
        new("Arcane", 280, 262, 0.8, 188, 0.30, 0.04, "Arcane"),
        // Forge orange and molten amber on charred red, a little darker: rising flames.
        new("Ember", 22, 6, 1.1, 46, 0.06, -0.06, "Ember"),
        // Sea glass and sunlit sand on deep water: curling waves.
        new("Tide", 172, 192, 0.8, 34, 0, 0.02, "Tide"),
        // Gold and moonlit steel on near-black stone: a crescent among stars.
        new("Shadow", DefaultAccentHue, DefaultBaseHue, 0.12, 222, 0, -0.08, "Shadow"),
    ];

    /// <summary>The preset with this name, or a custom theme made of the given values.</summary>
    public static ThemeSpec Resolve(string? name, double accentHue, double baseHue, double baseTint) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Custom(Wrap(accentHue), Wrap(baseHue), Math.Clamp(baseTint, 0, 1.5));

    public static bool IsDefault(ThemeSpec theme) =>
        theme.AccentHue == DefaultAccentHue && theme.BaseHue == DefaultBaseHue && theme.BaseTint == 1.0
        && theme.SecondaryHue == DefaultSecondaryHue && theme.AccentLift == 0 && theme.SurfaceLift == 0
        && theme.Motif == DefaultName;

    /// <summary>Maps one colour of the built-in palette (0xAARRGGBB) into the theme.</summary>
    public static uint Map(uint argb, ThemeSpec theme)
    {
        if (IsDefault(theme))
        {
            return argb;
        }

        var (h, s, l) = ToHsl((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        if (s < 0.08)
        {
            return argb;
        }

        var chroma = s * (1 - Math.Abs((2 * l) - 1));
        if (h >= SecondaryFrom && h <= SecondaryTo && chroma >= 0.15)
        {
            // The second accent turns with the theme; only its lit tones are lifted, so its dark washes stay dark.
            h = theme.SecondaryHue + (h - DefaultSecondaryHue);
            l = l > 0.3 ? Lift(l, theme.AccentLift) : l;
        }
        else if (h < 15 || h > 60)
        {
            return argb;
        }
        else if (chroma >= 0.3)
        {
            // Saturated mid tones are the gold accents; dull or very dark and very light tones are wood and parchment.
            h = theme.AccentHue + (h - DefaultAccentHue);
            l = Lift(l, theme.AccentLift);
        }
        else
        {
            h = theme.BaseHue + (h - DefaultBaseHue);
            s = Math.Clamp(s * theme.BaseTint, 0, 1);
            l = l < 0.5 ? Math.Clamp(l * (1 + theme.SurfaceLift), 0, 0.5) : l;
        }

        var (r, g, b) = FromHsl(Wrap(h), s, l);
        return (argb & 0xFF000000) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>Moves a lightness towards white by a share of the remaining distance.</summary>
    private static double Lift(double l, double by) => Math.Clamp(l + (by * (1 - l)), 0, 1);

    /// <summary>A custom theme keeps the built-in spacing of the two accents and borrows the motif of the nearest preset.</summary>
    private static ThemeSpec Custom(double accentHue, double baseHue, double baseTint)
    {
        var nearest = Presets.MinBy(p => HueDistance(p.AccentHue, accentHue))!;
        return new ThemeSpec(CustomName, accentHue, baseHue, baseTint,
            Wrap(accentHue + (DefaultSecondaryHue - DefaultAccentHue)), Motif: nearest.Motif);
    }

    private static double HueDistance(double a, double b) => Math.Min(Wrap(a - b), Wrap(b - a));

    private static double Wrap(double hue) => ((hue % 360) + 360) % 360;

    private static (double H, double S, double L) ToHsl(byte red, byte green, byte blue)
    {
        double r = red / 255.0, g = green / 255.0, b = blue / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var d = max - min;
        if (d < 1e-9)
        {
            return (0, 0, l);
        }

        var s = d / (1 - Math.Abs((2 * l) - 1));
        var h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * (((b - r) / d) + 2) : 60 * (((r - g) / d) + 4);
        return (Wrap(h), Math.Min(1, s), l);
    }

    private static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs((2 * l) - 1)) * s;
        var x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        var m = l - (c / 2);
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return (Byte(r + m), Byte(g + m), Byte(b + m));
    }
}
