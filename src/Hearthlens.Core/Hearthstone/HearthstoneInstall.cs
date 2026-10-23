using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Hearthlens.Core.Hearthstone;

/// <summary>
/// Locates the Hearthstone installation, its log folders, its log.config and the client locale on every platform:
/// Windows, macOS, Linux (Wine, Lutris, Bottles, Steam/Proton prefixes) and Android.
/// </summary>
public static class HearthstoneInstall
{
    /// <summary>Android package that owns the mobile client's external files (Logs, log.config).</summary>
    public const string AndroidPackage = "com.blizzard.wtcg.hearthstone";

    // Windows reports "Hearthstone"; Wine processes show their .exe name on Linux.
    private static readonly string[] ProcessNames = ["Hearthstone", "Hearthstone.exe"];

    private static readonly string[] WindowsFallbacks =
    [
        @"C:\Program Files (x86)\Hearthstone",
        @"C:\Program Files\Hearthstone",
        @"D:\Hearthstone",
        @"D:\Games\Hearthstone",
        @"E:\Hearthstone",
    ];

    public static bool IsRunning()
    {
        foreach (var name in ProcessNames)
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var p in processes) p.Dispose();
            if (processes.Length > 0)
            {
                return true;
            }
        }

        return false;
    }

    public static string? FindInstallDirectory(string? configured = null)
    {
        if (IsInstall(configured))
        {
            return configured;
        }

        if (FromRunningProcess() is { } running)
        {
            return running;
        }

        if (OperatingSystem.IsWindows() && FromRegistry() is { } registered)
        {
            return registered;
        }

        return CandidateDirectories().FirstOrDefault(IsInstall);
    }

    /// <summary>Where Hearthstone is usually installed on this platform, most likely first.</summary>
    public static IEnumerable<string> CandidateDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsFallbacks;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsAndroid())
        {
            return
            [
                $"/storage/emulated/0/Android/data/{AndroidPackage}/files",
                $"/sdcard/Android/data/{AndroidPackage}/files",
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return ["/Applications/Hearthstone", Path.Combine(home, "Applications", "Hearthstone")];
        }

        return WinePrefixes(home).SelectMany(InstallsInPrefix);
    }

    /// <summary>
    /// The log.config the client reads, or null when it cannot be located (e.g. Linux without a Wine install).
    /// </summary>
    public static string? LogConfigPathFor(string? installDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Blizzard", "Hearthstone", "log.config");
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Preferences", "Blizzard", "Hearthstone", "log.config");
        }

        if (OperatingSystem.IsAndroid())
        {
            return installDir is null ? null : Path.Combine(installDir, "log.config");
        }

        return installDir is null ? null : WineLogConfigPath(installDir, Environment.UserName);
    }

    /// <summary>
    /// log.config inside the Wine prefix that holds <paramref name="installDir"/>: an existing one wins, else the
    /// profile of the Linux user, the Proton user, or any other Windows user in the prefix.
    /// </summary>
    public static string? WineLogConfigPath(string installDir, string userName)
    {
        var marker = installDir.Replace('\\', '/').IndexOf("/drive_c/", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var users = Path.Combine(installDir[..marker], "drive_c", "users");
        if (!Directory.Exists(users))
        {
            return null;
        }

        static string ConfigOf(string profile) =>
            Path.Combine(profile, "AppData", "Local", "Blizzard", "Hearthstone", "log.config");

        var profiles = Directory.EnumerateDirectories(users)
            .Where(d => !string.Equals(Path.GetFileName(d), "Public", StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => Path.GetFileName(d) == userName ? 0 : Path.GetFileName(d) == "steamuser" ? 1 : 2)
            .ThenBy(d => d, StringComparer.Ordinal)
            .ToList();
        var existing = profiles.Select(ConfigOf).FirstOrDefault(File.Exists);
        return existing ?? (profiles.Count > 0 ? ConfigOf(profiles[0]) : null);
    }

    /// <summary>Maps a Windows path seen inside a Wine prefix (e.g. C:\Program Files\…) to the Linux path.</summary>
    public static string? MapWinePath(string windowsPath, string prefix)
    {
        if (windowsPath.Length < 3 || windowsPath[1] != ':' || windowsPath[2] is not ('\\' or '/') || !char.IsLetter(windowsPath[0]))
        {
            return null;
        }

        // Wine prefixes are Linux paths, so join with '/' whatever the host.
        var drive = char.ToLowerInvariant(windowsPath[0]);
        var root = prefix.TrimEnd('/') + (drive == 'c' ? "/drive_c" : $"/dosdevices/{drive}:");
        var rest = windowsPath[3..].Replace('\\', '/').TrimEnd('/');
        return rest.Length == 0 ? root : $"{root}/{rest}";
    }

    /// <summary>Newest Hearthstone_yyyy_MM_dd_HH_mm_ss session folder.</summary>
    public static string? LatestLogSession(string installDir)
    {
        var logs = Path.Combine(installDir, "Logs");
        if (!Directory.Exists(logs))
        {
            return null;
        }

        return Directory.EnumerateDirectories(logs, "Hearthstone_*")
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static IEnumerable<string> AllLogSessions(string installDir)
    {
        var logs = Path.Combine(installDir, "Logs");
        return Directory.Exists(logs)
            ? Directory.EnumerateDirectories(logs, "Hearthstone_*").OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
            : Enumerable.Empty<string>();
    }

    /// <summary>Session start time encoded in the folder name.</summary>
    public static DateTime? SessionStart(string sessionDir)
    {
        var name = Path.GetFileName(sessionDir);
        return DateTime.TryParseExact(name, "'Hearthstone_'yyyy_MM_dd_HH_mm_ss", null,
            System.Globalization.DateTimeStyles.None, out var start)
            ? start
            : null;
    }

    /// <summary>Picks the installed text locale, preferring a non-English one when present.</summary>
    public static string DetectLocale(string? installDir)
    {
        var strings = installDir is null ? null : Path.Combine(installDir, "Strings");
        if (strings is null || !Directory.Exists(strings))
        {
            return "enUS";
        }

        var locales = Directory.EnumerateDirectories(strings).Select(Path.GetFileName).OfType<string>()
            .Where(n => n.Length == 4).ToList();
        return locales.FirstOrDefault(l => l != "enUS") ?? "enUS";
    }

    /// <summary>A folder holding the desktop client (Hearthstone.exe or Hearthstone.app) or the Android client's files.</summary>
    public static bool IsInstall(string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && (File.Exists(Path.Combine(dir, "Hearthstone.exe"))
            || Directory.Exists(Path.Combine(dir, "Hearthstone.app"))
            || (dir.Contains(AndroidPackage, StringComparison.Ordinal) && Directory.Exists(dir)));

    private static string? FromRunningProcess()
    {
        foreach (var name in ProcessNames)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        var executable = OperatingSystem.IsLinux() ? WineExecutable(process.Id) : process.MainModule?.FileName;
                        // macOS reports …/Hearthstone.app/Contents/MacOS/Hearthstone, so walk up to the install folder.
                        for (var dir = Path.GetDirectoryName(executable); dir is not null; dir = Path.GetDirectoryName(dir))
                        {
                            if (IsInstall(dir))
                            {
                                return dir;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                                   or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Linux path of a Wine process's Windows executable, from its command line and WINEPREFIX.</summary>
    private static string? WineExecutable(int pid)
    {
        var cmdline = File.ReadAllText($"/proc/{pid}/cmdline").Split('\0');
        var exe = cmdline.FirstOrDefault(a => a.EndsWith("Hearthstone.exe", StringComparison.OrdinalIgnoreCase));
        if (exe is null)
        {
            return null;
        }

        if (exe.StartsWith('/'))
        {
            return exe;
        }

        var environment = File.ReadAllText($"/proc/{pid}/environ").Split('\0');
        var prefix = environment.FirstOrDefault(e => e.StartsWith("WINEPREFIX=", StringComparison.Ordinal))?["WINEPREFIX=".Length..]
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
        return MapWinePath(exe, prefix);
    }

    [SupportedOSPlatform("windows")]
    private static string? FromRegistry()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var key in new[]
                     {
                         @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone",
                         @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Hearthstone",
                     })
            {
                using var reg = hive.OpenSubKey(key);
                if (reg?.GetValue("InstallLocation") is string location && IsInstall(location))
                {
                    return location;
                }
            }
        }

        return null;
    }

    /// <summary>Wine prefixes used by plain Wine, Lutris, Bottles and Steam (Proton), as far as they exist.</summary>
    private static IEnumerable<string> WinePrefixes(string home)
    {
        if (Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } configured)
        {
            yield return configured;
        }

        yield return Path.Combine(home, ".wine");

        string[] parents =
        [
            Path.Combine(home, "Games"),
            Path.Combine(home, ".local", "share", "lutris", "prefixes"),
            Path.Combine(home, ".local", "share", "bottles", "bottles"),
            Path.Combine(home, ".var", "app", "com.usebottles.bottles", "data", "bottles", "bottles"),
            Path.Combine(home, ".var", "app", "net.lutris.Lutris", "data", "lutris", "prefixes"),
        ];
        foreach (var parent in parents.Where(Directory.Exists))
        {
            foreach (var prefix in SafeSubdirectories(parent))
            {
                yield return prefix;
            }
        }

        string[] steamRoots =
        [
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        ];
        foreach (var compat in steamRoots.Select(r => Path.Combine(r, "steamapps", "compatdata")).Where(Directory.Exists))
        {
            foreach (var game in SafeSubdirectories(compat))
            {
                yield return Path.Combine(game, "pfx");
            }
        }
    }

    private static IEnumerable<string> InstallsInPrefix(string prefix)
    {
        yield return Path.Combine(prefix, "drive_c", "Program Files (x86)", "Hearthstone");
        yield return Path.Combine(prefix, "drive_c", "Program Files", "Hearthstone");
    }

    private static IEnumerable<string> SafeSubdirectories(string parent)
    {
        try
        {
            return Directory.GetDirectories(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}

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
    public static IReadOnlyList<string> MissingSections(string? installDir)
    {
        if (HearthstoneInstall.LogConfigPathFor(installDir) is not { } path)
        {
            return Array.Empty<string>();
        }

        var sections = Read(path);
        return Required.Where(r => !sections.TryGetValue(r.Section, out var values) || !IsSatisfied(values, r.Verbose))
            .Select(r => r.Section).ToList();
    }

    /// <summary>Adds or fixes required sections; other sections are preserved. Returns true if the file changed.</summary>
    public static bool Ensure(string? installDir)
    {
        if (HearthstoneInstall.LogConfigPathFor(installDir) is not { } path)
        {
            return false;
        }

        var sections = Read(path);
        var changed = false;
        foreach (var (section, verbose) in Required)
        {
            if (sections.TryGetValue(section, out var values) && IsSatisfied(values, verbose))
            {
                continue;
            }

            sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["LogLevel"] = "1",
                ["FilePrinting"] = "True",
                ["ConsolePrinting"] = "False",
                ["ScreenPrinting"] = "False",
                ["Verbose"] = verbose ? "True" : "False",
            };
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            else if (current is not null && line.IndexOf('=') is > 0 and var eq)
            {
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return result;
    }
}

/// <summary>
/// Hearthstone closes a log file for the rest of its session once the file passes Log.FileSizeLimit.Int kilobytes
/// (default 10000, about 10 MB, checked every 100 writes). A Battlegrounds Power.log passes 10 MB around turn 8, so
/// without this setting the log (and every tracker reading it) stops there with no error anywhere. The client reads
/// the limit from client.config in its working folder (the install folder) when it starts; -1 removes the limit.
/// </summary>
public static class ClientConfig
{
    public const int DefaultLimitKilobytes = 10000;
    private const string Section = "Log";
    private const string Key = "FileSizeLimit.Int";

    public static string? PathFor(string? installDir) =>
        string.IsNullOrEmpty(installDir) || OperatingSystem.IsAndroid() ? null : Path.Combine(installDir, "client.config");

    /// <summary>The log size limit in kilobytes the file asks for (-1: none), or null when the install folder is unknown.</summary>
    public static int? LogSizeLimit(string? installDir)
    {
        if (PathFor(installDir) is not { } path)
        {
            return null;
        }

        if (!File.Exists(path))
        {
            return DefaultLimitKilobytes;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var inSection = false;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Trim().Equals(Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inSection && line.IndexOf('=') is > 0 and var eq && line[..eq].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(line[(eq + 1)..].Trim(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : DefaultLimitKilobytes;
            }
        }

        return DefaultLimitKilobytes;
    }

    /// <summary>
    /// True when the client has a log size limit: the file asks for one, or the running client started before the
    /// file was fixed and still uses the old limit. False when the install folder is unknown.
    /// </summary>
    public static bool LimitActive(string? installDir) => LogSizeLimit(installDir) switch
    {
        null => false,
        -1 => ClientStartTime() is { } started && PathFor(installDir) is { } path && File.GetLastWriteTime(path) > started,
        _ => true,
    };

    private static DateTime? ClientStartTime()
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("Hearthstone");
            try
            {
                return processes.Length == 0 ? null : processes[0].StartTime;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Sets Log.FileSizeLimit.Int=-1 and keeps everything else in the file. True when the file changed.</summary>
    public static bool Ensure(string? installDir)
    {
        if (PathFor(installDir) is not { } path || !Directory.Exists(installDir) || LogSizeLimit(installDir) == -1)
        {
            return false;
        }

        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var setting = Key + "=-1";
        var section = -1;
        var replaced = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (section >= 0)
                {
                    break;
                }

                if (line[1..^1].Trim().Equals(Section, StringComparison.OrdinalIgnoreCase))
                {
                    section = i;
                }

                continue;
            }

            if (section >= 0 && line.IndexOf('=') is > 0 and var eq && line[..eq].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = setting;
                replaced = true;
                break;
            }
        }

        if (!replaced)
        {
            if (section >= 0)
            {
                lines.Insert(section + 1, setting);
            }
            else
            {
                lines.Add("[" + Section + "]");
                lines.Add(setting);
            }
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, string.Join(newline, lines) + newline);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    public static string Describe(string? installDir) => LogSizeLimit(installDir) switch
    {
        null => "unknown (install folder not found)",
        -1 => "none",
        var kb => $"{kb:N0} KB",
    };
}
