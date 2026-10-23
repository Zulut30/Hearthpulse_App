namespace Hearthlens.Core;

/// <summary>A colour theme: the hue of the accents, the hue of the background and how strongly the background is tinted.</summary>
public sealed record ThemeSpec(string Name, double AccentHue, double BaseHue, double BaseTint);

/// <summary>
/// Every theme is a recolouring of the built-in palette: gold accents (hue 42) on warm brown wood and parchment
/// (hue 28). Greens, reds, blues and greys are never touched, so wins, losses, tiers and rarities keep their meaning.
/// </summary>
public static class ThemePalette
{
    public const string DefaultName = "Tavern";
    public const string CustomName = "Custom";
    public const double DefaultAccentHue = 42;
    public const double DefaultBaseHue = 28;

    public static IReadOnlyList<ThemeSpec> Presets { get; } =
    [
        new(DefaultName, DefaultAccentHue, DefaultBaseHue, 1.0),
        new("Frost", 195, 214, 0.8),
        new("Fel", 105, 150, 0.75),
        new("Arcane", 280, 262, 0.8),
        new("Ember", 24, 6, 1.1),
        new("Tide", 170, 192, 0.8),
        new("Shadow", DefaultAccentHue, DefaultBaseHue, 0.12),
    ];

    /// <summary>The preset with this name, or a custom theme made of the given values.</summary>
    public static ThemeSpec Resolve(string? name, double accentHue, double baseHue, double baseTint) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? new ThemeSpec(CustomName, Wrap(accentHue), Wrap(baseHue), Math.Clamp(baseTint, 0, 1.5));

    public static bool IsDefault(ThemeSpec theme) =>
        theme.AccentHue == DefaultAccentHue && theme.BaseHue == DefaultBaseHue && theme.BaseTint == 1.0;

    /// <summary>Maps one colour of the built-in palette (0xAARRGGBB) into the theme.</summary>
    public static uint Map(uint argb, ThemeSpec theme)
    {
        if (IsDefault(theme))
        {
            return argb;
        }

        var (h, s, l) = ToHsl((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        if (s < 0.08 || h < 15 || h > 60)
        {
            return argb;
        }

        // Saturated mid tones are the gold accents; dull or very dark and very light tones are wood and parchment.
        var chroma = s * (1 - Math.Abs((2 * l) - 1));
        if (chroma >= 0.3)
        {
            h = theme.AccentHue + (h - DefaultAccentHue);
        }
        else
        {
            h = theme.BaseHue + (h - DefaultBaseHue);
            s = Math.Clamp(s * theme.BaseTint, 0, 1);
        }

        var (r, g, b) = FromHsl(Wrap(h), s, l);
        return (argb & 0xFF000000) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

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
