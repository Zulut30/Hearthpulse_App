namespace Hearthpulse.Core;

public static class AppPaths
{
    /// <summary>
    /// The portable zip ships a portable.txt next to the app: all data then lives in a "data" folder beside it.
    /// Declared before <see cref="Root"/>, which reads it.
    /// </summary>
    public static bool IsPortable { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    /// <summary>
    /// Data folder: %APPDATA%\Hearthpulse on Windows, ~/Library/Application Support/Hearthpulse on macOS,
    /// ~/.local/share/Hearthpulse on Linux, the app's private files on Android. HEARTHPULSE_DATA overrides it.
    /// The folder of the app's former name (Hearthlens) is copied here on first start, so history and settings carry over.
    /// </summary>
    public static string Root { get; } = Ensure(Environment.GetEnvironmentVariable("HEARTHPULSE_DATA") is { Length: > 0 } custom
        ? custom
        : IsPortable ? PortableRoot()
        : MigrateLegacy(Path.Combine(DefaultParent(), "Hearthlens"), Path.Combine(DefaultParent(), "Hearthpulse")));

    public static string Cache { get; } = Ensure(Path.Combine(Root, "cache"));

    public static string Images { get; } = Ensure(Path.Combine(Cache, "images"));

    public static string Settings => Path.Combine(Root, "settings.json");

    public static string Database => Path.Combine(Root, "history.db");

    public static string LogFile => Path.Combine(Root, "hearthpulse.log");

    /// <summary>One match replay file per game (<c>Replay/ReplayStore</c>); created on the first save.</summary>
    public static string Replays => Path.Combine(Root, "replays");

    /// <summary>
    /// Saved and imported replays (<c>Replay/ReplayLibrary</c>, <c>docs/replay-library.md</c>): Documents\Hearthpulse\Replays,
    /// outside the data folder so that its clean-up, updates and uninstalling never touch them. With HEARTHPULSE_DATA,
    /// in a portable copy or without a Documents folder: <c>&lt;data&gt;\library</c>. Created on the first save.
    /// </summary>
    public static string LibraryRoot { get; } = LibraryInData ? Path.Combine(Root, "library") : Path.Combine(Documents, "Hearthpulse", "Replays");

    /// <summary>Replays the player starred: copies kept forever.</summary>
    public static string SavedReplays => Path.Combine(LibraryRoot, LibraryInData ? "saved" : "Saved");

    /// <summary>Replays imported from a file or a Power.log, after the import filter.</summary>
    public static string ImportedReplays => Path.Combine(LibraryRoot, LibraryInData ? "imported" : "Imported");

    private static string Documents => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    private static bool LibraryInData => Environment.GetEnvironmentVariable("HEARTHPULSE_DATA") is { Length: > 0 } || IsPortable || Documents.Length == 0;

    /// <summary>Copies made for "Share", deleted after a day.</summary>
    public static string ShareTemp => Path.Combine(Cache, "share");

    // Next to the app. A macOS app bundle is never written to (that would break its signature), so there the
    // folder sits beside Hearthpulse.app instead.
    private static string PortableRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (dir is { Name: "MacOS", Parent: { Name: "Contents", Parent: { } bundle } }
            && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && bundle.Parent is { } outside)
        {
            return Path.Combine(outside.FullName, "Hearthpulse Data");
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }

    private static string DefaultParent()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        // .NET maps LocalApplicationData to ~/Library/Application Support on macOS and ~/.local/share on Linux.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return local.Length > 0 ? local : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    }

    /// <summary>
    /// Copies (never moves) the former app's settings, history and card cache, so an old copy that is still installed
    /// keeps working on its own data. Its logs and downloaded updates are left behind.
    /// </summary>
    private static string MigrateLegacy(string legacy, string path)
    {
        if (Directory.Exists(path) || !Directory.Exists(legacy))
        {
            return path;
        }

        var temp = path + ".migrating";
        try
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }

            CopyTree(new DirectoryInfo(legacy), temp, top: true);
            Directory.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A history file locked by the old app, for example: start fresh, the old folder stays untouched.
        }

        return path;
    }

    private static void CopyTree(DirectoryInfo from, string to, bool top)
    {
        Directory.CreateDirectory(to);
        foreach (var file in from.EnumerateFiles())
        {
            if (!top || !file.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                file.CopyTo(Path.Combine(to, file.Name));
            }
        }

        foreach (var dir in from.EnumerateDirectories())
        {
            if (!top || !dir.Name.Equals("updates", StringComparison.OrdinalIgnoreCase))
            {
                CopyTree(dir, Path.Combine(to, dir.Name), top: false);
            }
        }
    }

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>Tiny append-only diagnostic log.</summary>
public static class Diag
{
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO", message);

    /// <summary>Regular state reports (heartbeats), so a log shows what every part was doing when something stopped.</summary>
    public static void Debug(string message) => Write("DEBUG", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var file = new FileInfo(AppPaths.LogFile);
                if (file.Exists && file.Length > 4 * 1024 * 1024)
                {
                    // The previous file is kept, so a long session never loses the start of a problem.
                    File.Move(AppPaths.LogFile, Path.ChangeExtension(AppPaths.LogFile, ".old.log"), overwrite: true);
                }

                File.AppendAllText(AppPaths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never throw: it runs inside the crash handlers.
        }
    }
}
