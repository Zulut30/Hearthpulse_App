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
        ("Text", ["Bg", "Surface", "Surface2", "Surface3", "Surface4", "Row", "CardFill", "GhostFill", "SelectedFill", "OverlayCardFill"], 4.5),
        ("Muted", ["Bg", "Surface", "Surface2", "Surface3", "Surface4", "Row", "CardFill", "OverlayCardFill"], 4.5),
        ("Accent", ["Bg", "Surface", "Surface2", "CardFill", "SelectedFill", "OverlayCardFill"], 4.5),
        ("OnAccent", ["Accent", "PrimaryFill", "PrimaryFillHover"], 4.5),
        ("OnSecondaryContainer", ["SecondaryContainer", "GhostFill"], 4.5),
        ("OnAccentContainer", ["AccentContainer"], 4.5),
        ("Faint", ["Bg", "Surface", "Surface2", "CardFill"], 3.0),
    ];

    public static TheoryData<string> Presets() => new(ThemePalette.Presets.Select(p => p.Name));

    [Theory]
    [MemberData(nameof(Presets))]
    public void Text_meets_wcag_contrast_on_every_surface(string preset)
    {
        var theme = ThemePalette.Resolve(preset, 0, 0, 1);
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
