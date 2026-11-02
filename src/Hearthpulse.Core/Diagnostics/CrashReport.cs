using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hearthpulse.Core.Diagnostics;

/// <summary>One exception of a report's chain: its type, its message and stack with personal data cut out.</summary>
public sealed record CrashError(string Type, string Message, IReadOnlyList<string> Stack);

/// <summary>
/// A crash report the player chose to send (plan 0.5, docs/crash-reports.md): what the app is and where it ran, and the
/// error chain, never who the player is. Built by <see cref="CrashReport.Build"/>, which scrubs every message and stack.
/// </summary>
public sealed partial record CrashReport(
    int V,
    string Id,
    string App,
    string Version,
    string Ui,
    string Os,
    string Runtime,
    string Language,
    string Where,
    int Count,
    int UptimeSeconds,
    string At,
    IReadOnlyList<CrashError> Errors)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private const int MaxErrors = 5;
    private const int MaxFrames = 40;
    private const int MaxMessage = 300;
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>
    /// The report of <paramref name="ex"/> caught by the handler <paramref name="where"/>: the app, OS and runtime, and up
    /// to five exceptions of its chain (inner ones, or an aggregate's), each message and stack scrubbed.
    /// </summary>
    public static CrashReport Build(string where, Exception ex, string ui, string version, int count, DateTime utcNow)
    {
        var errors = new List<CrashError>();
        var pending = new Queue<Exception>([ex]);
        while (pending.Count > 0 && errors.Count < MaxErrors)
        {
            var e = pending.Dequeue();
            errors.Add(new CrashError(e.GetType().FullName ?? e.GetType().Name, Scrub(e.Message, MaxMessage), Frames(e.StackTrace)));
            foreach (var inner in e is AggregateException aggregate ? aggregate.InnerExceptions : e.InnerException is { } one ? [one] : [])
            {
                pending.Enqueue(inner);
            }
        }

        return new CrashReport(
            1,
            Guid.NewGuid().ToString("D"),
            "hearthpulse",
            Clip(version, 32),
            ui,
            Clip($"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}", 120),
            Clip(RuntimeInformation.FrameworkDescription, 60),
            Clip(CultureInfo.CurrentUICulture.Name, 16),
            Clip(where, 60),
            Math.Clamp(count, 1, 1_000_000),
            (int)Math.Clamp((utcNow - Started).TotalSeconds, 0, int.MaxValue),
            utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            errors);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Personal data out of a message or a stack line: the user profile and home paths, any other absolute path, what
    /// looks like a BattleTag, an e-mail, an IP address or a long number (account, card or phone), then cut to length.
    /// </summary>
    public static string Scrub(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Names first (a user name may also be part of a BattleTag or an e-mail), then the home folder, then other paths.
        var scrubbed = Email().Replace(text.ReplaceLineEndings(" "), "<email>");
        scrubbed = BattleTag().Replace(scrubbed, "<name>");
        foreach (var home in Homes())
        {
            scrubbed = scrubbed.Replace(home, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        scrubbed = AbsolutePath().Replace(scrubbed, "<path>");
        scrubbed = Address().Replace(scrubbed, "<ip>");
        scrubbed = LongNumber().Replace(scrubbed, "<number>");
        return Clip(scrubbed, max);
    }

    /// <summary>"at Namespace.Type.Method(Args)" lines without their " in file:line" part (paths stay on this machine).</summary>
    private static List<string> Frames(string? stack) => (stack ?? string.Empty)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => FileSuffix().Replace(line, string.Empty))
        .Select(line => Scrub(line, 300))
        .Where(line => line.Length > 0)
        .Take(MaxFrames)
        .ToList();

    private static IEnumerable<string> Homes() =>
        new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName }
            .Where(h => h.Length >= 3)
            .OrderByDescending(h => h.Length);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // An absolute path: a drive or UNC path on Windows, or a /home, /Users, /tmp... path elsewhere.
    [GeneratedRegex(@"([A-Za-z]:\\|\\\\)[^\s""'<>|]*|/(?:home|Users|tmp|var|private|mnt|media|root)/[^\s""'<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePath();

    [GeneratedRegex(@"[\p{L}\p{N}_]{2,24}#\d{3,6}", RegexOptions.CultureInvariant)]
    private static partial Regex BattleTag();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b\d{1,3}(?:\.\d{1,3}){3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Address();

    [GeneratedRegex(@"\d{7,}", RegexOptions.CultureInvariant)]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"\s+in\s+.+:line\s+\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex FileSuffix();
}
