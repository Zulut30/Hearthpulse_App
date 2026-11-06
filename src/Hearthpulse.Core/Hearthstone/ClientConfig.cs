using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Hearthpulse.Core.Hearthstone;

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

            if (inSection && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq && line[..eq].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase))
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

    /// <summary>When the running client started, or null. Tests put a time of their own here.</summary>
    internal static Func<DateTime?> ClientStartTime { get; set; } = GameProcess.StartTime;

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

            if (section >= 0 && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq && line[..eq].Trim().Equals(Key, StringComparison.OrdinalIgnoreCase))
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
