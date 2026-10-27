using System.Runtime.Versioning;
using System.Security;
using Hearthpulse.Core;
using Hearthpulse.Core.Updates;
using Microsoft.Win32;

namespace Hearthpulse.Desktop.Platform;

/// <summary>
/// Starts Hearthpulse when the user signs in: the Run key on Windows, a LaunchAgent on macOS, an XDG autostart entry
/// on Linux.
/// </summary>
internal static class Autostart
{
    private const string Name = "Hearthpulse";

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
                var plist = Path.Combine(Home, "Library", "LaunchAgents", "app.hearthpulse.desktop.plist");
                WriteOrDelete(plist, enabled, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0">
                    <dict>
                        <key>Label</key><string>app.hearthpulse.desktop</string>
                        <key>ProgramArguments</key><array><string>{SecurityElement.Escape(Executable)}</string></array>
                        <key>RunAtLoad</key><true/>
                    </dict>
                    </plist>
                    """);
            }
            else if (OperatingSystem.IsLinux())
            {
                var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x : Path.Combine(Home, ".config");
                WriteOrDelete(Path.Combine(config, "autostart", "hearthpulse.desktop"), enabled, $"""
                    [Desktop Entry]
                    Type=Application
                    Name=Hearthpulse
                    Comment=Hearthstone companion: deck tracker and Battlegrounds advisor
                    Exec="{Executable}"
                    Icon=hearthpulse
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

    // An AppImage runs from a mount point that changes on every start; APPIMAGE is the image file itself.
    private static string Executable =>
        UpdateService.IsAppImage ? Environment.GetEnvironmentVariable("APPIMAGE")! : Environment.ProcessPath ?? Name;

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
