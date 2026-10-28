using System.Net;
using Hearthpulse.Core;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// The update check must never throw at the player: GitHub answers with nulls, assets it cannot name, drafts and plain
/// failures (no network, 403 without a user agent). Failures are logged and reported as such, not as "no update", and a
/// file without a published SHA-256 is never downloaded. The version compare and the install helper are pure, so they
/// are checked here directly.
/// </summary>
public sealed class UpdateTests
{
    private const string AllTargetsReply = """
        {
          "tag_name": "v99.0.0",
          "html_url": "https://github.com/Zulut30/IceCrowV2/releases/tag/v99.0.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "Hearthpulse-Setup-99.0.0-win-x64.exe", "browser_download_url": "https://example.invalid/setup.exe" },
            { "name": "Hearthpulse-Preview-99.0.0-win-x64.zip", "browser_download_url": "https://example.invalid/preview.zip" },
            { "name": "Hearthpulse-99.0.0-macOS-arm64.pkg", "browser_download_url": "https://example.invalid/mac-arm64.pkg" },
            { "name": "Hearthpulse-99.0.0-macOS-x64.pkg", "browser_download_url": "https://example.invalid/mac-x64.pkg" },
            { "name": "hearthpulse_99.0.0_amd64.deb", "browser_download_url": "https://example.invalid/deb-amd64" },
            { "name": "hearthpulse_99.0.0_arm64.deb", "browser_download_url": "https://example.invalid/deb-arm64" },
            { "name": "hearthpulse-99.0.0-1.x86_64.rpm", "browser_download_url": "https://example.invalid/rpm-x86_64" },
            { "name": "hearthpulse-99.0.0-1.aarch64.rpm", "browser_download_url": "https://example.invalid/rpm-aarch64" },
            { "name": "Hearthpulse-99.0.0-linux-x64.tar.gz", "browser_download_url": "https://example.invalid/tar-x64" },
            { "name": "Hearthpulse-99.0.0-linux-arm64.tar.gz", "browser_download_url": "https://example.invalid/tar-arm64" }
          ]
        }
        """;

    [Theory]
    [InlineData("0.4.0", "0.4.0", 0)]
    [InlineData("0.4.0", "0.5.0", -1)]
    [InlineData("0.5.0", "0.4.0", 1)]
    [InlineData("v0.5.0", "0.5.0", 0)]
    [InlineData("0.5.0-preview", "0.5.0", 0)]
    [InlineData("0.10.0", "0.9.0", 1)]
    [InlineData("0.4", "0.4.0", 0)]
    [InlineData("", "0.4.0", -1)]
    [InlineData("garbage", "0.4.0", -1)]
    public void Versions_compare_by_dotted_numbers(string a, string b, int expected) =>
        Assert.Equal(expected, UpdateService.CompareVersions(a, b));

    [Fact]
    public void A_version_the_player_does_not_have_is_newer()
    {
        Assert.True(UpdateService.IsNewer("99.0.0"));
        Assert.False(UpdateService.IsNewer(UpdateService.CurrentVersion));
    }

    [Theory]
    [InlineData(UpdateTarget.WindowsInstalled, "x64", "", "Hearthpulse-Setup-0.5.0-win-x64.exe")]
    [InlineData(UpdateTarget.WindowsPortable, "x64", "", "Hearthpulse-Preview-0.5.0-win-x64.zip")]
    [InlineData(UpdateTarget.MacPackage, "arm64", "", "Hearthpulse-0.5.0-macOS-arm64.pkg")]
    [InlineData(UpdateTarget.MacPackage, "x64", "", "Hearthpulse-0.5.0-macOS-x64.pkg")]
    [InlineData(UpdateTarget.LinuxPackage, "x64", "deb", "hearthpulse_0.5.0_amd64.deb")]
    [InlineData(UpdateTarget.LinuxPackage, "arm64", "deb", "hearthpulse_0.5.0_arm64.deb")]
    [InlineData(UpdateTarget.LinuxPackage, "x64", "rpm", "hearthpulse-0.5.0-1.x86_64.rpm")]
    [InlineData(UpdateTarget.LinuxPackage, "arm64", "rpm", "hearthpulse-0.5.0-1.aarch64.rpm")]
    [InlineData(UpdateTarget.LinuxPackage, "x64", "", "Hearthpulse-0.5.0-linux-x64.tar.gz")]
    public void Asset_names_match_the_released_files(UpdateTarget target, string architecture, string flavor, string expected) =>
        Assert.Equal(expected, UpdateService.AssetName("0.5.0", target, architecture, flavor));

