using System.Text;
using System.Text.RegularExpressions;

namespace Hearthpulse.Core.Updates;

/// <summary>
/// The release body for the "What's new" block in Settings → About: GitHub markdown turned into plain text that wraps
/// (no headings marks, emphasis, links or HTML), at most <see cref="MaxLength"/> characters cut at a line end, and the
/// release page link only when it is this project's GitHub releases page.
/// </summary>
internal static partial class ReleaseNotes
{
    public const int MaxLength = 1500;

    public const string PagePrefix = "https://github.com/" + UpdateService.RepoOwner + "/" + UpdateService.RepoName + "/releases/";

    /// <summary>The body as plain text, or null when nothing is left.</summary>
    public static string? ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = HtmlComment().Replace(text, string.Empty);
        text = Image().Replace(text, string.Empty);
        text = Link().Replace(text, "$1");
        text = HtmlTag().Replace(text, string.Empty);

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (Rule().IsMatch(line) || line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            line = Heading().Replace(line, string.Empty);
            line = Bullet().Replace(line, "$1• ");
            line = Quote().Replace(line, string.Empty);
            line = Emphasis().Replace(line, string.Empty);
            line = line.Replace("`", string.Empty, StringComparison.Ordinal);
            line = line.Replace("&amp;", "&", StringComparison.Ordinal).Replace("&lt;", "<", StringComparison.Ordinal)
                .Replace("&gt;", ">", StringComparison.Ordinal).Replace("&nbsp;", " ", StringComparison.Ordinal);
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
            {
                continue;
            }

            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var plain = string.Join('\n', lines);
        return plain.Length == 0 ? null : Cut(plain);
    }

    /// <summary>The release page when it is this project's releases page on GitHub; null for anything else.</summary>
    public static Uri? Page(string? url) =>
        url is not null && url.StartsWith(PagePrefix, StringComparison.Ordinal) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            ? uri
            : null;

    /// <summary>At most <see cref="MaxLength"/> characters with the "…" mark, cut at the last line end that fits.</summary>
    private static string Cut(string text)
    {
        if (text.Length <= MaxLength)
        {
            return text;
        }

        var room = text[..(MaxLength - 1)];
        var end = room.LastIndexOf('\n');
        var kept = (end > 0 ? room[..end] : room).TrimEnd();
        return new StringBuilder(kept).Append('…').ToString();
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, 1000)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)", RegexOptions.None, 1000)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.None, 1000)]
    private static partial Regex Link();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>", RegexOptions.None, 1000)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^\s{0,3}([-*_]\s*){3,}$", RegexOptions.None, 1000)]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+", RegexOptions.None, 1000)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*+]\s+", RegexOptions.None, 1000)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s{0,3}>\s?", RegexOptions.None, 1000)]
    private static partial Regex Quote();

    [GeneratedRegex(@"(\*\*|__|~~)", RegexOptions.None, 1000)]
    private static partial Regex Emphasis();
}
