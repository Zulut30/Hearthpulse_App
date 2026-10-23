using System.IO;
using Hearthlens.Core;
using Microsoft.Win32;

namespace Hearthlens.App.Infrastructure;

/// <summary>Registers Hearthlens in the current user's Run key so it starts with Windows.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Hearthlens";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            var command = $"\"{Environment.ProcessPath}\"";
            if (enabled)
            {
                // Rewrite only when the path changed (first enable, or the app was moved).
                if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue(ValueName, command);
                }
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Diag.Error("autostart registration failed", ex);
        }
    }
}
