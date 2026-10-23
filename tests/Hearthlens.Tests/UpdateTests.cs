using System.Net;
using Hearthlens.Core;
using Hearthlens.Core.Updates;
using Xunit;

namespace Hearthlens.Tests;

/// <summary>
/// The update check must never bother the player with an error: GitHub answers with nulls, assets it cannot name,
/// drafts and plain failures (no network, 403 without a user agent), and every one of those means "no update".
/// The version compare and the install helper are pure, so they are checked here directly.
/// </summary>
public sealed class UpdateTests
{
    private const string AllTargetsReply = """
        {
          "tag_name": "v99.0.0",
          "html_url": "https://github.com/Dev-NotAqua/HeartLens/releases/tag/v99.0.0",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "Hearthlens-Setup-99.0.0-win-x64.exe", "browser_download_url": "https://example.invalid/setup.exe" },
            { "name": "Hearthlens-Preview-99.0.0-win-x64.zip", "browser_download_url": "https://example.invalid/preview.zip" },
            { "name": "Hearthlens-99.0.0-macOS-arm64.pkg", "browser_download_url": "https://example.invalid/mac-arm64.pkg" },
            { "name": "Hearthlens-99.0.0-macOS-x64.pkg", "browser_download_url": "https://example.invalid/mac-x64.pkg" },
            { "name": "hearthlens_99.0.0_amd64.deb", "browser_download_url": "https://example.invalid/deb-amd64" },
            { "name": "hearthlens_99.0.0_arm64.deb", "browser_download_url": "https://example.invalid/deb-arm64" },
            { "name": "hearthlens-99.0.0-1.x86_64.rpm", "browser_download_url": "https://example.invalid/rpm-x86_64" },
            { "name": "hearthlens-99.0.0-1.aarch64.rpm", "browser_download_url": "https://example.invalid/rpm-aarch64" },
            { "name": "Hearthlens-99.0.0-linux-x64.tar.gz", "browser_download_url": "https://example.invalid/tar-x64" },
            { "name": "Hearthlens-99.0.0-linux-arm64.tar.gz", "browser_download_url": "https://example.invalid/tar-arm64" }
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
    [InlineData(UpdateTarget.WindowsInstalled, "x64", "", "Hearthlens-Setup-0.5.0-win-x64.exe")]
    [InlineData(UpdateTarget.WindowsPortable, "x64", "", "Hearthlens-Preview-0.5.0-win-x64.zip")]
    [InlineData(UpdateTarget.MacPackage, "arm64", "", "Hearthlens-0.5.0-macOS-arm64.pkg")]
    [InlineData(UpdateTarget.MacPackage, "x64", "", "Hearthlens-0.5.0-macOS-x64.pkg")]
    [InlineData(UpdateTarget.LinuxPackage, "x64", "deb", "hearthlens_0.5.0_amd64.deb")]
    [InlineData(UpdateTarget.LinuxPackage, "arm64", "deb", "hearthlens_0.5.0_arm64.deb")]
    [InlineData(UpdateTarget.LinuxPackage, "x64", "rpm", "hearthlens-0.5.0-1.x86_64.rpm")]
    [InlineData(UpdateTarget.LinuxPackage, "arm64", "rpm", "hearthlens-0.5.0-1.aarch64.rpm")]
    [InlineData(UpdateTarget.LinuxPackage, "x64", "", "Hearthlens-0.5.0-linux-x64.tar.gz")]
    public void Asset_names_match_the_released_files(UpdateTarget target, string architecture, string flavor, string expected) =>
        Assert.Equal(expected, UpdateService.AssetName("0.5.0", target, architecture, flavor));

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
    [InlineData("""{ "tag_name": "v0.5.0", "assets": [ { "name": "Hearthlens-Setup-0.5.0-win-x64.exe" } ] }""")]
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

    [Fact]
    public async Task No_network_is_not_an_error()
    {
        using var http = new HttpClient(new Offline());
        Assert.Null(await UpdateService.CheckAsync(http));
    }

    [Fact]
    public void The_staged_file_waits_in_the_data_folder()
    {
        var info = new UpdateInfo("99.0.0", "Hearthlens-Setup-99.0.0-win-x64.exe", "https://example.invalid/setup.exe", "https://example.invalid/page");
        var staged = UpdateService.StagedPath(info);
        Assert.StartsWith(AppPaths.Root, staged);
        Assert.EndsWith(info.AssetName, staged);
    }

    [Fact]
    public void The_install_helper_waits_then_runs_the_setup_silently()
    {
        var script = UpdateService.BuildApplyScript(1234, @"C:\tmp\setup.exe", @"C:\tmp\setup.exe", @"C:\app", @"C:\app\Hearthlens.exe", installer: true, cleanupDir: null);
        Assert.Contains(":wait", script);
        Assert.Contains(@"PID eq 1234", script);
        Assert.Contains("start \"\" /wait \"C:\\tmp\\setup.exe\" /S", script);
        Assert.Contains("start \"\" \"C:\\app\\Hearthlens.exe\"", script);
        Assert.DoesNotContain("xcopy", script);
        Assert.Contains("del \"%~f0\"", script);
    }

    [Fact]
    public void The_zip_helper_unpacks_over_the_app_folder_and_clears_the_temp_copy()
    {
        var script = UpdateService.BuildApplyScript(42, @"C:\tmp\preview.zip", @"C:\tmp\hearthlens-update", @"C:\app", null, installer: false, cleanupDir: @"C:\tmp\hearthlens-update");
        Assert.Contains("xcopy /E /Y /I \"C:\\tmp\\hearthlens-update\\*\" \"C:\\app\\\"", script);
        Assert.Contains("rmdir /S /Q \"C:\\tmp\\hearthlens-update\"", script);
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

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("The network is down.");
    }
}
