using System.Runtime.Versioning;
using System.Security;
using Hearthlens.Core;
using Microsoft.Win32;

namespace Hearthlens.Desktop.Platform;

/// <summary>
/// Starts Hearthlens when the user signs in: the Run key on Windows, a LaunchAgent on macOS, an XDG autostart entry
/// on Linux.
/// </summary>
internal static class Autostart
{
    private const string Name = "Hearthlens";

    public static void Apply(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyWindows(enabled);
            }
            else if (OperatingSystem.IsMacOS())
            {
                var plist = Path.Combine(Home, "Library", "LaunchAgents", "app.hearthlens.desktop.plist");
                WriteOrDelete(plist, enabled, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                        <key>Label</key><string>app.hearthlens.desktop</string>
                        <key>ProgramArguments</key><array><string>{SecurityElement.Escape(Executable)}</string></array>
                        <key>RunAtLoad</key><true/>
                    </dict>
                    </plist>
                    """);
            }
            else if (OperatingSystem.IsLinux())
            {
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config");
                WriteOrDelete(Path.Combine(config, "autostart", "hearthlens.desktop"), enabled, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=Hearthlens
                    Comment=Hearthstone companion: deck tracker and Battlegrounds advisor
                    Exec="{Executable}"
                    Icon=hearthlens
                    Terminal=false
                    X-GNOME-Autostart-enabled=true
                    """);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            Diag.Error("autostart registration failed", ex);
        }
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string Executable => Environment.ProcessPath ?? Name;

    private static void WriteOrDelete(string path, bool enabled, string content)
    {
        if (!enabled)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return;
        }

        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        var command = $"\"{Executable}\"";
        if (enabled)
        {
            // Rewrite only when the path changed (first enable, or the app was moved).
            if (!string.Equals(key.GetValue(Name) as string, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(Name, command);
            }
        }
        else if (key.GetValue(Name) is not null)
        {
            key.DeleteValue(Name, false);
        }
    }
}
