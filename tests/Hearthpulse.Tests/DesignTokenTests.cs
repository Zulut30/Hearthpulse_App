using System.Globalization;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Hearthpulse.Tests;

/// <summary>
/// D1 design scale (docs/design-identity.md): text sizes come from the FontSize* ramp tokens, corner radii and
/// spacing sit on the scale. Both theme files define the same tokens with the same values. The markup of both UIs is
/// scanned and what is still off the scale is counted per UI; the counts may only go down (a ratchet like the
/// coverage one). <c>tools/design/migrate_tokens.py</c> moves a file onto the scale with the same rules.
/// </summary>
public sealed class DesignTokenTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Spacing scale; any multiple of 4 from 40 up is a layout offset (page margins, the live bar's room).</summary>
    private static readonly double[] Spaces = [0, 1, 2, 4, 8, 12, 16, 24, 32];

    private static readonly double[] Radii = [0, 1, 2, 4, 8, 12, 16, 999];

    /// <summary>
    /// What is left off the scale per UI: literal FontSize values, off-scale CornerRadius values, off-scale
    /// Margin/Padding values. Lower these when a migration removes literals; never raise them.
    /// </summary>
    private static readonly Dictionary<string, (int Font, int Radius, int Space)> Baseline = new(StringComparer.Ordinal)
    {
        ["wpf"] = (397, 85, 631),
        ["avalonia"] = (408, 86, 627),
    };

    private static readonly string[] TokenElements = ["Double", "CornerRadius", "Thickness"];

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    private static Dictionary<string, string> Tokens(string relative) =>
        XDocument.Load(Path.Combine(RepoRoot(), relative)).Root!.Descendants()
            .Where(e => TokenElements.Contains(e.Name.LocalName) && e.Attribute(X + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(X + "Key")!, e => e.Value.Trim(), StringComparer.Ordinal);

    [Fact]
    public void Both_themes_define_the_same_scale_tokens_with_the_same_values()
    {
        var wpf = Tokens("src/Hearthpulse.App/Themes/Theme.xaml");
        var avalonia = Tokens("src/Hearthpulse.UI/Themes/Theme.axaml");
        Assert.Contains("FontSizeBody", wpf.Keys);
        Assert.Contains("RadiusMedium", wpf.Keys);
        Assert.Equal(wpf.OrderBy(p => p.Key, StringComparer.Ordinal), avalonia.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("wpf", "src/Hearthpulse.App")]
    [InlineData("avalonia", "src/Hearthpulse.UI")]
    public void Off_scale_literals_do_not_grow(string ui, string folder)
    {
        var (font, radius, space) = Count(folder);
        var limit = Baseline[ui];
        Assert.True(font <= limit.Font && radius <= limit.Radius && space <= limit.Space,
            $"{ui}: off-scale literals grew to FontSize {font}, CornerRadius {radius}, Margin/Padding {space} (limit {limit}). " +
            "Use the FontSize* / Radius* tokens and the 4/8/12/16/24/32 spacing scale, or run tools/design/migrate_tokens.py.");
        Assert.True(font == limit.Font && radius == limit.Radius && space == limit.Space,
            $"{ui}: off-scale literals went down to ({font}, {radius}, {space}); lower the baseline in DesignTokenTests to lock it in.");
    }

    [Fact]
    public void The_scanner_tells_scale_values_from_literals()
    {
        var doc = XElement.Parse("""
            <Grid xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <TextBlock FontSize="{StaticResource FontSizeBody}" Margin="0,4,8,16" />
              <TextBlock FontSize="13" Margin="0,6,40,120" />
              <Border Width="22" Height="22" CornerRadius="11" Padding="2" />
              <Border CornerRadius="6" Padding="10,3" />
              <Setter Property="FontSize" Value="12.5" />
              <Setter Property="CornerRadius" Value="8" />
            </Grid>
            """);
        Assert.Equal((2, 1, 3), Count([doc]));
    }

    private static (int Font, int Radius, int Space) Count(string folder)
    {
        var root = Path.Combine(RepoRoot(), folder);
        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !Path.GetFileName(f).StartsWith("Logo.", StringComparison.Ordinal));
        return Count(files.Select(f => XDocument.Load(f).Root!));
    }

    private static (int Font, int Radius, int Space) Count(IEnumerable<XElement> roots)
    {
        int font = 0, radius = 0, space = 0;
        foreach (var e in roots.SelectMany(r => r.DescendantsAndSelf()))
        {
            var setter = e.Name.LocalName == "Setter" ? (string?)e.Attribute("Property") : null;
            foreach (var a in e.Attributes())
            {
                var kind = setter is not null && a.Name.LocalName == "Value" ? setter : a.Name.LocalName;
                if (a.Value.StartsWith('{'))
                {
                    continue;
                }

                switch (kind)
                {
                    case "FontSize":
                        font++;
                        break;
                    case "CornerRadius" when setter is not null || !FullyRounded(e, a.Value):
                        radius += Numbers(a.Value).Count(v => !Radii.Contains(v));
                        break;
                    case "Margin" or "Padding":
                        space += Numbers(a.Value).Count(v => !OnSpacing(v));
                        break;
                }
            }
        }

        return (font, radius, space);
    }

    private static bool OnSpacing(double v)
    {
        var a = Math.Abs(v);
        return Spaces.Contains(a) || (a >= 40 && a % 4 == 0);
    }

    /// <summary>A pill or a circle: its radius follows the element's size, not the scale.</summary>
    private static bool FullyRounded(XElement e, string value)
    {
        var sizes = new[] { "Width", "Height" }.Select(n => (string?)e.Attribute(n)).OfType<string>().SelectMany(Numbers).ToList();
        var r = Numbers(value).ToList();
        return sizes.Count > 0 && r.Count == 1 && 2 * r[0] >= sizes.Min();
    }

    private static IEnumerable<double> Numbers(string value) =>
        value.Split(',', StringSplitOptions.TrimEntries)
            .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
            .Where(d => !double.IsNaN(d));
}
