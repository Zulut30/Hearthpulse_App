using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Hearthpulse.Core.Hearthstone;

/// <summary>Ensures the client writes the log sections the tracker reads.</summary>
public static class LogConfig
{
    private static readonly (string Section, bool Verbose)[] Required =
    [
        ("Power", true),
        ("LoadingScreen", false),
        ("Decks", false),
        ("Arena", false),
        ("FullScreenFX", false),
        ("Achievements", false),
    ];

    /// <summary>
    /// Makes Hearthstone write the logs the tracker needs: the log.config sections and, in client.config, no log size
    /// limit. Writes what it found to the diagnostic log. True when Hearthstone must restart to pick the change up.
    /// </summary>
    public static bool EnsureAll(string? installDir)
    {
        var changed = false;
        try
        {
            changed |= Ensure(installDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("log.config not writable", ex);
        }

        try
        {
            changed |= ClientConfig.Ensure(installDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error($"client.config not writable ({ClientConfig.PathFor(installDir)}): Hearthstone will stop its log at 10 MB, around turn 8", ex);
        }

        var missing = MissingSections(installDir);
        Diag.Info($"Hearthstone setup: install {installDir ?? "not found"}; log.config {(missing.Count > 0 ? "missing " + string.Join(", ", missing) : "ok")} "
                  + $"({HearthstoneInstall.LogConfigPathFor(installDir)}); client.config log size limit {ClientConfig.Describe(installDir)}"
                  + (changed ? "; changed, Hearthstone must restart once" : string.Empty));
        return changed;
    }

    /// <summary>Required sections missing from the client's log.config (empty when its location is unknown).</summary>
    public static IReadOnlyList<string> MissingSections(string? installDir) =>
        HearthstoneInstall.LogConfigPathFor(installDir) is { } path ? MissingSectionsIn(path) : Array.Empty<string>();

    /// <summary>Required sections missing from the log.config at <paramref name="path"/> (all of them when it does not exist).</summary>
    public static IReadOnlyList<string> MissingSectionsIn(string path)
    {
        var sections = Read(path);
        return Required.Where(r => !sections.TryGetValue(r.Section, out var values) || !IsSatisfied(values, r.Verbose))
            .Select(r => r.Section).ToList();
    }

    /// <summary>Adds or fixes required sections; other sections are preserved. Returns true if the file changed.</summary>
    public static bool Ensure(string? installDir) =>
        HearthstoneInstall.LogConfigPathFor(installDir) is { } path && EnsureIn(path);

    /// <summary>
    /// The same on the log.config at <paramref name="path"/> (created with its folder when missing). A section's other
    /// keys are kept, and the file as it was is copied to log.config.bak before it is written.
    /// </summary>
    public static bool EnsureIn(string path)
    {
        var sections = Read(path);
        var changed = false;
        foreach (var (section, verbose) in Required)
        {
            if (sections.TryGetValue(section, out var values) && IsSatisfied(values, verbose))
            {
                continue;
            }

            if (!sections.TryGetValue(section, out values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[section] = values;
            }

            values["LogLevel"] = "1";
            values["FilePrinting"] = "True";
            values["ConsolePrinting"] = "False";
            values["ScreenPrinting"] = "False";
            values["Verbose"] = verbose ? "True" : "False";
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            File.Copy(path, path + ".bak", overwrite: true);
        }

        using var writer = new StreamWriter(path, append: false);
        foreach (var (name, values) in sections)
        {
            writer.WriteLine($"[{name}]");
            foreach (var (key, value) in values)
            {
                writer.WriteLine($"{key}={value}");
            }
        }

        return true;
    }

    private static bool IsSatisfied(Dictionary<string, string> values, bool verbose) =>
        values.TryGetValue("FilePrinting", out var printing) && printing.Equals("True", StringComparison.OrdinalIgnoreCase)
        && values.TryGetValue("LogLevel", out var level) && level == "1"
        && (!verbose || (values.TryGetValue("Verbose", out var v) && v.Equals("True", StringComparison.OrdinalIgnoreCase)));

    private static Dictionary<string, Dictionary<string, string>> Read(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        Dictionary<string, string>? current = null;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[line[1..^1]] = current;
            }
            else if (current is not null && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq)
            {
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return result;
    }
}
