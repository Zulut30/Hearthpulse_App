using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Hearthpulse.Core.Diagnostics;

/// <summary>
/// Sends crash reports the player agreed to (plan 0.5, docs/crash-reports.md): one by the error window's "Send report",
/// or every new kind of error while Settings → Data → "Send crash reports automatically" is on (off by default). A report
/// is first written to <c>crash-reports/</c> in the data folder, so a crash that ends the process goes at the next start;
/// at most <see cref="MaxPending"/> wait, none older than 30 days. Without consent nothing is written or sent.
/// </summary>
public sealed class CrashReporter(HttpClient http, Uri endpoint, string folder, Func<bool> sendAutomatically, string ui, string version)
{
    public const int MaxPending = 5;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private readonly Lock _gate = new();

    public static string DefaultFolder => Path.Combine(AppPaths.Root, "crash-reports");

    /// <summary>
    /// The app's reporter, built right after the settings load (before the history, the session and the windows), so a
    /// crash at start is reported too: its own HTTP client, the site's address and the data folder, nothing else.
    /// </summary>
    public static CrashReporter ForApp(AppSettings settings, string ui, string version) =>
        new(AppHttp, new Uri(Accounts.AccountClient.ResolveBaseUri(), "crash-reports"), DefaultFolder, () => settings.SendCrashReports, ui, version);

    /// <summary>The app's own client for reports: it lives as long as the process and carries no sign-in.</summary>
    private static readonly HttpClient AppHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>Whether reports go without asking (the Settings switch, read each time).</summary>
    public bool Automatic => sendAutomatically();

    /// <summary>
    /// A new kind of error reached a global handler: with automatic reports on it is queued and, unless the process is
    /// ending (<paramref name="fatal"/>), sent now. Returns whether it was queued.
    /// </summary>
    public bool OnError(string where, Exception ex, bool fatal)
    {
        if (!sendAutomatically())
        {
            return false;
        }

        Queue(where, ex);
        if (!fatal)
        {
            SendPendingAsync().Forget("Crash report");
        }

        return true;
    }

    /// <summary>The player pressed "Send report" in the error window: this error is queued and sent now; true when it went.</summary>
    public Task<bool> SendAsync(string where, Exception ex)
    {
        Queue(where, ex);
        return SendPendingAsync();
    }

    /// <summary>Queues the error only (a crash that ends the process): it goes at the next start.</summary>
    public void Queue(string where, Exception ex)
    {
        var report = CrashReport.Build(where, ex, ui, version, UtcNow());
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var pending = Pending();
                foreach (var old in pending.Skip(MaxPending - 1))
                {
                    File.Delete(old.FullName);
                }

                var path = Path.Combine(folder, report.Id + ".json");
                var temp = path + ".tmp";
                File.WriteAllText(temp, report.ToJson(), Encoding.UTF8);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception io) when (io is IOException or UnauthorizedAccessException)
            {
                Diag.Error("Saving a crash report failed", io);
            }
        }
    }

    /// <summary>
    /// Sends what waits, oldest first. A report the site took, or refused as malformed, is deleted; one that could not
    /// reach the site waits for the next start. Reports older than 30 days are dropped unsent. True when none waits.
    /// </summary>
    public async Task<bool> SendPendingAsync(CancellationToken cancellationToken = default)
    {
        FileInfo[] files;
        lock (_gate)
        {
            if (!Directory.Exists(folder))
            {
                return true;
            }

            files = [.. Pending().OrderBy(f => f.LastWriteTimeUtc)];
        }

        foreach (var file in files)
        {
            if (UtcNow() - file.LastWriteTimeUtc > MaxAge)
            {
                Delete(file);
                continue;
            }

            string json;
            try
            {
                json = await File.ReadAllTextAsync(file.FullName, cancellationToken).ConfigureAwait(false);
                using var _ = JsonDocument.Parse(json);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Delete(file);
                continue;
            }

            int status;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SendTimeout);
                using var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await http.PostAsync(endpoint, content, timeout.Token).ConfigureAwait(false);
                status = (int)response.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Diag.Info($"crash report: not sent now ({ex.GetType().Name}), it waits for the next start");
                return false;
            }

            // Taken, or refused as not a report: either way it never goes again. Throttled or down: it waits.
            if (status is >= 200 and < 300 or 400 or 413 or 422)
            {
                Delete(file);
            }
            else
            {
                Diag.Info($"crash report: the site answered {status}, it waits for the next start");
                return false;
            }
        }

        return true;
    }

    /// <summary>The automatic reports were switched off: what waits is deleted unsent (consent withdrawn).</summary>
    public void ClearPending()
    {
        lock (_gate)
        {
            foreach (var file in Pending())
            {
                Delete(file);
            }
        }
    }

    private List<FileInfo> Pending() => Directory.Exists(folder)
        ? [.. new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc)]
        : [];

    private void Delete(FileInfo file)
    {
        lock (_gate)
        {
            try
            {
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Info($"crash report: could not delete {file.Name} ({ex.Message})");
            }
        }
    }
}
