using System.Net;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// The update check must never throw at the player: GitHub answers with nulls, assets it cannot name, drafts and plain
/// failures (no network, 403 without a user agent). Failures are logged and reported as such, not as "no update", and a
/// file without a published SHA-256 is never downloaded. The version compare and the install helper are pure, so they
/// are checked here directly.
/// </summary>
[Collection(nameof(UpdateStagingCollection))]
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

    // The Windows zip is the cross-platform preview. The Windows app outside the folder its setup registered (moved, or
    // copied elsewhere) is not a zip copy: unpacking the preview over it replaced the app with another one.
    [Theory]
    [InlineData(UpdateTarget.WindowsPortable, true, true)]
    [InlineData(UpdateTarget.WindowsPortable, false, false)]
    [InlineData(UpdateTarget.WindowsInstalled, false, true)]
    [InlineData(UpdateTarget.LinuxPackage, false, true)]
    public void Only_the_preview_s_own_folder_unpacks_the_preview_zip(UpdateTarget target, bool previewFolder, bool expected)
    {
        var folder = Path.Combine(Path.GetTempPath(), "hp-app");
        Assert.Equal(expected, UpdateService.InstallsItself(target, folder, path => previewFolder && path == Path.Combine(folder, "Avalonia.Base.dll")));
    }

    [Fact]
    public async Task The_update_loop_only_tells_the_windows_app_outside_its_setup_folder_about_a_release()
    {
        // The loop itself, not only the check: a zip copy that is not the preview gets "available", never a download.
        using var http = new HttpClient(new Stub(HttpStatusCode.OK, AllTargetsReply));
        var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var folder = Path.Combine(Path.GetTempPath(), "hp-moved-app");

        _ = UpdateService.WatchAsync(http, notifyOnly: false,
            available: _ => outcome.TrySetResult("available"),
            staged: _ => outcome.TrySetResult("staged"),
            failed: _ => outcome.TrySetResult("failed"),
            TimeSpan.Zero, UpdateTarget.WindowsPortable, folder, _ => false);

        Assert.Equal("available", await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

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
        var info = new UpdateInfo("99.0.2", "hp-test-recheck.bin", "https://example.invalid/r.bin", sha);
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
        var info = new UpdateInfo("99.0.3", "hp-test-unverified.bin", "https://example.invalid/u.bin");
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
        var good = new UpdateInfo("99.0.1", "hp-test-good.bin", "https://example.invalid/good.bin", sha);
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
        var info = new UpdateInfo("99.0.0", "Hearthpulse-Setup-99.0.0-win-x64.exe", "https://example.invalid/setup.exe");
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
        Assert.Contains("start \"\" /b /wait \"C:\\tmp\\setup.exe\" /S", script);
        Assert.Contains("start \"\" \"C:\\app\\Hearthpulse.exe\"", script);
        Assert.DoesNotContain("xcopy", script);
        Assert.Contains("del \"%~f0\"", script);
    }

    [Fact]
    public void The_zip_helper_unpacks_over_the_app_folder_and_clears_the_temp_copy()
    {
        var script = UpdateService.BuildApplyScript(42, @"C:\tmp\preview.zip", @"C:\tmp\hearthpulse-update", @"C:\app", null, installer: false, cleanupDir: @"C:\tmp\hearthpulse-update");
        Assert.Contains("xcopy.exe\" /E /Y /I \"C:\\tmp\\hearthpulse-update\\*\" \"C:\\app\\\"", script);
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

    // ---- ETag and GitHub's limit (AC1, AC2) ----

    [Fact]
    public async Task A_saved_answer_is_asked_about_by_its_etag_and_a_304_reads_it_back()
    {
        var context = Context();
        var body = UpdateServer.Release("99.0.0", [(Setup("99.0.0"), "setup 99.0.0"u8.ToArray())], "## What's new\n- **Faster** start", Page("99.0.0"));
        var cache = WriteCache(context, "W/\"abc\"", body);
        var before = File.ReadAllBytes(cache);
        var written = File.GetLastWriteTimeUtc(cache);

        var server = new UpdateServer().Latest(() => UpdateServer.Status(HttpStatusCode.NotModified));
        using var http = new HttpClient(server);
        var check = await UpdateService.CheckForUpdateAsync(http, UpdateTarget.WindowsInstalled, context, CancellationToken.None);

        Assert.Equal("W/\"abc\"", Assert.Single(server.Requests).IfNoneMatch);
        using var fresh = new HttpClient(new UpdateServer().Latest(body));
        var direct = await UpdateService.CheckForUpdateAsync(fresh, UpdateTarget.WindowsInstalled, Context(), CancellationToken.None);
        Assert.Equal(UpdateCheckOutcome.Available, check.Outcome);
        Assert.Equal(direct, check);
        Assert.Equal("What's new\n• Faster start", check.Update!.Notes);
        Assert.Equal(before, File.ReadAllBytes(cache));
        Assert.Equal(written, File.GetLastWriteTimeUtc(cache));
    }

    [Fact]
    public async Task A_200_with_a_new_etag_replaces_the_saved_answer()
    {
        var context = Context();
        var cache = WriteCache(context, "W/\"abc\"", UpdateServer.Release("98.0.0", []));
        var body = UpdateServer.Release("99.0.0", [(Setup("99.0.0"), "setup"u8.ToArray())]);
        using var http = new HttpClient(new UpdateServer().Latest(() => UpdateServer.Json(body, ("ETag", "W/\"def\""))));

        var check = await UpdateService.CheckForUpdateAsync(http, UpdateTarget.WindowsInstalled, context, CancellationToken.None);

        Assert.Equal("99.0.0", check.Update!.Version);
        using var saved = JsonDocument.Parse(File.ReadAllText(cache));
        Assert.Equal("W/\"def\"", saved.RootElement.GetProperty("etag").GetString());
        Assert.Equal(body, saved.RootElement.GetProperty("body").GetString());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "body": "{}" }""")]
    [InlineData("""{ "etag": "W/\"abc\"" }""")]
    public async Task A_damaged_saved_answer_is_deleted_and_the_check_asks_afresh(string damaged)
    {
        var context = Context();
        Directory.CreateDirectory(context.StateDirectory);
        var cache = Path.Combine(context.StateDirectory, "latest-release.json");
        File.WriteAllText(cache, damaged);
        var server = new UpdateServer().Latest(UpdateServer.Release("0.0.1", []));
        using var http = new HttpClient(server);

        var check = await UpdateService.CheckForUpdateAsync(http, UpdateTarget.WindowsInstalled, context, CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.UpToDate, check.Outcome);
        Assert.Null(Assert.Single(server.Requests).IfNoneMatch);
        Assert.False(File.Exists(cache));
    }

    [Fact]
    public async Task A_403_with_no_requests_left_carries_the_reset_time()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddMinutes(37).ToUnixTimeSeconds();
        using var http = new HttpClient(new UpdateServer().Latest(() => UpdateServer.Status(HttpStatusCode.Forbidden,
            ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture)))));

        var check = await UpdateService.CheckForUpdateAsync(http, UpdateTarget.WindowsInstalled, Context(() => now), CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Failed, check.Outcome);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(reset), check.RetryAfter);
        Assert.Contains("403 (rate limited)", check.Message);
        Assert.Contains(UpdateService.TryLaterText(DateTimeOffset.FromUnixTimeSeconds(reset)), check.Message);
    }

    [Fact]
    public async Task A_429_waits_for_its_retry_after()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        using var http = new HttpClient(new UpdateServer().Latest(() => UpdateServer.Status(HttpStatusCode.TooManyRequests, ("Retry-After", "120"))));

        var check = await UpdateService.CheckForUpdateAsync(http, UpdateTarget.WindowsInstalled, Context(() => now), CancellationToken.None);

        Assert.Equal(UpdateCheckOutcome.Failed, check.Outcome);
        Assert.Equal(now.AddSeconds(120), check.RetryAfter);
        Assert.Contains("429 (rate limited)", check.Message);
        Assert.Contains(UpdateService.TryLaterText(now.AddSeconds(120)), check.Message);
    }

    [Fact]
    public async Task Until_the_limit_ends_neither_the_loop_nor_the_button_asks_github()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddHours(1);
        var server = new UpdateServer().Latest(() => UpdateServer.Status(HttpStatusCode.Forbidden,
            ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var trigger = new UpdateTrigger(() => now);
        var events = new Events();
        await using var loop = Loop(server, events, trigger, Options(Context(() => now)), automatic: true);

        await events.WaitAsync(e => e.Any(x => x.StartsWith("failed", StringComparison.Ordinal)));
        Assert.Equal(["checked Failed", "failed " + events.Texts[^1]], events.Log);
        await Task.Delay(400);
        Assert.False(trigger.Request());
        Assert.Equal(UpdateService.TryLaterText(reset), trigger.RefusalMessage());
        Assert.Equal(1, server.LatestCount);

        now = reset.AddSeconds(1);
        Assert.True(trigger.Request());
        await events.WaitAsync(e => e.Count(x => x == "checked Failed") == 2);
        Assert.Equal(2, server.LatestCount);
    }

    [Theory]
    [InlineData("X-RateLimit-Reset", "32503680000")]
    [InlineData("Retry-After", "999999999")]
    public async Task A_far_off_limit_never_stops_the_checks_for_more_than_six_hours(string header, string value)
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        using var http = new HttpClient(new UpdateServer().Latest(() => UpdateServer.Status(HttpStatusCode.Forbidden, ("X-RateLimit-Remaining", "0"), (header, value))));

        var check = await UpdateService.CheckForUpdateAsync(http, UpdateTarget.WindowsInstalled, Context(() => now), CancellationToken.None);

        Assert.Equal(now + UpdateService.LongestLimit, check.RetryAfter);
    }

    [Fact]
    public async Task Stopping_the_loop_ends_a_download_in_progress_without_a_callback()
    {
        var payload = "stopped 99.8.0"u8.ToArray();
        var server = new UpdateServer().Latest(UpdateServer.Release("99.8.0", [(Setup("99.8.0"), payload)])).File(Setup("99.8.0"), payload);
        server.Hold(Setup("99.8.0"), new TaskCompletionSource().Task);
        var events = new Events();
        using var http = new HttpClient(server);
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => UpdateService.WatchAsync(http, false, true, events.Callbacks, new UpdateTrigger(), Options(Context()), stop.Token));

        await WaitUntil(() => server.Count(Setup("99.8.0")) == 1);
        await stop.CancelAsync();
        await loop.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.True(loop.IsCompletedSuccessfully);
        Assert.Equal(["checked Available 99.8.0"], events.Log);
    }

    // ---- The loop (AC4–AC7) ----

    [Fact]
    public async Task A_newer_release_replaces_the_staged_one_and_the_old_file_goes_only_after()
    {
        var v1 = "staged 99.1.0"u8.ToArray();
        var v2 = "staged 99.1.1"u8.ToArray();
        var server = new UpdateServer()
            .Latest(UpdateServer.Release("99.1.0", [(Setup("99.1.0"), v1)]))
            .Latest(UpdateServer.Release("99.1.1", [(Setup("99.1.1"), v2)]))
            .File(Setup("99.1.0"), v1)
            .File(Setup("99.1.1"), v2);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Hold(Setup("99.1.1"), release.Task);
        var events = new Events();
        await using var loop = Loop(server, events, new UpdateTrigger(), Options(Context()), automatic: true);

        await events.WaitAsync(e => e.Contains("staged 99.1.0"));
        var first = events.Infos.Single(i => i.Version == "99.1.0");
        await WaitUntil(() => server.Count(Setup("99.1.1")) == 1);
        Assert.True(File.Exists(UpdateService.StagedPath(first)));
        Assert.True(UpdateService.StagedFileMatches(first));

        release.SetResult();
        await events.WaitAsync(e => e.Contains("staged 99.1.1"));
        Assert.False(File.Exists(UpdateService.StagedPath(first)));
        Assert.Equal(v2, File.ReadAllBytes(UpdateService.StagedPath(events.Infos.Single(i => i.Version == "99.1.1"))));

        // Later checks see 99.1.1 again: nothing more is reported or downloaded.
        await events.WaitAsync(e => e.Count(x => x.StartsWith("checked", StringComparison.Ordinal)) >= 4);
        Assert.Equal(["staged 99.1.0", "staged 99.1.1"], events.Log.Where(x => x.StartsWith("staged", StringComparison.Ordinal)));
        Assert.Equal(1, server.Count(Setup("99.1.1")));
        Assert.DoesNotContain(events.Log, x => x.StartsWith("failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_copy_that_only_tells_is_told_once_per_version_and_keeps_watching()
    {
        var server = new UpdateServer()
            .Latest(UpdateServer.Release("99.3.0", [(Setup("99.3.0"), "a"u8.ToArray())], "**Bigger** fixes", Page("99.3.0")))
            .Latest(UpdateServer.Release("99.3.0", [(Setup("99.3.0"), "a"u8.ToArray())], "**Bigger** fixes", Page("99.3.0")))
            .Latest(UpdateServer.Release("99.3.1", [(Setup("99.3.1"), "b"u8.ToArray()), ("SHA256SUMS.txt", null), ("SHA256SUMS.txt.sig", null)]));
        var events = new Events();
        await using var loop = Loop(server, events, new UpdateTrigger(), Options(Context()), automatic: true, notifyOnly: true);

        await events.WaitAsync(e => e.Count(x => x.StartsWith("checked", StringComparison.Ordinal)) >= 5);
        Assert.Equal(["available 99.3.0", "available 99.3.1"], events.Log.Where(x => !x.StartsWith("checked", StringComparison.Ordinal)));
        // Checked comes first, and the told release carries its notes and page.
        Assert.Equal(["checked Available 99.3.0", "available 99.3.0"], events.Log.Take(2));
        var told = events.Infos.First();
        Assert.Equal("Bigger fixes", told.Notes);
        Assert.Equal(new Uri(Page("99.3.0")), told.PageUrl);
        Assert.Equal(0, server.Count("/dl/"));
    }

    [Fact]
    public async Task With_automatic_checks_off_only_the_button_starts_one_full_round()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var payload = "manual 99.4.0"u8.ToArray();
        var server = new UpdateServer().Latest(UpdateServer.Release("99.4.0", [(Setup("99.4.0"), payload)])).File(Setup("99.4.0"), payload);
        var trigger = new UpdateTrigger(() => now);
        var events = new Events();
        await using var loop = Loop(server, events, trigger, Options(Context(() => now)), automatic: false);

        await Task.Delay(300);
        Assert.Empty(server.Requests);

        Assert.True(trigger.Request());
        await events.WaitAsync(e => e.Contains("staged 99.4.0"));
        Assert.Equal(["checked Available 99.4.0", "staged 99.4.0"], events.Log);

        now = now.AddSeconds(30);
        Assert.False(trigger.Request());
        Assert.Equal(Loc.T("Core.Update.TooSoon"), trigger.RefusalMessage());

        now = now.AddSeconds(31);
        Assert.True(trigger.Request());
        await events.WaitAsync(e => e.Count(x => x.StartsWith("checked", StringComparison.Ordinal)) == 2);
        Assert.Equal(2, server.LatestCount);
        Assert.Equal(1, server.Count(Setup("99.4.0")));
    }

    [Fact]
    public async Task The_button_is_refused_while_a_check_runs()
    {
        var now = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var payload = "busy 99.5.0"u8.ToArray();
        var server = new UpdateServer().Latest(UpdateServer.Release("99.5.0", [(Setup("99.5.0"), payload)])).File(Setup("99.5.0"), payload);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Hold(Setup("99.5.0"), release.Task);
        var trigger = new UpdateTrigger(() => now);
        var events = new Events();
        await using var loop = Loop(server, events, trigger, Options(Context(() => now)), automatic: false);

        Assert.True(trigger.Request());
        await WaitUntil(() => server.Count(Setup("99.5.0")) == 1);
        now = now.AddMinutes(5);
        Assert.False(trigger.Request());
        Assert.Equal(Loc.T("Core.Update.InProgress"), trigger.RefusalMessage());

        release.SetResult();
        await events.WaitAsync(e => e.Contains("staged 99.5.0"));
        await WaitUntil(trigger.Request);
    }

    [Fact]
    public async Task An_up_to_date_answer_is_checked_and_nothing_else()
    {
        var events = new Events();
        await using var loop = Loop(new UpdateServer().Latest(UpdateServer.Release("0.0.1", [])), events, new UpdateTrigger(), Options(Context()), automatic: true);

        await events.WaitAsync(e => e.Count >= 2);
        Assert.All(events.Log, x => Assert.Equal("checked UpToDate", x));
    }

    // ---- Notes ----

    [Fact]
    public void Release_notes_become_plain_text_and_long_ones_are_cut_at_a_line_end()
    {
        const string markdown = "# Hearthpulse 0.15.0\r\n\r\n<!-- hidden -->\r\n## What's new\r\n- **Check now** in [Settings](https://example.invalid)\r\n* `Faster` start ![pic](x.png)\r\n\r\n---\r\n> Not affiliated with Blizzard &amp; co.";
        Assert.Equal("Hearthpulse 0.15.0\n\nWhat's new\n• Check now in Settings\n• Faster start\n\nNot affiliated with Blizzard & co.", ReleaseNotes.ToPlainText(markdown));
        Assert.Null(ReleaseNotes.ToPlainText("  \n <!-- only a comment --> "));
        Assert.Null(ReleaseNotes.ToPlainText(null));

        var lines = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"- Line number {i} of the changes"));
        var cut = ReleaseNotes.ToPlainText(lines)!;
        Assert.True(cut.Length <= ReleaseNotes.MaxLength);
        Assert.EndsWith("of the changes…", cut);
    }

    [Theory]
    [InlineData("https://github.com/Zulut30/Hearthpulse_App/releases/tag/v0.15.0", true)]
    [InlineData("https://github.com/Zulut30/IceCrowV2/releases/tag/v99.0.0", false)]
    [InlineData("http://github.com/Zulut30/Hearthpulse_App/releases/tag/v0.15.0", false)]
    [InlineData("https://github.com/Zulut30/Hearthpulse_App/issues", false)]
    [InlineData("", false)]
    public void Only_this_project_s_release_page_is_linked(string url, bool linked) =>
        Assert.Equal(linked, ReleaseNotes.Page(url) is not null);

    [Fact]
    public void The_reply_gives_the_notes_page_and_the_signature_files()
    {
        var json = UpdateServer.Release("99.0.0", [(Setup("99.0.0"), "s"u8.ToArray()), ("SHA256SUMS.txt", "x"u8.ToArray()), ("SHA256SUMS.txt.sig", "y"u8.ToArray())],
            "Fixes", Page("99.0.0"));
        var info = UpdateService.ParseLatest(json, UpdateTarget.WindowsInstalled, "x64")!;
        Assert.Equal("Fixes", info.Notes);
        Assert.Equal(new Uri(Page("99.0.0")), info.PageUrl);
        Assert.Equal(UpdateServer.Url("SHA256SUMS.txt"), info.ChecksumsUrl);
        Assert.Equal(UpdateServer.Url("SHA256SUMS.txt.sig"), info.SignatureUrl);
        Assert.Null(UpdateService.ParseLatest(AllTargetsReply, UpdateTarget.WindowsInstalled, "x64")!.PageUrl);
    }

    internal static string Setup(string version) => UpdateService.AssetName(version, UpdateTarget.WindowsInstalled, "x64");

    internal static string Page(string version) => $"https://github.com/Zulut30/Hearthpulse_App/releases/tag/v{version}";

    internal static UpdateContext Context(Func<DateTimeOffset>? now = null, IReadOnlyList<byte[]>? keys = null) =>
        new(Path.Combine(TestDataFolder.Path, "update-state-" + Guid.NewGuid().ToString("N")), now ?? (() => DateTimeOffset.UtcNow), keys ?? []);

    internal static UpdateService.WatchOptions Options(UpdateContext context, UpdateTarget target = UpdateTarget.WindowsInstalled) =>
        new(TimeSpan.Zero, TimeSpan.FromMilliseconds(50), target, Path.Combine(TestDataFolder.Path, "app-" + Guid.NewGuid().ToString("N")), _ => true, context);

    /// <summary>Starts the loop; disposing stops it and waits for it to end.</summary>
    internal static RunningLoop Loop(UpdateServer server, Events events, UpdateTrigger trigger, UpdateService.WatchOptions options, bool automatic, bool notifyOnly = false)
    {
        var http = new HttpClient(server);
        var stop = new CancellationTokenSource();
        var task = Task.Run(() => UpdateService.WatchAsync(http, notifyOnly, automatic, events.Callbacks, trigger, options, stop.Token));
        return new RunningLoop(http, stop, task);
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }

    private static string WriteCache(UpdateContext context, string etag, string body)
    {
        Directory.CreateDirectory(context.StateDirectory);
        var cache = Path.Combine(context.StateDirectory, "latest-release.json");
        File.WriteAllText(cache, JsonSerializer.Serialize(new Dictionary<string, string> { ["etag"] = etag, ["body"] = body }));
        return cache;
    }

    internal sealed class RunningLoop(HttpClient http, CancellationTokenSource stop, Task task) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await task.WaitAsync(TimeSpan.FromSeconds(30));
            stop.Dispose();
            http.Dispose();
        }
    }

    /// <summary>What the loop reported, in order: "checked Outcome [version]", "available v", "staged v", "failed text".</summary>
    internal sealed class Events
    {
        private readonly Lock _gate = new();
        private readonly List<string> _log = [];
        private readonly List<UpdateInfo> _infos = [];
        private readonly List<string> _texts = [];

        public UpdateCallbacks Callbacks => new(
            info => Add("available " + info.Version, info),
            info => Add("staged " + info.Version, info),
            text =>
            {
                var shown = text();
                lock (_gate)
                {
                    _texts.Add(shown);
                }

                Add("failed " + shown, null);
            },
            check => Add($"checked {check.Outcome}{(check.Update is { } u ? " " + u.Version : string.Empty)}", null));

        public List<string> Log
        {
            get
            {
                lock (_gate)
                {
                    return [.. _log];
                }
            }
        }

        public List<UpdateInfo> Infos
        {
            get
            {
                lock (_gate)
                {
                    return [.. _infos];
                }
            }
        }

        public List<string> Texts
        {
            get
            {
                lock (_gate)
                {
                    return [.. _texts];
                }
            }
        }

        public Task WaitAsync(Func<List<string>, bool> condition) => WaitUntil(() => condition(Log));

        private void Add(string entry, UpdateInfo? info)
        {
            lock (_gate)
            {
                _log.Add(entry);
                if (info is not null)
                {
                    _infos.Add(info);
                }
            }
        }
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

/// <summary>
/// A fake GitHub for the update tests: answers by address, the releases/latest replies in turn (the last one repeats),
/// records every request with its If-None-Match, and can hold a download until the test lets it go.
/// </summary>
internal sealed class UpdateServer : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly Queue<Func<HttpResponseMessage>> _latest = new();
    private readonly Dictionary<string, Func<HttpResponseMessage>> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _holds = new(StringComparer.Ordinal);
    private Func<HttpResponseMessage>? _lastLatest;

    public List<(string Url, string? IfNoneMatch)> Requests { get; } = [];

    public static string Url(string name) => "https://example.invalid/dl/" + name;

    public UpdateServer Latest(Func<HttpResponseMessage> reply)
    {
        lock (_gate)
        {
            _latest.Enqueue(reply);
        }

        return this;
    }

    public UpdateServer Latest(string json) => Latest(() => Json(json));

    public UpdateServer File(string name, byte[] content)
    {
        lock (_gate)
        {
            _files[Url(name)] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
        }

        return this;
    }

    public UpdateServer File(string name, string content) => File(name, System.Text.Encoding.UTF8.GetBytes(content));

    /// <summary>The download of <paramref name="name"/> waits for <paramref name="release"/>.</summary>
    public void Hold(string name, Task release)
    {
        lock (_gate)
        {
            _holds[Url(name)] = release;
        }
    }

    public int Count(string urlPart)
    {
        lock (_gate)
        {
            return Requests.Count(r => r.Url.Contains(urlPart, StringComparison.Ordinal));
        }
    }

    public int LatestCount => Count("api.github.com");

    public static HttpResponseMessage Json(string json, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    /// <summary>A releases/latest reply: the tag, the files (name, content or null for no digest), a body and a page.</summary>
    public static string Release(string version, IEnumerable<(string Name, byte[]? Content)> assets, string? body = null, string? page = null) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["tag_name"] = "v" + version,
            ["draft"] = false,
            ["prerelease"] = false,
            ["body"] = body,
            ["html_url"] = page,
            ["assets"] = assets.Select(a => new Dictionary<string, string?>
            {
                ["name"] = a.Name,
                ["browser_download_url"] = Url(a.Name),
                ["digest"] = a.Content is null ? null : "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(a.Content)),
            }).ToList(),
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Func<HttpResponseMessage>? reply;
        Task? hold;
        lock (_gate)
        {
            Requests.Add((url, request.Headers.TryGetValues("If-None-Match", out var tags) ? tags.FirstOrDefault() : null));
            if (url.Contains("api.github.com", StringComparison.Ordinal))
            {
                _lastLatest = _latest.Count > 0 ? _latest.Dequeue() : _lastLatest;
                reply = _lastLatest;
            }
            else
            {
                reply = _files.GetValueOrDefault(url);
            }

            hold = _holds.GetValueOrDefault(url);
        }

        if (hold is not null)
        {
            await hold.WaitAsync(cancellationToken);
        }

        return reply?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
