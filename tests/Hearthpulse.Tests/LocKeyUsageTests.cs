using System.Text.RegularExpressions;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Tests;

/// <summary>
/// The other half of <see cref="LocalizationTests.Every_key_used_in_the_code_exists"/>: every interface string is used
/// somewhere, so the tables hold no texts nobody sees (a translator still reads them).
/// </summary>
public sealed class LocKeyUsageTests
{
    /// <summary>
    /// A key prefix completed at run time: <c>"Core.Counter." + id</c> or <c>$"Pool.Mechanic.{tag}"</c>. At least two
    /// parts, so a bare <c>"Core."</c> could never excuse a whole file.
    /// </summary>
    private static readonly Regex RuntimePrefix = new(@"""([A-Z][A-Za-z]*(?:\.[A-Za-z0-9]+)+\.)(?:\{|""\s*\+)", RegexOptions.CultureInvariant);

    /// <summary>A dotted name as a whole: <c>Match.View.Next</c> must not count as used because <c>Match.View.NextTurn</c> is.</summary>
    private static readonly Regex DottedName = new(@"[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z0-9_-]+)+", RegexOptions.CultureInvariant);

    /// <summary>
    /// Unused keys left for now, each with its reason. <c>Stats.Bg.Placements</c>: only its <c>…Tip</c> is shown; it sits in
    /// <c>stats.json</c>, which TASK-2026-10-29-ux-round2 is editing. Delete the key from both languages and this entry once
    /// that branch is merged.
    /// </summary>
    private static readonly string[] KnownUnused = ["Stats.Bg.Placements"];

    [Fact]
    public void Every_key_is_used_in_the_code()
    {
        var code = string.Join('\n', RepoFiles.Sources(".cs", ".xaml", ".axaml").Select(File.ReadAllText));
        var names = DottedName.Matches(code).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var prefixes = RuntimePrefix.Matches(code).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.NotEmpty(prefixes);

        var unused = Loc.Table(Loc.English).Keys
            .Where(key => !names.Contains(key) && !prefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            .Except(KnownUnused, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unused.Count == 0, "Keys no code uses (delete them from every language): " + string.Join(", ", unused));
    }
}
