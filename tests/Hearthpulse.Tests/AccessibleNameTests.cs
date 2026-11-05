using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Hearthpulse.Tests;

/// <summary>
/// Etalon D2: a button that shows only an icon has an accessible name (WCAG 4.1.2), or Narrator reads it as a bare
/// "button". The markup of both UIs is scanned: a <c>Button</c>, <c>ToggleButton</c> or <c>RepeatButton</c> whose
/// content is an MDI glyph, or that wears the icon-button style, needs <c>AutomationProperties.Name</c>. A tooltip is
/// not a name: UI Automation reads it as help text.
/// </summary>
public sealed partial class AccessibleNameTests
{
    private static readonly string[] Buttons = ["Button", "ToggleButton", "RepeatButton"];

    private static readonly string[] TextElements = ["TextBlock", "Run", "Label", "SelectableTextBlock", "AccessText"];

    private static string RepoRoot([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", ".."));

    [Theory]
    [InlineData("src/Hearthpulse.App")]
    [InlineData("src/Hearthpulse.UI")]
    public void Every_icon_only_button_has_an_accessible_name(string folder)
    {
        var root = Path.Combine(RepoRoot(), folder);
        var unnamed = Directory.EnumerateFiles(root, "*.*xaml", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Unnamed(XDocument.Load(f, LoadOptions.SetLineInfo).Root!)
                .Select(e => $"{Path.GetRelativePath(root, f)}:{((System.Xml.IXmlLineInfo)e).LineNumber}"))
            .ToList();
        Assert.True(unnamed.Count == 0,
            "Icon-only buttons without AutomationProperties.Name=\"{i:Loc Key}\" (the same key in both UIs):\n" + string.Join('\n', unnamed));
    }

    [Fact]
    public void The_scanner_tells_icon_only_buttons_from_buttons_with_text()
    {
        var doc = XElement.Parse("""
            <Grid xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Button x:Name="glyph" Content="&#xF0156;" ToolTip="Hide" />
              <Button x:Name="styled" Style="{StaticResource IconButton}" Content="{Binding Glyph}" />
              <Button x:Name="classed" Classes="icon hideCounter" />
              <Button x:Name="glyphChild"><TextBlock Text="{Binding PlayGlyph}" Classes="icon" /></Button>
              <ToggleButton x:Name="toggle"><TextBlock Text="&#xF033F;" /></ToggleButton>
              <Button Content="&#xF0156;" AutomationProperties.Name="{i:Loc Overlay.Counter.HideName}" />
              <Button Content="Save" />
              <Button Content="{Binding Label}" />
              <Button><StackPanel><TextBlock Text="&#xF033F;" Style="{StaticResource Icon}" /><TextBlock Text="{i:Loc Overlay.Lock.Unlock}" /></StackPanel></Button>
              <Button />
              <Button.Template><ControlTemplate /></Button.Template>
            </Grid>
            """);
        Assert.Equal(["glyph", "styled", "classed", "glyphChild", "toggle"],
            Unnamed(doc).Select(e => (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))));
    }

    private static IEnumerable<XElement> Unnamed(XElement root) =>
        root.DescendantsAndSelf().Where(e => Buttons.Contains(e.Name.LocalName) && IconOnly(e) && !Named(e));

    private static bool Named(XElement button) =>
        button.Attributes().Any(a => a.Name.LocalName is "AutomationProperties.Name" or "AutomationProperties.LabeledBy")
        || button.Elements().Any(c => c.Name.LocalName == "AutomationProperties.Name");

    private static bool IconOnly(XElement button)
    {
        var style = (string?)button.Attribute("Style") ?? string.Empty;
        var classes = (string?)button.Attribute("Classes") ?? string.Empty;
        if (style.Contains("IconButton", StringComparison.Ordinal) || IconClass().IsMatch(classes))
        {
            return true;
        }

        if (button.Attribute("Content") is { } attribute)
        {
            return IsGlyph(attribute.Value);
        }

        var content = button.Elements().Where(c => !c.Name.LocalName.Contains('.', StringComparison.Ordinal)).ToList();
        return content.Count > 0 && content.SelectMany(c => c.DescendantsAndSelf())
            .Where(d => TextElements.Contains(d.Name.LocalName))
            .All(IsIconText);
    }

    private static bool IsIconText(XElement text) =>
        IsGlyph((string?)text.Attribute("Text") ?? text.Value)
        || ((string?)text.Attribute("Style") ?? string.Empty).Contains("Icon", StringComparison.Ordinal)
        || IconClass().IsMatch((string?)text.Attribute("Classes") ?? string.Empty)
        || ((string?)text.Attribute("FontFamily") ?? string.Empty).Contains("IconFont", StringComparison.Ordinal);

    /// <summary>Material Design Icons glyphs sit in the private use areas.</summary>
    private static bool IsGlyph(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        for (var i = 0; i < trimmed.Length; i += char.IsSurrogatePair(trimmed, i) ? 2 : 1)
        {
            var c = char.ConvertToUtf32(trimmed, i);
            if (c is not ((>= 0xE000 and <= 0xF8FF) or >= 0xF0000))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"(^|\s)icon(\s|$)")]
    private static partial Regex IconClass();
}
