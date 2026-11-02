using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
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

    /// <summary>A report stays under the site's 32 KB: past this its inner exceptions lose stack frames, then whole.</summary>
    internal const int MaxJsonBytes = 30 * 1024;
    private static readonly DateTime Started = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>
    /// The report of <paramref name="ex"/> caught by the handler <paramref name="where"/>: the app, OS and runtime, and up
    /// to five exceptions of its chain (inner ones, or an aggregate's), each message and stack scrubbed, cut to
    /// <see cref="MaxJsonBytes"/> (frames of the innermost exceptions go first).
    /// </summary>
    public static CrashReport Build(string where, Exception ex, string ui, string version, DateTime utcNow)
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

        var report = new CrashReport(
            1,
            Guid.NewGuid().ToString("D"),
            "hearthpulse",
            Clip(version, 32),
            ui,
            Clip($"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}", 120),
            Clip(RuntimeInformation.FrameworkDescription, 60),
            Clip(CultureInfo.CurrentUICulture.Name, 16),
            Clip(where, 60),
            (int)Math.Clamp((utcNow - Started).TotalSeconds, 0, int.MaxValue),
            utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            errors);
        return Fit(report);
    }

    /// <summary>Drops stack frames from the innermost exception outwards, then inner exceptions, until the JSON fits.</summary>
    private static CrashReport Fit(CrashReport report)
    {
        var errors = report.Errors.ToList();
        while (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(report with { Errors = errors }, Json)) > MaxJsonBytes)
        {
            var last = errors.Count - 1;
            if (errors[last].Stack.Count > 0)
            {
                errors[last] = errors[last] with { Stack = [.. errors[last].Stack.Take(errors[last].Stack.Count / 2)] };
            }
            else if (last > 0)
            {
                errors.RemoveAt(last);
            }
            else
            {
                break;
            }
        }

        return report with { Errors = errors };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>
    /// Personal data out of a message or a stack line, then cut to length: e-mails, BattleTags, any quoted value (the
    /// runtime puts the failing key or input there: <c>The given key 'Name' was not present</c>), <c>key=value</c>
    /// pairs, the user profile and user name, every absolute path whole (spaces included, to the next quote or the end),
    /// IP addresses and numbers of 7 digits and more.
    /// </summary>
    public static string Scrub(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // Names first (a user name may also be part of a BattleTag or an e-mail), then paths whole (an apostrophe inside
        // one, "Bob's Company", does not end it), then the OS user name as a word, then quoted values and key=value.
        var scrubbed = Email().Replace(text.ReplaceLineEndings(" "), "<email>");
        scrubbed = BattleTag().Replace(scrubbed, "<name>");
        scrubbed = AbsolutePath().Replace(scrubbed, "<path>");
        if (Environment.UserName is { Length: >= 3 } user)
        {
            scrubbed = Regex.Replace(scrubbed, $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(user)}(?![\p{{L}}\p{{N}}_])", "<user>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        scrubbed = Quoted().Replace(scrubbed, "$1<value>$2");
        scrubbed = KeyValue().Replace(scrubbed, "$1=<value>");
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

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    // An absolute path, whole (folder names may hold spaces): a drive, UNC or %USERPROFILE% path on Windows, or a /home,
    // /Users, /tmp... path elsewhere, up to a quote or the end.
    // An apostrophe ends a path or a quoted value only before a space, a punctuation mark or the end: "O'Brien",
    // "Bob's Company" stay inside.
    [GeneratedRegex(@"(?:file:///|[A-Za-z]:[\\/]|\\\\|%USERPROFILE%(?=[\\/]|$)|/(?:home|Users|tmp|var|private|mnt|media|root|Volumes|opt|srv)/)(?:[^""'<>|«»“”„]|'(?![\s.,;:!?)\]]|$))*", RegexOptions.CultureInvariant)]
    private static partial Regex AbsolutePath();

    // A quoted value in any quotes ('…', "…", «…», “…”, „…“), however long; the delimiters stay.
    [GeneratedRegex(@"('|""|«|“|„)(?:(?<=')(?:[^']|'(?![\s.,;:!?)\]]|$))+(?='(?:[\s.,;:!?)\]]|$))|(?<="")[^""]+(?="")|(?<=«)[^»]+(?=»)|(?<=[“„])[^”“]+(?=[”“]))('|""|»|”|“)", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    // key=value, as in log-like messages (Entity=Name, tag=...).
    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]{0,40})=[^\s,;)\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

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
