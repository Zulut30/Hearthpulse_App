using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Hearthpulse.Tests;

/// <summary>
/// Text the player has to read (notes, captions, statuses) is painted <c>Muted</c>, which keeps WCAG AA on every
/// surface and theme (ThemeContrastTests). <c>Faint</c> is below that and stays for separators, glyphs, an empty search
/// box's placeholder and disabled things. Read from the markup of both UIs; themes and the match page (its own session)
/// are not scanned.
/// </summary>
public sealed class ReadableTextTests
{
    private const string Faint = "{StaticResource Faint}";

    /// <summary>Bound texts that may stay faint: the deck page's search placeholder (an empty box's prompt).</summary>
    private static readonly string[] FaintOnPurpose = ["{i:Loc Deck.Versions.SearchPlaceholder}"];

    [Fact]
    public void Bound_texts_are_not_painted_with_the_faint_brush_in_either_ui()
    {
        var faint = Markup()
            .SelectMany(f => f.Root.Descendants()
                .Where(e => e.Name.LocalName is "TextBlock" or "Run")
                .Where(e => (string?)e.Attribute("Foreground") == Faint)
                .Where(Readable)
                .Select(e => Where(f.Path, e)))
            .ToList();
        Assert.True(faint.Count == 0, "Readable text painted Faint (use Muted):" + Environment.NewLine + string.Join(Environment.NewLine, faint));
    }

    /// <summary>
    /// The same rule through a style: a WPF text's own Style whose base setter is Faint (a trigger may still dim it), or an
    /// Avalonia class whose plain <c>TextBlock.name</c> style in the same file is Faint (the turn timer's detail was both).
    /// </summary>
    [Fact]
    public void Bound_texts_do_not_take_the_faint_brush_from_their_style_in_either_ui()
    {
        var faint = new List<string>();
        foreach (var (path, root) in Markup())
        {
            foreach (var text in root.Descendants().Where(e => e.Name.LocalName == "TextBlock").Where(Readable))
            {
                var own = text.Elements().Where(e => e.Name.LocalName == "TextBlock.Style").Elements().Where(e => e.Name.LocalName == "Style");
                if (own.Elements().Any(FaintSetter))
                {
                    faint.Add(Where(path, text));
                }
            }

            var faintClasses = root.Descendants()
                .Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute("Selector") is { } selector
                            && selector.StartsWith("TextBlock.", StringComparison.Ordinal) && selector.IndexOfAny([' ', ',', ':', '>', '/']) < 0
                            && selector.Count(c => c == '.') == 1)
                .Where(e => e.Elements().Any(FaintSetter))
                .Select(e => ((string)e.Attribute("Selector")!)["TextBlock.".Length..])
                .ToHashSet(StringComparer.Ordinal);
            faint.AddRange(root.Descendants()
                .Where(e => e.Name.LocalName == "TextBlock" && Readable(e))
                .Where(e => ((string?)e.Attribute("Classes") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(faintClasses.Contains))
                .Select(e => Where(path, e)));
        }

        Assert.True(faint.Count == 0, "Readable text painted Faint by its style (use Muted):" + Environment.NewLine + string.Join(Environment.NewLine, faint));

        static bool FaintSetter(XElement setter) =>
            setter.Name.LocalName == "Setter" && (string?)setter.Attribute("Property") == "Foreground" && (string?)setter.Attribute("Value") == Faint;
    }

    private static bool Readable(XElement e) =>
        (string?)e.Attribute("Text") is { } text
        && (text.StartsWith("{Binding", StringComparison.Ordinal) || text.StartsWith("{i:Loc", StringComparison.Ordinal))
        && !FaintOnPurpose.Contains(text);

    private static string Where(string path, XElement e) => $"{path}:{((System.Xml.IXmlLineInfo)e).LineNumber} {(string?)e.Attribute("Text")}";

    /// <summary>Every view of both UIs but the themes and the match page, parsed with line numbers.</summary>
    private static List<(string Path, XElement Root)> Markup()
    {
        var root = RepoRoot();
        var files = new[] { ("src/Hearthpulse.App", "*.xaml"), ("src/Hearthpulse.UI", "*.axaml") }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p.Item1), p.Item2, SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/") && !f.Contains("/Themes/") && !f.Contains("/Main/Match/"))
            .ToList();
        Assert.Contains("src/Hearthpulse.App/Main/MainWindow.xaml", files);
        Assert.Contains("src/Hearthpulse.UI/Overlay/OverlayView.axaml", files);
        return files.Select(f => (f, XDocument.Load(Path.Combine(root, f), LoadOptions.SetLineInfo).Root!)).ToList();
    }

    private static string RepoRoot([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hearthpulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (Hearthpulse.slnx) not found above the test sources.");
    }
}