    // The Linux package follows how this copy was installed, not the distribution: a tar.gz unpacked on Ubuntu must not
    // be offered a .deb (opening it installed a second copy).
    [Theory]
    [InlineData("/opt/hearthpulse/app/", "/var/lib/dpkg/info/hearthpulse.list", "deb")]
    [InlineData("/opt/hearthpulse/app/", "/usr/share/applications/hearthpulse.desktop", "rpm")]
    [InlineData("/opt/hearthpulse/app/", "/usr/local/share/applications/hearthpulse.desktop", "")]
    [InlineData("/home/player/.local/share/hearthpulse/app/", "/var/lib/dpkg/info/hearthpulse.list", "")]
    [InlineData("/home/player/Downloads/Hearthpulse-0.13.0-linux-x64/app/", "/usr/share/applications/hearthpulse.desktop", "")]
    public void The_linux_package_follows_how_this_copy_was_installed(string appDirectory, string existingFile, string expected) =>
        Assert.Equal(expected, UpdateService.LinuxFlavor(appDirectory, path => path == existingFile));

    [Fact]
    public void The_reply_for_this_platform_gives_a_download_url()
    {
        var target = UpdateService.DetectTarget();
        var info = UpdateService.ParseLatest(AllTargetsReply, target, UpdateService.RuntimeArchitecture(), UpdateService.LinuxFlavor());
        Assert.NotNull(info);
        Assert.Equal("99.0.0", info.Version);
        Assert.Equal(UpdateService.AssetName("99.0.0", target, UpdateService.RuntimeArchitecture(), UpdateService.LinuxFlavor()), info.AssetName);
        Assert.StartsWith("https://example.invalid/", info.DownloadUrl);
    }

    [Theory]
    [InlineData("""{ "tag_name": "v0.5.0", "assets": null }""")]
    [InlineData("""{ "tag_name": "v0.5.0" }""")]
    [InlineData("""{ "tag_name": null, "assets": [] }""")]
    [InlineData("""{ "tag_name": "v0.5.0", "assets": [ { "name": null, "browser_download_url": null } ] }""")]
    [InlineData("""{ "tag_name": "v0.5.0", "assets": [ { "name": "Hearthpulse-Setup-0.5.0-win-x64.exe" } ] }""")]
    [InlineData("""{ "tag_name": "v0.5.0", "assets": [ { "name": "other-file.exe", "browser_download_url": "https://example.invalid/x" } ] }""")]
    [InlineData("""{ "tag_name": "v0.5.0", "draft": true, "assets": [] }""")]
    [InlineData("""{ "tag_name": "v0.5.0", "prerelease": true, "assets": [] }""")]
    [InlineData("null")]
    [InlineData("not json at all")]
    public void Odd_replies_mean_no_update(string reply) =>
        Assert.Null(UpdateService.ParseLatest(reply, UpdateTarget.WindowsInstalled, "x64"));

