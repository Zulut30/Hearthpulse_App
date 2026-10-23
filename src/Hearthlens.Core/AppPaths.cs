namespace Hearthlens.Core;

public static class AppPaths
{
    /// <summary>
    /// Data folder: %APPDATA%\Hearthlens on Windows, ~/Library/Application Support/Hearthlens on macOS,
    /// ~/.local/share/Hearthlens on Linux, the app's private files on Android. HEARTHLENS_DATA overrides it.
    /// </summary>
    public static string Root { get; } = Ensure(Environment.GetEnvironmentVariable("HEARTHLENS_DATA") is { Length: > 0 } custom
        ? custom
        : Path.Combine(DefaultParent(), "Hearthlens"));

    public static string Cache { get; } = Ensure(Path.Combine(Root, "cache"));

    public static string Images { get; } = Ensure(Path.Combine(Cache, "images"));

    public static string Settings => Path.Combine(Root, "settings.json");

    public static string Database => Path.Combine(Root, "history.db");

    public static string LogFile => Path.Combine(Root, "hearthlens.log");

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

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>Tiny append-only diagnostic log.</summary>
public static class Diag
{
    private static readonly object Gate = new();

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
        catch (IOException)
        {
        }
    }
}
