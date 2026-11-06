using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Hearthpulse.Core;

namespace Hearthpulse.Tests;

/// <summary>
/// Every preset keeps text readable: the colours of Theme.xaml are recoloured as ThemeEngine does, and text tokens are
/// checked against every surface they sit on (every stop of a gradient) for the WCAG contrast ratio - 4.5:1 (AA body
/// text) for text, 3:1 for the faint tertiary text.
/// </summary>
public sealed class ThemeContrastTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly (string Text, string[] On, double Min)[] Pairs =
    [
        ("Text", ["Bg", "Surface", "Surface2", "Surface3", "Surface4", "Row", "CardFill", "GhostFill", "SelectedFill", "OverlayCardFill", "OverlayPanelSolid", "OverlayBadgeShade"], 4.5),
        ("Muted", ["Bg", "Surface", "Surface2", "Surface3", "Surface4", "Row", "CardFill", "OverlayCardFill", "OverlayPanelSolid", "OverlayBadgeShade"], 4.5),
        ("Accent", ["Bg", "Surface", "Surface2", "CardFill", "SelectedFill", "OverlayCardFill", "OverlayPanelSolid", "OverlayBadgeShade"], 4.5),
        ("OnAccent", ["Accent", "PrimaryFill", "PrimaryFillHover"], 4.5),
        ("OnSecondaryContainer", ["SecondaryContainer", "GhostFill"], 4.5),
        ("OnAccentContainer", ["AccentContainer"], 4.5),
        ("Faint", ["Bg", "Surface", "Surface2", "CardFill"], 3.0),
        // V3 card rows: a legendary name (and its star) in the rarity colour on the row's plate
        ("RarityLegendary", ["Row", "OverlayBadgeShade"], 4.5),
    ];

    /// <summary>Every preset on its own and with every accent swatch over its accent ("Frost+Rose").</summary>
    public static TheoryData<string> Presets() => new(ThemePalette.Presets.Select(p => p.Name)
        .Concat(ThemePalette.Presets.SelectMany(p => ThemePalette.Accents.Select(a => p.Name + "+" + a.Key))));

    private static ThemeSpec Theme(string name) => name.Split('+') is [var preset, var accent]
        ? ThemePalette.WithAccent(ThemePalette.Resolve(preset, 0, 0, 1), accent)
        : ThemePalette.Resolve(name, 0, 0, 1);

    [Theory]
    [MemberData(nameof(Presets))]
    public void Text_meets_wcag_contrast_on_every_surface(string preset)
    {
        var theme = Theme(preset);
        var colours = Colours();
        var failures = new List<string>();
        foreach (var (text, surfaces, min) in Pairs)
        {
            foreach (var fg in colours[text])
            {
                foreach (var surface in surfaces)
                {
                    foreach (var bg in colours[surface])
                    {
                        var ratio = Contrast(ThemePalette.Map(fg, theme), ThemePalette.Map(bg, theme));
                        if (ratio < min)
                        {
                            failures.Add($"{text} on {surface} #{ThemePalette.Map(bg, theme):X8}: {ratio:0.00}");
                        }
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// Cards are 90 % opaque over the page's hero (V3 backdrop A): text still reads where the brightest art, white at the
    /// hero's opacity over the plate, shows through a card.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void Text_reads_on_cards_over_the_brightest_hero(string preset)
    {
        var (heroOpacity, _, _) = Hero();
        var theme = Theme(preset);
        var colours = Colours();
        var behind = Over(0xFFFFFFFF, ThemePalette.Map(colours["Bg"][0], theme), heroOpacity);
        var failures = new List<string>();
        foreach (var text in new[] { "Text", "Muted", "Accent" })
        {
            foreach (var stop in colours["CardFill"])
            {
                var card = ThemePalette.Map(stop, theme);
                Assert.True(card >> 24 < 0xFF, "CardFill is meant to be translucent");
                var ratio = Contrast(ThemePalette.Map(colours[text][0], theme), Over(card, behind, (card >> 24) / 255.0));
                if (ratio < 4.5)
                {
                    failures.Add($"{text} on CardFill #{card:X8}: {ratio:0.00}");
                }
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// Text outside the cards - the page titles, their rule and the tab bars in the left column - stays readable over
    /// the page's hero at any width of the plate: the brightest art (white) at the hero's opacity, faded by
    /// PageHeroFade across the hero's box (0.8 of the plate, at most 940 wide, at the right), then the plate-coloured
    /// PageVignette and PageShade over it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void Text_beside_the_hero_reads_at_every_plate_width(string preset)
    {
        var (opacity, share, max) = Hero();
        var theme = Theme(preset);
        var root = XDocument.Load(RepoFile("src/Hearthpulse.App/Themes/Theme.xaml")).Root!;
        var fade = Stops(root, "PageHeroFade");
        var vignette = Stops(root, "PageVignette");
        var shade = Stops(root, "PageShade");
        var (cx, cy, rx, ry) = Radial(root, "PageVignette");
        var colours = Colours();
        var plate = ThemePalette.Map(colours["Bg"][0], theme);
        var failures = new List<string>();
        // The window's narrowest plate (1040 wide), the default (1260) and a wide one; the title column is the left 45 %
        foreach (var (width, height) in new[] { (790.0, 790.0), (1010.0, 790.0), (1500.0, 900.0) })
        {
            var heroWidth = Math.Min(max, share * width);
            for (var x = 0.0; x <= 0.45; x += 0.05)
            {
                for (var y = 0.0; y <= 0.2; y += 0.05)
                {
                    var inHero = (x * width) - (width - heroWidth);
                    var heroAlpha = inHero < 0 ? 0 : opacity * At(fade, inHero / heroWidth);
                    var d = Math.Sqrt(Math.Pow((x - cx) / rx, 2) + Math.Pow((y - cy) / ry, 2));
                    var back = Over(0xFFFFFFFF, plate, heroAlpha);
                    back = Over(ThemePalette.Map(vignette[^1].Color, theme), back, At(vignette, d));
                    back = Over(ThemePalette.Map(shade[^1].Color, theme), back, At(shade, y));
                    foreach (var text in new[] { "Text", "Muted" })
                    {
                        var ratio = Contrast(ThemePalette.Map(colours[text][0], theme), back);
                        if (ratio < 4.5)
                        {
                            failures.Add($"{text} at {x:0.00},{y:0.00} of a {width} plate: {ratio:0.00}");
                        }
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// The page hero's opacity, its share of the plate's width and its widest, read from MainWindow.xaml (the art under
    /// PageHeroFade in a column of a star share, capped); MainView.axaml must say the same.
    /// </summary>
    private static (double Opacity, double Share, double Max) Hero()
    {
        (double, double, double) Read(string path)
        {
            var art = XDocument.Load(RepoFile(path)).Descendants().Single(e => (string?)e.Attribute("OpacityMask") == "{StaticResource PageHeroFade}");
            var columns = art.Parent!.Descendants().Where(e => e.Name.LocalName == "ColumnDefinition").ToList();
            double Star(XElement c) => double.Parse(((string)c.Attribute("Width")!).TrimEnd('*') is { Length: > 0 } s ? s : "1", CultureInfo.InvariantCulture);
            return (double.Parse((string)art.Attribute("Opacity")!, CultureInfo.InvariantCulture),
                Star(columns[1]) / (Star(columns[0]) + Star(columns[1])),
                double.Parse((string)columns[1].Attribute("MaxWidth")!, CultureInfo.InvariantCulture));
        }

        var wpf = Read("src/Hearthpulse.App/Main/MainWindow.xaml");
        Assert.Equal(wpf, Read("src/Hearthpulse.UI/Main/MainView.axaml"));
        return wpf;
    }

    /// <summary>A gradient's stops (offset, colour) by key.</summary>
    private static List<(double Offset, uint Color)> Stops(XElement root, string key) =>
        root.Elements().Single(e => (string?)e.Attribute(X + "Key") == key).Elements()
            .Select(s => (double.Parse((string)s.Attribute("Offset")!, CultureInfo.InvariantCulture), Parse((string)s.Attribute("Color")!)))
            .ToList();

    /// <summary>A radial gradient's centre and radii, relative to the plate.</summary>
    private static (double Cx, double Cy, double Rx, double Ry) Radial(XElement root, string key)
    {
        var e = root.Elements().Single(b => (string?)b.Attribute(X + "Key") == key);
        var c = ((string)e.Attribute("Center")!).Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        return (c[0], c[1], double.Parse((string)e.Attribute("RadiusX")!, CultureInfo.InvariantCulture), double.Parse((string)e.Attribute("RadiusY")!, CultureInfo.InvariantCulture));
    }

    /// <summary>The alpha of a gradient (0..1) at <paramref name="offset"/>, linear between its stops.</summary>
    private static double At(List<(double Offset, uint Color)> stops, double offset)
    {
        double Alpha(uint c) => (c >> 24) / 255.0;
        if (offset <= stops[0].Offset)
        {
            return Alpha(stops[0].Color);
        }

        for (var i = 1; i < stops.Count; i++)
        {
            if (offset <= stops[i].Offset)
            {
                var t = (offset - stops[i - 1].Offset) / (stops[i].Offset - stops[i - 1].Offset);
                return Alpha(stops[i - 1].Color) + (t * (Alpha(stops[i].Color) - Alpha(stops[i - 1].Color)));
            }
        }

        return Alpha(stops[^1].Color);
    }

    /// <summary><paramref name="top"/> at <paramref name="alpha"/> over an opaque <paramref name="bottom"/>.</summary>
    private static uint Over(uint top, uint bottom, double alpha)
    {
        uint Channel(int shift) => (uint)Math.Round((((top >> shift) & 0xFF) * alpha) + (((bottom >> shift) & 0xFF) * (1 - alpha)));
        return 0xFF000000 | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    [Fact]
    public void Presets_differ_in_accent_second_accent_surface_and_motif()
    {
        var colours = Colours();
        var looks = ThemePalette.Presets.Select(p => (
            Accent: ThemePalette.Map(colours["Accent"][0], p),
            Second: ThemePalette.Map(colours["Accent2"][0], p),
            Surface: ThemePalette.Map(colours["Surface3"][0], p),
            p.Motif)).ToList();

        Assert.Equal(looks.Count, looks.Select(l => l.Second).Distinct().Count());
        Assert.Equal(looks.Count, looks.Select(l => l.Surface).Distinct().Count());
        Assert.Equal(looks.Count, looks.Select(l => l.Motif).Distinct().Count());
        // Shadow keeps Tavern's gold on purpose; every other accent is its own.
        Assert.Equal(looks.Count - 1, looks.Select(l => l.Accent).Distinct().Count());
    }

    /// <summary>Every colour token of Theme.xaml as 0xAARRGGBB values: one for a colour or brush, one per gradient stop.</summary>
    private static Dictionary<string, uint[]> Colours()
    {
        var root = XDocument.Load(RepoFile("src/Hearthpulse.App/Themes/Theme.xaml")).Root!;
        var named = root.Elements().Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => Parse(e.Value));
        uint Value(string text) => text.StartsWith('{') ? named[text.Split(' ')[1].TrimEnd('}')] : Parse(text);

        var colours = new Dictionary<string, uint[]>();
        foreach (var e in root.Elements())
        {
            var key = (string?)e.Attribute(X + "Key");
            uint[]? values = e.Name.LocalName switch
            {
                "Color" => [Parse(e.Value)],
                "SolidColorBrush" => [Value((string)e.Attribute("Color")!)],
                "LinearGradientBrush" or "RadialGradientBrush" => e.Elements().Select(s => Value((string)s.Attribute("Color")!)).ToArray(),
                _ => null,
            };
            if (key is not null && values is not null)
            {
                colours[key] = values;
            }
        }

        return colours;
    }

    private static uint Parse(string hex)
    {
        var value = uint.Parse(hex.Trim().AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return hex.Trim().Length == 7 ? value | 0xFF000000 : value;
    }

    private static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(uint argb)
    {
        static double Channel(byte v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel((byte)(argb >> 16))) + (0.7152 * Channel((byte)(argb >> 8))) + (0.0722 * Channel((byte)argb));
    }

    private static string RepoFile(string relative, [CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", relative));
}
