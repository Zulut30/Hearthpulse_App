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
    /// <summary>Bound texts that may stay faint: the deck page's search placeholder (an empty box's prompt).</summary>
    private static readonly string[] FaintOnPurpose = ["{i:Loc Deck.Versions.SearchPlaceholder}"];

    [Fact]
    public void Bound_texts_are_not_painted_with_the_faint_brush_in_either_ui()
    {
        var root = RepoRoot();
        var files = new[] { ("src/Hearthpulse.App", "*.xaml"), ("src/Hearthpulse.UI", "*.axaml") }
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(root, p.Item1), p.Item2, SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/") && !f.Contains("/Themes/") && !f.Contains("/Main/Match/"))
            .ToList();
        Assert.Contains("src/Hearthpulse.App/Main/MainWindow.xaml", files);
        Assert.Contains("src/Hearthpulse.UI/Overlay/OverlayView.axaml", files);

        var faint = files
            .SelectMany(f => XDocument.Load(Path.Combine(root, f), LoadOptions.SetLineInfo).Descendants()
                .Where(e => e.Name.LocalName is "TextBlock" or "Run")
                .Where(e => (string?)e.Attribute("Foreground") == "{StaticResource Faint}")
                .Where(e => (string?)e.Attribute("Text") is { } text && (text.StartsWith("{Binding", StringComparison.Ordinal) || text.StartsWith("{i:Loc", StringComparison.Ordinal)))
                .Where(e => !FaintOnPurpose.Contains((string?)e.Attribute("Text")))
                .Select(e => $"{f}:{((System.Xml.IXmlLineInfo)e).LineNumber} {(string?)e.Attribute("Text")}"))
            .ToList();
        Assert.True(faint.Count == 0, "Readable text painted Faint (use Muted):" + Environment.NewLine + string.Join(Environment.NewLine, faint));
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
