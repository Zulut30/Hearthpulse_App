using System.Runtime.CompilerServices;

namespace Hearthpulse.Tests;

/// <summary>The repository the tests run from: its root and its source files, for tests that read the code itself.</summary>
internal static class RepoFiles
{
    /// <summary>The folder with <c>Hearthpulse.slnx</c>, found above this source file.</summary>
    public static string Root { get; } = FindRoot();

    /// <summary>The files under <c>src/</c> with one of <paramref name="extensions"/>, outside build output.</summary>
    public static IEnumerable<string> Sources(params string[] extensions) =>
        Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => extensions.Any(e => f.EndsWith(e, StringComparison.Ordinal)))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string FindRoot([CallerFilePath] string source = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Hearthpulse.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root (Hearthpulse.slnx) not found above the test sources.");
    }
}
