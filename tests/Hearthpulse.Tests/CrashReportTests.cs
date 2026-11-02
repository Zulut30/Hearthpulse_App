using System.Net;
using System.Text.Json;
using Hearthpulse.Core.Diagnostics;

namespace Hearthpulse.Tests;

/// <summary>Crash reports (plan 0.5): no personal data in them, nothing without consent, the queue and the sending.</summary>
public sealed class CrashReportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-crash-{Guid.NewGuid():N}");
    private readonly FakeSite _site = new();
    private bool _automatic;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private CrashReporter Reporter() =>
        new(new HttpClient(_site), new Uri("https://site.test/api/v1/crash-reports"), _dir, () => _automatic, "wpf", "0.15.0");

    private static Exception Thrown(Exception ex)
    {
        try
        {
            throw ex;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    [Fact]
    public void A_report_carries_no_user_path_battletag_email_address_or_long_number()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var inner = Thrown(new IOException($"Could not find file '{Path.Combine(profile, "AppData", "Roaming", "Hearthpulse", "history.db")}'"));
        var ex = Thrown(new InvalidOperationException(
            "Player Zulut#2187 (zulut@example.com) from 192.168.1.20, account 123456789012 at D:\\Games\\Hearthstone\\Logs", inner));

        var report = CrashReport.Build("UI error", ex, "wpf", "0.15.0", DateTime.UtcNow);
        var json = report.ToJson();

        Assert.DoesNotContain("Zulut#2187", json, StringComparison.Ordinal);
        Assert.DoesNotContain("zulut@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.20", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123456789012", json, StringComparison.Ordinal);
        Assert.DoesNotContain("D:\\\\Games", json, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.Replace("\\", "\\\\", StringComparison.Ordinal), json, StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= 3)
        {
            Assert.DoesNotContain(Environment.UserName, json, StringComparison.OrdinalIgnoreCase);
        }

        // What stays: the types, the scrubbed messages and the methods of the stack, without file paths.
        Assert.Equal(["System.InvalidOperationException", "System.IO.IOException"], report.Errors.Select(e => e.Type));
        Assert.Contains("<name>", report.Errors[0].Message, StringComparison.Ordinal);
        Assert.Equal("Could not find file '<value>'", report.Errors[1].Message);
        Assert.Contains(report.Errors[0].Stack, line => line.Contains(nameof(Thrown), StringComparison.Ordinal));
        Assert.DoesNotContain(report.Errors[0].Stack, line => line.Contains(":line", StringComparison.Ordinal));
        Assert.Equal(("hearthpulse", "wpf", "0.15.0", "UI error"), (report.App, report.Ui, report.Version, report.Where));
    }

    [Theory]
    [InlineData("The given key 'Иван Петров' was not present in the dictionary.", "The given key '<value>' was not present in the dictionary.")]
    [InlineData("The input string \"Ivan\" was not in a correct format.", "The input string \"<value>\" was not in a correct format.")]
    [InlineData("Could not open D:\\Иван Петров\\Hearthstone\\Logs\\Power.log", "Could not open <path>")]
    [InlineData("Bad file %USERPROFILE%\\OneDrive - Компания\\replays\\game.hpr", "Bad file <path>")]
    [InlineData("Missing /home/ivan petrov/.config/x", "Missing <path>")]
    [InlineData("Entity=Ivan tag=ZONE value=PLAY", "Entity=<value> tag=<value> value=<value>")]
    public void Names_values_and_whole_paths_are_cut_out(string message, string expected) =>
        Assert.Equal(expected, CrashReport.Scrub(message, 300));

    [Fact]
    public void A_report_is_cut_to_thirty_kilobytes_innermost_frames_first()
    {
        Exception chain = new DeepStack("innermost", null);
        for (var i = 0; i < 4; i++)
        {
            chain = new DeepStack($"level {i}", chain);
        }

        var report = CrashReport.Build("UI error", chain, "wpf", "0.15.0", DateTime.UtcNow);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(report.ToJson()) <= 30 * 1024);
        Assert.Equal(40, report.Errors[0].Stack.Count);
        Assert.True(report.Errors[^1].Stack.Count < 40);
    }

    [Fact]
    public void The_app_s_reporter_needs_only_the_settings()
    {
        var settings = new Hearthpulse.Core.AppSettings { SendCrashReports = true };
        var reporter = CrashReporter.ForApp(settings, "wpf", "0.15.0");

        Assert.True(reporter.Automatic);
        settings.SendCrashReports = false;
        Assert.False(reporter.Automatic);
    }

    [Fact]
    public async Task Without_consent_nothing_is_written_or_sent()
    {
        var reporter = Reporter();

        Assert.False(reporter.OnError("UI error", Thrown(new InvalidOperationException("x")), fatal: false));
        await reporter.SendPendingAsync();

        Assert.False(Directory.Exists(_dir) && Directory.EnumerateFiles(_dir).Any());
        Assert.Empty(_site.Bodies);
    }

    [Fact]
    public async Task The_send_button_sends_this_report_now()
    {
        var reporter = Reporter();

        Assert.True(await reporter.SendAsync("UI error", Thrown(new InvalidOperationException("boom"))));

        var body = Assert.Single(_site.Bodies);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("System.InvalidOperationException", doc.RootElement.GetProperty("errors")[0].GetProperty("type").GetString());
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public async Task With_the_switch_on_a_fatal_crash_waits_for_the_next_start()
    {
        _automatic = true;
        var reporter = Reporter();

        Assert.True(reporter.OnError("Fatal error", Thrown(new InvalidOperationException("dying")), fatal: true));
        Assert.Single(Directory.EnumerateFiles(_dir, "*.json"));
        Assert.Empty(_site.Bodies);

        // The next start sends it; offline it waits again.
        _site.Offline = true;
        Assert.False(await Reporter().SendPendingAsync());
        Assert.Single(Directory.EnumerateFiles(_dir, "*.json"));
        _site.Offline = false;
        Assert.True(await Reporter().SendPendingAsync());
        Assert.Single(_site.Bodies);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.json"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task A_refused_report_is_dropped_and_a_throttled_one_waits(HttpStatusCode status, bool waits)
    {
        var reporter = Reporter();
        reporter.Queue("UI error", Thrown(new InvalidOperationException("x")));
        _site.Status = status;

        await reporter.SendPendingAsync();

        Assert.Equal(waits, Directory.EnumerateFiles(_dir, "*.json").Any());
    }

    [Fact]
    public async Task A_report_older_than_thirty_days_is_dropped_unsent()
    {
        var reporter = Reporter();
        reporter.Queue("UI error", Thrown(new InvalidOperationException("x")));
        foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-31));
        }

        Assert.True(await reporter.SendPendingAsync());

        Assert.Empty(_site.Bodies);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.json"));
    }

    [Fact]
    public void Switching_automatic_reports_off_deletes_what_waits()
    {
        _automatic = true;
        var reporter = Reporter();
        reporter.OnError("Fatal error", Thrown(new InvalidOperationException("x")), fatal: true);
        Assert.NotEmpty(Directory.EnumerateFiles(_dir, "*.json"));

        reporter.ClearPending();

        Assert.Empty(Directory.EnumerateFiles(_dir, "*.json"));
    }

    [Fact]
    public void At_most_five_reports_wait()
    {
        var reporter = Reporter();
        for (var i = 0; i < 8; i++)
        {
            reporter.Queue("UI error", Thrown(new InvalidOperationException($"error {i}")));
        }

        Assert.Equal(CrashReporter.MaxPending, Directory.EnumerateFiles(_dir, "*.json").Count());
    }

    /// <summary>An exception with a long stack of its own (40 frames of 300 characters).</summary>
    private sealed class DeepStack(string message, Exception? inner) : Exception(message, inner)
    {
        public override string StackTrace => string.Join('\n', Enumerable.Range(0, 40).Select(i => $"   at Frame{i}.Method({new string('x', 280)})"));
    }

    private sealed class FakeSite : HttpMessageHandler
    {
        public bool Offline { get; set; }

        public HttpStatusCode Status { get; set; } = HttpStatusCode.Created;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            // A report is never tied to an account: no sign-in goes with it.
            Assert.Null(request.Headers.Authorization);
            if (Status == HttpStatusCode.Created)
            {
                Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            }

            return new HttpResponseMessage(Status);
        }
    }
}
