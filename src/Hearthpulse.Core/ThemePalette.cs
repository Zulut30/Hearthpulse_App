namespace Hearthpulse.Core;

/// <summary>
/// A colour theme: the hue of the accents, the hue of the background and how strongly the background is tinted, plus
/// the character of a preset - the hue of its second accent, how far its accents and surfaces are lightened (negative
/// values darken the surfaces), the name of its background motif (a <c>Motif&lt;Name&gt;</c> geometry in the theme) and
/// the hero whose art shows behind Settings and on the theme's gallery tile (a card id of the HearthstoneJSON cache).
/// <see cref="Depth"/> sinks only the deepest tones (the window and the darkest plates) towards black, so faint text,
/// drawn a little lighter, keeps its contrast: Midnight's OLED black.
/// </summary>
public sealed record ThemeSpec(
    string Name,
    double AccentHue,
    double BaseHue,
    double BaseTint,
    double SecondaryHue = ThemePalette.DefaultSecondaryHue,
    double AccentLift = 0,
    double SurfaceLift = 0,
    string Motif = ThemePalette.DefaultName,
    string Hero = ThemePalette.DefaultHero,
    double Depth = 0);

/// <summary>
/// An accent the player can put on any preset instead of its own (Settings, Appearance). A curated set rather than a free
/// picker: each swatch's hue and lift are chosen so the accent and the dark text on it stay at WCAG AA on every preset
/// (<c>ThemeContrastTests</c>).
/// </summary>
public sealed record AccentSwatch(string Key, double Hue, double Lift);

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

    /// <summary>Harth Stonebrew, the innkeeper: Tavern's hero.</summary>
    public const string DefaultHero = "GIFT_01";

    /// <summary>Second-accent colours of the built-in palette: patina teals around <see cref="DefaultSecondaryHue"/>.</summary>
    private const double SecondaryFrom = 150, SecondaryTo = 186;

    /// <summary>Tones darker than this are what <see cref="ThemeSpec.Depth"/> sinks.</summary>
    private const double DeepTone = 0.2;

    public static IReadOnlyList<ThemeSpec> Presets { get; } =
    [
        // Gold and patina on dark oak: a carved hearth rosette.
        new(DefaultName, DefaultAccentHue, DefaultBaseHue, 1.0),
        // Glacier blue and aurora violet on slate, lit a little brighter: a snow crystal.
        new("Frost", 198, 214, 0.8, 255, 0.10, 0.10, "Frost", "HERO_08"),
        // Fel green and void violet on deep moss: a watching sigil.
        new("Fel", 105, 150, 0.75, 290, 0, -0.04, "Fel", "HERO_10"),
        // Lilac and ley cyan on midnight, accents lifted for contrast: crossing orbits.
        new("Arcane", 280, 262, 0.8, 188, 0.30, 0.04, "Arcane", "HERO_09"),
        // Forge orange and molten amber on charred red, a little darker: rising flames.
        new("Ember", 22, 6, 1.1, 46, 0.06, -0.06, "Ember", "HERO_07"),
        // Sea glass and sunlit sand on deep water: curling waves.
        new("Tide", 172, 192, 0.8, 34, 0, 0.02, "Tide", "HERO_02"),
        // Gold and moonlit steel on near-black stone: a crescent among stars.
        new("Shadow", DefaultAccentHue, DefaultBaseHue, 0.12, 222, 0, -0.08, "Shadow", "HERO_03"),
        // Druid's grove: young-leaf gold and autumn amber on dark pine, a ring of leaves.
        new("Grove", 72, 128, 0.55, 28, 0.06, -0.04, "Grove", "HERO_06"),
        // The void: orchid and cold starlight on deep violet, darker still, a spiral pulling inwards.
        new("Void", 300, 272, 0.9, 196, 0.32, 0, "Void", "HERO_11", Depth: 0.5),
        // Warrior's blood and steel: crimson and bronze on gunmetal, two crossed blades over a shield.
        new("Bloodsteel", 354, 214, 0.3, 40, 0.34, 0.02, "Bloodsteel", "HERO_01"),
        // Ancient lands: turquoise and terracotta on sandstone, lit warmer: a pyramid under the sun.
        new("Sands", 180, 36, 0.7, 14, 0.12, 0.10, "Sands", "HERO_05"),
        // Midnight: ice-white accent and gold on true black for OLED screens and the most contrast, a field of stars.
        new("Midnight", 204, 220, 0, 46, 0.55, -0.1, "Midnight", "HERO_04", Depth: 0.9),
        // Battlegrounds mechs: polished copper and arc cyan on gunmetal, a cog.
        new("Mech", 26, 200, 0.35, 186, 0.10, 0.0, "Mech", "TB_BaconShopBob"),
    ];

    /// <summary>
    /// Accents that can replace a preset's own (<see cref="AppSettings.ThemeAccent"/>): none is green, red-orange or blue
    /// enough to be mistaken for a win, a loss or a link.
    /// </summary>
    public static IReadOnlyList<AccentSwatch> Accents { get; } =
    [
        new("Gold", DefaultAccentHue, 0),
        new("Amber", 30, 0.26),
        new("Rose", 336, 0.54),
        new("Orchid", 290, 0.54),
        new("Lilac", 262, 0.6),
        new("Sky", 196, 0.22),
        new("Jade", 164, 0),
        new("Lime", 70, 0),
    ];

    /// <summary>
    /// The theme the settings describe: the preset (or custom theme), with the chosen accent swatch over its own accent.
    /// </summary>
    public static ThemeSpec FromSettings(AppSettings settings, string? name = null) =>
        WithAccent(Resolve(name ?? settings.Theme, settings.ThemeAccentHue, settings.ThemeBaseHue, settings.ThemeBaseTint), settings.ThemeAccent);

    /// <summary>The theme with the swatch <paramref name="accent"/> (a key of <see cref="Accents"/>) for its accent; unknown or empty keeps its own.</summary>
    public static ThemeSpec WithAccent(ThemeSpec theme, string? accent) =>
        Accents.FirstOrDefault(a => string.Equals(a.Key, accent, StringComparison.OrdinalIgnoreCase)) is { } swatch
            ? theme with { AccentHue = swatch.Hue, AccentLift = Math.Max(theme.AccentLift, swatch.Lift) }
            : theme;

    /// <summary>The preset with this name, or a custom theme made of the given values.</summary>
    public static ThemeSpec Resolve(string? name, double accentHue, double baseHue, double baseTint) =>
        Presets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? Custom(Wrap(accentHue), Wrap(baseHue), Math.Clamp(baseTint, 0, 1.5));

    public static bool IsDefault(ThemeSpec theme) =>
        theme.AccentHue == DefaultAccentHue && theme.BaseHue == DefaultBaseHue && theme.BaseTint == 1.0
        && theme.SecondaryHue == DefaultSecondaryHue && theme.AccentLift == 0 && theme.SurfaceLift == 0
        && theme.Depth == 0 && theme.Motif == DefaultName;

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
            if (theme.Depth > 0 && l < DeepTone)
            {
                // The darker the tone, the further it sinks: black stays black, tones at DeepTone do not move.
                l *= 1 - (theme.Depth * (1 - (l / DeepTone)));
            }
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
            Wrap(accentHue + (DefaultSecondaryHue - DefaultAccentHue)), Motif: nearest.Motif, Hero: nearest.Hero);
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