    [Fact]
    public async Task A_newer_release_is_offered()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.OK, AllTargetsReply));
        var info = await UpdateService.CheckAsync(http);
        Assert.NotNull(info);
        Assert.Equal("99.0.0", info.Version);
    }

    [Fact]
    public async Task An_older_release_is_not_offered()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.OK, """{ "tag_name": "v0.0.1", "assets": [] }"""));
        Assert.Null(await UpdateService.CheckAsync(http));
    }

    [Fact]
    public async Task A_forbidden_or_missing_release_is_not_an_error()
    {
        using var forbidden = new HttpClient(new Stub(HttpStatusCode.Forbidden, string.Empty));
        using var missing = new HttpClient(new Stub(HttpStatusCode.NotFound, string.Empty));
        Assert.Null(await UpdateService.CheckAsync(forbidden));
        Assert.Null(await UpdateService.CheckAsync(missing));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "404 (no published release)")]
    [InlineData(HttpStatusCode.Forbidden, "403 (forbidden)")]
    [InlineData(HttpStatusCode.TooManyRequests, "429 (rate limited)")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task A_failed_answer_is_logged_and_reported_not_taken_for_no_update(HttpStatusCode status, string expected)
    {
        using var http = new HttpClient(new Stub(status, string.Empty));
        var check = await UpdateService.CheckForUpdateAsync(http);
        Assert.Equal(UpdateCheckOutcome.Failed, check.Outcome);
        Assert.Null(check.Update);
        Assert.Contains(expected, check.Message);
        Assert.Contains($"Update check: GitHub answered {expected}", TestDataFolder.ReadLog());
    }

    [Fact]
    public async Task A_403_with_no_requests_left_is_the_rate_limit()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.Forbidden, string.Empty, ("X-RateLimit-Remaining", "0")));
        Assert.Contains("403 (rate limited)", (await UpdateService.CheckForUpdateAsync(http)).Message);
    }

    [Fact]
    public async Task A_newer_release_without_a_file_for_this_platform_is_not_called_up_to_date()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.OK, """{ "tag_name": "v99.0.0", "assets": [] }"""));
        var check = await UpdateService.CheckForUpdateAsync(http);
        Assert.Null(check.Update);
        Assert.Equal("Version 99.0.0 is out, but there is no file for this platform yet.", check.Message);
    }

    [Fact]
    public async Task A_file_changed_after_its_download_is_deleted_instead_of_installed()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("the published setup");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var info = new UpdateInfo("99.0.2", "hp-test-recheck.bin", "https://example.invalid/r.bin", "https://example.invalid/page", sha);
        using (var http = new HttpClient(new Counting(payload)))
        {
            Assert.NotNull(await UpdateService.DownloadAsync(http, info));
        }

        Assert.True(UpdateService.StagedFileMatches(info));
        File.WriteAllText(UpdateService.StagedPath(info), "changed on disk");
        Assert.False(UpdateService.StagedFileMatches(info));
        Assert.False(File.Exists(UpdateService.StagedPath(info)));
    }

    [Fact]
    public async Task An_unverified_copy_left_by_an_older_build_is_removed()
    {
        var info = new UpdateInfo("99.0.3", "hp-test-unverified.bin", "https://example.invalid/u.bin", "https://example.invalid/page");
        Directory.CreateDirectory(Path.GetDirectoryName(UpdateService.StagedPath(info))!);
        File.WriteAllText(UpdateService.StagedPath(info), "downloaded without a check");
        using var http = new HttpClient(new Counting([1]));
        Assert.Null(await UpdateService.DownloadAsync(http, info));
        Assert.False(File.Exists(UpdateService.StagedPath(info)));
    }

    [Fact]
    public async Task The_newest_release_is_reported_up_to_date()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.OK, """{ "tag_name": "v0.0.1", "assets": [] }"""));
        var check = await UpdateService.CheckForUpdateAsync(http);
        Assert.Equal(UpdateCheckOutcome.UpToDate, check.Outcome);
        Assert.Contains("up to date", check.Message);
    }

    [Fact]
    public async Task A_release_without_a_checksum_is_offered_but_never_downloaded()
    {
        using var http = new HttpClient(new Stub(HttpStatusCode.OK, AllTargetsReply));
        var check = await UpdateService.CheckForUpdateAsync(http);
        Assert.Equal(UpdateCheckOutcome.Available, check.Outcome);
        Assert.Null(check.Update!.Sha256);
        Assert.Contains("no SHA-256 checksum", check.Message);

        var download = new Counting(System.Text.Encoding.UTF8.GetBytes("setup"));
        using var files = new HttpClient(download);
        Assert.Null(await UpdateService.DownloadAsync(files, check.Update));
        Assert.Equal(0, download.Requests);
        Assert.False(File.Exists(UpdateService.StagedPath(check.Update)));
    }

    [Fact]
    public async Task A_download_is_kept_only_when_it_matches_the_published_checksum()
    {
        var payload = System.Text.Encoding.UTF8.GetBytes("the published setup");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var good = new UpdateInfo("99.0.1", "hp-test-good.bin", "https://example.invalid/good.bin", "https://example.invalid/page", sha);
        using (var http = new HttpClient(new Counting(payload)))
        {
            var staged = await UpdateService.DownloadAsync(http, good);
            Assert.Equal(UpdateService.StagedPath(good), staged);
            Assert.Equal(payload, File.ReadAllBytes(staged!));
        }

        var tampered = good with { AssetName = "hp-test-tampered.bin" };
        using (var http = new HttpClient(new Counting(System.Text.Encoding.UTF8.GetBytes("something else"))))
        {
            Assert.Null(await UpdateService.DownloadAsync(http, tampered));
            Assert.False(File.Exists(UpdateService.StagedPath(tampered)));
            Assert.False(File.Exists(UpdateService.StagedPath(tampered) + ".part"));
        }
    }

    [Fact]
    public async Task No_network_is_not_an_error()
    {
        using var http = new HttpClient(new Offline());
        Assert.Null(await UpdateService.CheckAsync(http));
        var check = await UpdateService.CheckForUpdateAsync(http);
        Assert.Equal(UpdateCheckOutcome.Failed, check.Outcome);
        Assert.Contains("The network is down.", check.Message);
    }

    [Fact]
    public void The_staged_file_waits_in_the_data_folder()
    {
        var info = new UpdateInfo("99.0.0", "Hearthpulse-Setup-99.0.0-win-x64.exe", "https://example.invalid/setup.exe", "https://example.invalid/page");
        var staged = UpdateService.StagedPath(info);
        Assert.StartsWith(AppPaths.Root, staged);
        Assert.EndsWith(info.AssetName, staged);
    }

    [Fact]
    public void The_install_helper_waits_then_runs_the_setup_silently()
    {
        var script = UpdateService.BuildApplyScript(1234, @"C:\tmp\setup.exe", @"C:\tmp\setup.exe", @"C:\app", @"C:\app\Hearthpulse.exe", installer: true, cleanupDir: null);
        Assert.Contains(":wait", script);
        Assert.Contains(@"PID eq 1234", script);
        Assert.Contains("start \"\" /wait \"C:\\tmp\\setup.exe\" /S", script);
        Assert.Contains("start \"\" \"C:\\app\\Hearthpulse.exe\"", script);
        Assert.DoesNotContain("xcopy", script);
        Assert.Contains("del \"%~f0\"", script);
    }

    [Fact]
    public void The_zip_helper_unpacks_over_the_app_folder_and_clears_the_temp_copy()
    {
        var script = UpdateService.BuildApplyScript(42, @"C:\tmp\preview.zip", @"C:\tmp\hearthpulse-update", @"C:\app", null, installer: false, cleanupDir: @"C:\tmp\hearthpulse-update");
        Assert.Contains("xcopy /E /Y /I \"C:\\tmp\\hearthpulse-update\\*\" \"C:\\app\\\"", script);
        Assert.Contains("rmdir /S /Q \"C:\\tmp\\hearthpulse-update\"", script);
        Assert.DoesNotContain("/wait", script);
        Assert.DoesNotContain("start \"\" \"C:", script);
    }

    [Fact]
    public void Settings_ship_with_automatic_updates_on()
    {
        Assert.True(new AppSettings().AutoUpdate);

        var off = new AppSettings { AutoUpdate = false };
        Assert.False(AppSettings.FromJson(off.ToJson()).AutoUpdate);
    }

    private sealed class Stub(HttpStatusCode status, string body, params (string Name, string Value)[] headers) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return Task.FromResult(response);
        }
    }

    /// <summary>Answers every request with the same file and counts the requests.</summary>
    private sealed class Counting(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The network is down.");
    }
}
