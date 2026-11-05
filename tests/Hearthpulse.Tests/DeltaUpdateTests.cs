using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// Delta updates of the installed Windows app: taken only when the installed copy is exactly the release the package
/// starts from and every file checks out; any doubt (a changed local file, a wrong digest, a corrupt file in the package,
/// an earlier failure) leaves the full setup to update. The helper script swaps by renames and puts everything back
/// when a rename fails; on Windows it is run for real against a scratch folder.
/// </summary>
public sealed class DeltaUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hp-delta-{Guid.NewGuid():N}");

    public DeltaUpdateTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // Old: a.dll (changes), keep.dll (same), gone.txt (removed). New: a.dll, keep.dll, ru/new.dll (added).
    private static readonly Dictionary<string, string> OldFiles = new()
    {
        ["a.dll"] = "old a", ["keep.dll"] = "same", ["gone.txt"] = "removed in the new version",
    };

    private static readonly Dictionary<string, string> NewFiles = new()
    {
        ["a.dll"] = "new a", ["keep.dll"] = "same", ["ru/new.dll"] = "added in the new version",
    };

    [Fact]
    public void The_plan_names_replaced_added_and_removed_files()
    {
        var plan = Plan("1.0.0", "1.1.0");
        Assert.Equal(["a.dll"], plan.Replaced.Select(f => f.Path));
        Assert.Equal(["ru/new.dll"], plan.Added.Select(f => f.Path));
        Assert.Equal(["gone.txt"], plan.Removed.Select(f => f.Path));
        Assert.Equal(["a.dll", "ru/new.dll"], plan.Carried.Select(f => f.Path));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("/abs.dll")]
    [InlineData("C:/abs.dll")]
    [InlineData("back\\slash.dll")]
    [InlineData("per%cent.dll")]
    [InlineData("amp&.dll")]
    [InlineData("dir/")]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("ru/COM1.dll")]
    [InlineData("a.")]
    [InlineData("...")]
    [InlineData("trailing ")]
    public void A_plan_with_a_path_outside_the_app_folder_or_unquotable_is_refused(string path)
    {
        Assert.False(DeltaUpdate.IsSafePath(path));
        var json = JsonSerializer.Serialize(new
        {
            from = "1.0.0", to = "1.1.0", @base = Array.Empty<object>(),
            target = new[] { new { path, size = 1, sha256 = new string('a', 64) } },
        });
        Assert.Null(DeltaUpdate.ReadPlan(json));
    }

    [Theory]
    [InlineData("""{ "from": "1.0.0", "to": "1.1.0", "base": [], "target": [ { "path": "a.dll", "size": 1, "sha256": "XYZ" } ] }""")]
    [InlineData("""{ "from": "1.0.0", "to": "1.1.0", "base": [], "target": [ { "path": "a.dll", "size": -1, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ] }""")]
    [InlineData("""{ "from": "1.0.0", "to": "1.1.0", "base": [], "target": [ { "path": "A.dll", "size": 1, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, { "path": "a.dll", "size": 1, "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ] }""")]
    [InlineData("""{ "from": "", "to": "1.1.0", "base": [], "target": [] }""")]
    [InlineData("""{ "from": "1.0.0", "to": "1.1.0", "base": [] }""")]
    [InlineData("""[ 1, 2 ]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_broken_plan_is_refused(string json) => Assert.Null(DeltaUpdate.ReadPlan(json));

    [Fact]
    public void The_release_offers_the_delta_only_for_the_installed_version_with_a_digest()
    {
        var reply = Release("1.1.0", ("Hearthpulse-Delta-1.0.0-1.1.0-win-x64.zip", Digest("x")), ("Hearthpulse-Delta-0.9.0-1.1.0-win-x64.zip", Digest("y")));
        var info = UpdateService.ParseLatest(reply, UpdateTarget.WindowsInstalled, "x64", "", "1.0.0");
        Assert.Equal("Hearthpulse-Delta-1.0.0-1.1.0-win-x64.zip", info!.Delta!.Name);
        Assert.Equal(Sha("x"), info.Delta.Sha256);

        Assert.Null(UpdateService.ParseLatest(reply, UpdateTarget.WindowsInstalled, "x64", "", "0.8.0")!.Delta);
        var preview = Release("1.1.0", ("Hearthpulse-Preview-1.1.0-win-x64.zip", null), ("Hearthpulse-Delta-1.0.0-1.1.0-win-x64.zip", Digest("x")));
        Assert.Null(UpdateService.ParseLatest(preview, UpdateTarget.WindowsPortable, "x64", "", "1.0.0")!.Delta);
        var unsigned = Release("1.1.0", ("Hearthpulse-Delta-1.0.0-1.1.0-win-x64.zip", null));
        Assert.Null(UpdateService.ParseLatest(unsigned, UpdateTarget.WindowsInstalled, "x64", "", "1.0.0")!.Delta);
    }

    [Fact]
    public async Task A_delta_that_fits_the_installed_copy_is_staged_with_every_file_checked()
    {
        var (app, info, server) = Setup("2.0.0");
        using var http = new HttpClient(server);
        Assert.True(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.0"));

        var staged = Path.Combine(DeltaUpdate.StagingDirectory(info), "files");
        Assert.Equal("new a", File.ReadAllText(Path.Combine(staged, "a.dll")));
        Assert.Equal("added in the new version", File.ReadAllText(Path.Combine(staged, "ru", "new.dll")));
        Assert.False(File.Exists(Path.Combine(staged, "keep.dll")));
        Assert.NotNull(DeltaUpdate.StagedPlan(info, app, "1.0.0"));
        Assert.False(File.Exists(Path.Combine(Core.AppPaths.Root, "updates", "delta", info.Delta!.Name)));
    }

    [Fact]
    public async Task A_changed_installed_file_leaves_the_update_to_the_setup()
    {
        var (app, info, server) = Setup("2.0.1");
        File.WriteAllText(Path.Combine(app, "keep.dll"), "edited by hand");
        using var http = new HttpClient(server);
        Assert.False(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.0"));
        Assert.False(Directory.Exists(DeltaUpdate.StagingDirectory(info)));
    }

    [Fact]
    public async Task A_package_that_fails_its_digest_is_not_used()
    {
        var (app, info, server) = Setup("2.0.2");
        using var http = new HttpClient(server);
        Assert.False(await DeltaUpdate.PrepareAsync(http, info with { Delta = info.Delta! with { Sha256 = Sha("other") } }, app, "1.0.0"));
        Assert.False(Directory.Exists(DeltaUpdate.StagingDirectory(info)));
    }

    [Fact]
    public async Task A_corrupt_file_in_the_package_is_not_used()
    {
        var (app, info, server) = Setup("2.0.3", corrupt: "a.dll");
        using var http = new HttpClient(server);
        Assert.False(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.0"));
        Assert.False(Directory.Exists(DeltaUpdate.StagingDirectory(info)));
    }

    [Fact]
    public async Task A_package_for_another_installed_version_or_after_a_failure_is_not_used()
    {
        var (app, info, server) = Setup("2.0.4");
        using var http = new HttpClient(server);
        Assert.False(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.9"));

        DeltaUpdate.MarkFailed(info);
        Assert.False(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.0"));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task A_file_already_where_the_new_version_adds_one_leaves_the_update_to_the_setup()
    {
        var (app, info, server) = Setup("2.0.5");
        Directory.CreateDirectory(Path.Combine(app, "ru"));
        File.WriteAllText(Path.Combine(app, "ru", "new.dll"), "someone else's file");
        using var http = new HttpClient(server);
        Assert.False(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.0"));
    }

    [Fact]
    public async Task A_staged_delta_changed_before_install_is_dropped_and_marked_for_the_setup()
    {
        var (app, info, server) = Setup("2.0.6");
        using var http = new HttpClient(server);
        Assert.True(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.0"));
        File.WriteAllText(Path.Combine(DeltaUpdate.StagingDirectory(info), "files", "a.dll"), "tampered");

        Assert.Null(DeltaUpdate.StagedPlan(info, app, "1.0.0"));
        Assert.False(Directory.Exists(DeltaUpdate.StagingDirectory(info)));
        Assert.True(File.Exists(DeltaUpdate.FailedMark(info)));
    }

    [Fact]
    public async Task The_update_loop_takes_the_delta_and_never_downloads_the_setup()
    {
        var (app, info, server) = Setup("97.0.0", installed: UpdateService.CurrentVersion);
        using var http = new HttpClient(server);
        Assert.Equal("staged", await Watch(http, app));
        Assert.Equal(0, server.SetupRequests);
        Assert.True(Directory.Exists(DeltaUpdate.StagingDirectory(info)));
    }

    [Fact]
    public async Task The_update_loop_falls_back_to_the_full_setup_when_the_delta_does_not_fit()
    {
        var (app, info, server) = Setup("97.0.1", installed: UpdateService.CurrentVersion);
        File.Delete(Path.Combine(app, "gone.txt"));
        using var http = new HttpClient(server);
        Assert.Equal("staged", await Watch(http, app));
        Assert.Equal(1, server.SetupRequests);
        Assert.False(Directory.Exists(DeltaUpdate.StagingDirectory(info)));
        Assert.True(File.Exists(UpdateService.StagedPath(info)));
    }

    [Fact]
    public void The_helper_swaps_by_renames_with_a_rollback_and_lists_added_files_for_the_uninstaller()
    {
        var script = DeltaUpdate.BuildApplyScript(7, @"C:\data\updates\delta-1.1.0\files", @"C:\Apps\Hearth 100%", Plan("1.0.0", "1.1.0"),
            null, @"Software\Test", @"C:\data\updates\delta-1.1.0.failed", null);
        Assert.Contains("PID eq 7", script);
        // Each step: the command, up to 5 tries a second apart, then where a failure goes.
        Assert.Contains("copy /Y \"C:\\data\\updates\\delta-1.1.0\\files\\ru\\new.dll\" \"C:\\Apps\\Hearth 100%%.update-new\\ru\\new.dll\" >nul 2>nul && goto s2ok\r\n"
            + "set /a TRY+=1\r\nif %TRY% lss 5 ( ping -n 2 127.0.0.1 >nul & goto s2 )\r\ngoto abort\r\n:s2ok", script);
        Assert.Contains("move /Y \"C:\\Apps\\Hearth 100%%\\gone.txt\" \"C:\\Apps\\Hearth 100%%.update-old\\gone.txt\" >nul 2>nul && goto s4ok\r\n"
            + "set /a TRY+=1\r\nif %TRY% lss 5 ( ping -n 2 127.0.0.1 >nul & goto s4 )\r\ngoto rollback", script);
        Assert.Contains("set \"KEEP_BACKUP=1\"", script);
        Assert.Contains("if defined KEEP_BACKUP goto broken", script);
        Assert.DoesNotContain("start \"\"", script[script.IndexOf(":broken", StringComparison.Ordinal)..]);
        Assert.Contains(@"reg add ""HKCU\Software\Test"" /v DisplayVersion /t REG_SZ /d ""1.1.0"" /f", script);
        Assert.Contains(@">>""C:\Apps\Hearth 100%%\uninstall-extra.txt"" echo ru\new.dll", script);
        Assert.Contains(@">>""C:\Apps\Hearth 100%%\uninstall-extra.txt"" echo ru\", script);
        Assert.DoesNotContain("keep.dll", script);
        Assert.True(script.IndexOf(":rollback", StringComparison.Ordinal) < script.IndexOf(":abort", StringComparison.Ordinal));
    }

    [WindowsFact]
    public void The_helper_installs_the_delta_on_disk()
    {
        var (app, staged, plan) = OnDisk();
        var key = $@"Software\Hearthpulse.Tests\{Guid.NewGuid():N}";
        var mark = Path.Combine(_root, "failed");
        try
        {
            Run(DeltaUpdate.BuildApplyScript(ExitedPid(), staged, app, plan, null, key, mark, null, tries: 2));

            Assert.Equal("new a", File.ReadAllText(Path.Combine(app, "a.dll")));
            Assert.Equal("same", File.ReadAllText(Path.Combine(app, "keep.dll")));
            Assert.Equal("added in the new version", File.ReadAllText(Path.Combine(app, "ru", "new.dll")));
            Assert.False(File.Exists(Path.Combine(app, "gone.txt")));
            Assert.Equal(["ru\\new.dll", "ru\\"], File.ReadAllLines(Path.Combine(app, DeltaUpdate.UninstallExtraFile)));
            Assert.False(Directory.Exists(app + ".update-old"));
            Assert.False(Directory.Exists(app + ".update-new"));
            Assert.False(File.Exists(mark));
            Assert.Equal("1.1.0", RegistryVersion(key));
        }
        finally
        {
            DeleteKey(key);
        }
    }

    [WindowsFact]
    public void A_failed_rename_puts_every_old_file_back_and_marks_the_delta_failed()
    {
        var (app, staged, plan) = OnDisk();
        var mark = Path.Combine(_root, "failed");
        var key = $@"Software\Hearthpulse.Tests\{Guid.NewGuid():N}";
        try
        {
            // a.dll goes to the backup first; holding gone.txt open makes the next rename fail, after a.dll has moved.
            using (new FileStream(Path.Combine(app, "gone.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Run(DeltaUpdate.BuildApplyScript(ExitedPid(), staged, app, plan, null, key, mark, null, tries: 2));
            }

            Assert.Equal("old a", File.ReadAllText(Path.Combine(app, "a.dll")));
            Assert.Equal("removed in the new version", File.ReadAllText(Path.Combine(app, "gone.txt")));
            Assert.False(File.Exists(Path.Combine(app, "ru", "new.dll")));
            Assert.False(File.Exists(Path.Combine(app, DeltaUpdate.UninstallExtraFile)));
            Assert.False(Directory.Exists(app + ".update-old"));
            Assert.False(Directory.Exists(app + ".update-new"));
            Assert.True(File.Exists(mark));
            Assert.Null(RegistryVersion(key));
        }
        finally
        {
            DeleteKey(key);
        }
    }

    [WindowsFact]
    public void A_replaced_file_held_open_stops_the_swap_before_anything_moves()
    {
        var (app, staged, plan) = OnDisk();
        var mark = Path.Combine(_root, "failed");
        using (new FileStream(Path.Combine(app, "a.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Run(DeltaUpdate.BuildApplyScript(ExitedPid(), staged, app, plan, null, @"Software\Hearthpulse.Tests\none", mark, null, tries: 2));
        }

        Assert.Equal("old a", File.ReadAllText(Path.Combine(app, "a.dll")));
        Assert.Equal("removed in the new version", File.ReadAllText(Path.Combine(app, "gone.txt")));
        Assert.False(File.Exists(Path.Combine(app, "ru", "new.dll")));
        Assert.False(DeltaUpdate.IsBroken(app));
        Assert.True(File.Exists(mark));
    }

    [WindowsFact]
    public void A_copy_that_fails_as_on_a_full_disk_touches_nothing()
    {
        var (app, staged, plan) = OnDisk();
        var mark = Path.Combine(_root, "failed");
        File.Delete(Path.Combine(staged, "ru", "new.dll"));
        Run(DeltaUpdate.BuildApplyScript(ExitedPid(), staged, app, plan, null, @"Software\Hearthpulse.Tests\none", mark, null, tries: 2));

        Assert.Equal(OldFiles.Keys.Order(), Directory.GetFiles(app).Select(p => Path.GetFileName(p)).Order());
        Assert.Equal("old a", File.ReadAllText(Path.Combine(app, "a.dll")));
        Assert.False(Directory.Exists(app + ".update-new"));
        Assert.False(Directory.Exists(app + ".update-old"));
        Assert.True(File.Exists(mark));
    }

    [WindowsFact]
    public void An_old_file_that_cannot_go_back_keeps_the_backup_and_does_not_start_the_app()
    {
        var (app, staged, plan) = OnDisk();
        var mark = Path.Combine(_root, "failed");
        var started = Path.Combine(_root, "started.txt");
        var relaunch = Path.Combine(_root, "relaunch.cmd");
        File.WriteAllText(relaunch, $"@echo started> \"{started}\"\r\n");
        // No new file may be created in the app folder: a.dll can leave it for the backup but cannot come back.
        Icacls(app, "/deny", "*S-1-1-0:(WD)");
        try
        {
            using (new FileStream(Path.Combine(app, "gone.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Run(DeltaUpdate.BuildApplyScript(ExitedPid(), staged, app, plan, relaunch, @"Software\Hearthpulse.Tests\none", mark, null, tries: 2));
            }
        }
        finally
        {
            Icacls(app, "/remove:d", "*S-1-1-0");
        }

        Assert.True(DeltaUpdate.IsBroken(app));
        Assert.Equal("old a", File.ReadAllText(Path.Combine(app + ".update-old", "a.dll")));
        Assert.Contains("backup kept", File.ReadAllText(mark));
        Thread.Sleep(500);
        Assert.False(File.Exists(started));
    }

    [Fact]
    public async Task An_unexpected_failure_while_preparing_leaves_the_update_to_the_setup()
    {
        var (app, info, _) = Setup("2.0.7");
        using var http = new HttpClient(new Throwing());
        Assert.False(await DeltaUpdate.PrepareAsync(http, info, app, "1.0.0"));
        Assert.False(Directory.Exists(DeltaUpdate.StagingDirectory(info)));
    }

    [Fact]
    public void A_plan_this_process_did_not_prepare_is_not_installed_and_not_marked()
    {
        var info = new UpdateInfo("2.0.8", "setup.exe", "https://example.invalid/s.exe", Sha("s"), new DeltaAsset("d.zip", "https://example.invalid/d.zip", Sha("d")));
        Directory.CreateDirectory(DeltaUpdate.StagingDirectory(info));
        File.WriteAllText(Path.Combine(DeltaUpdate.StagingDirectory(info), "delta.json"), "{}");
        Assert.Null(DeltaUpdate.StagedPlan(info, _root, "1.0.0"));
        Assert.False(File.Exists(DeltaUpdate.FailedMark(info)));
    }

    [Fact]
    public async Task A_half_updated_folder_is_repaired_with_the_full_setup_of_the_same_version()
    {
        var (app, info, server) = Setup(UpdateService.CurrentVersion, installed: UpdateService.CurrentVersion);
        Directory.CreateDirectory(app + ".update-old");
        using var http = new HttpClient(server);
        Assert.Equal("staged", await Watch(http, app));
        Assert.Equal(1, server.SetupRequests);
        Assert.Equal(0, server.Requests);
        Assert.True(File.Exists(UpdateService.StagedPath(info)));
    }

    [Fact]
    public void Leftovers_of_installed_versions_are_removed_after_a_start_unless_the_folder_is_half_updated()
    {
        var app = Write(Path.Combine(_root, "clean-app"), OldFiles);
        var delta = Path.Combine(Core.AppPaths.Root, "updates", "delta");
        Directory.CreateDirectory(Path.Combine(delta, "50.0.0"));
        File.WriteAllText(Path.Combine(delta, "49.0.0.failed"), "x");
        File.WriteAllText(Path.Combine(delta, "51.0.0.failed"), "x");
        File.WriteAllText(Path.Combine(delta, "Hearthpulse-Delta-1.0.0-60.0.0-win-x64.zip.part"), "x");
        Directory.CreateDirectory(app + ".update-new");
        Directory.CreateDirectory(app + ".update-old");

        DeltaUpdate.CleanUp(app, "50.0.0");
        Assert.True(Directory.Exists(Path.Combine(delta, "50.0.0")));
        Assert.True(Directory.Exists(app + ".update-new"));

        Directory.Delete(app + ".update-old");
        DeltaUpdate.CleanUp(app, "50.0.0");
        Assert.False(Directory.Exists(Path.Combine(delta, "50.0.0")));
        Assert.False(File.Exists(Path.Combine(delta, "49.0.0.failed")));
        Assert.True(File.Exists(Path.Combine(delta, "51.0.0.failed")));
        Assert.False(File.Exists(Path.Combine(delta, "Hearthpulse-Delta-1.0.0-60.0.0-win-x64.zip.part")));
        Assert.False(Directory.Exists(app + ".update-new"));
        File.Delete(Path.Combine(delta, "51.0.0.failed"));
    }

    private static void Icacls(string path, string action, string what)
    {
        using var process = Process.Start(new ProcessStartInfo("icacls", $"\"{path}\" {action} {what}") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("something nobody expected");
    }

    private async Task<string> Watch(HttpClient http, string app)
    {
        var outcome = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = UpdateService.WatchAsync(http, notifyOnly: false,
            available: _ => outcome.TrySetResult("available"),
            staged: _ => outcome.TrySetResult("staged"),
            failed: _ => outcome.TrySetResult("failed"),
            TimeSpan.Zero, UpdateTarget.WindowsInstalled, app, _ => true);
        return await outcome.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static DeltaPlan Plan(string from, string to) =>
        new(from, to, [.. OldFiles.Select(f => Entry(f.Key, f.Value))], [.. NewFiles.Select(f => Entry(f.Key, f.Value))]);

    private static DeltaFile Entry(string path, string content) => new(path, Encoding.UTF8.GetByteCount(content), Sha(content));

    private static string Sha(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static string Digest(string content) => "sha256:" + Sha(content);

    private static string Write(string dir, Dictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return dir;
    }

    /// <summary>An installed copy of <paramref name="installed"/>, a release <paramref name="version"/> with its setup and delta, and a server for both.</summary>
    private (string App, UpdateInfo Info, Server Server) Setup(string version, string installed = "1.0.0", string? corrupt = null)
    {
        var app = Write(Path.Combine(_root, "app-" + version), OldFiles);
        var plan = Plan(installed, version);
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var json = JsonSerializer.Serialize(new
            {
                from = plan.From, to = plan.To,
                @base = plan.Base.Select(f => new { path = f.Path, size = f.Size, sha256 = f.Sha256 }),
                target = plan.Target.Select(f => new { path = f.Path, size = f.Size, sha256 = f.Sha256 }),
            });
            using (var w = new StreamWriter(archive.CreateEntry("delta.json").Open()))
            {
                w.Write(json);
            }

            foreach (var file in plan.Carried)
            {
                using var w = new StreamWriter(archive.CreateEntry("files/" + file.Path).Open());
                w.Write(file.Path == corrupt ? "corrupt" : NewFiles[file.Path]);
            }
        }

        var deltaBytes = zip.ToArray();
        var setup = Encoding.UTF8.GetBytes("full setup " + version);
        var deltaName = DeltaUpdate.AssetName(installed, version);
        var setupName = UpdateService.AssetName(version, UpdateTarget.WindowsInstalled, "x64");
        var reply = Release(version, (setupName, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(setup))),
            (deltaName, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(deltaBytes))));
        var info = UpdateService.ParseLatest(reply, UpdateTarget.WindowsInstalled, "x64", "", installed)!;
        return (app, info, new Server(reply, deltaBytes, setup));
    }

    private static string Release(string version, params (string Name, string? Digest)[] assets) =>
        JsonSerializer.Serialize(new
        {
            tag_name = "v" + version, draft = false, prerelease = false,
            assets = assets.Select(a => new Dictionary<string, string?>
            {
                ["name"] = a.Name, ["browser_download_url"] = "https://example.invalid/" + a.Name, ["digest"] = a.Digest,
            }).Append(new Dictionary<string, string?>
            {
                ["name"] = UpdateService.AssetName(version, UpdateTarget.WindowsInstalled, "x64"),
                ["browser_download_url"] = "https://example.invalid/" + UpdateService.AssetName(version, UpdateTarget.WindowsInstalled, "x64"),
            }).GroupBy(a => a["name"]).Select(g => g.First()),
        });

    /// <summary>A 1.0.0 app folder, the staged files of 1.1.0, and the plan between them.</summary>
    private (string App, string Staged, DeltaPlan Plan) OnDisk()
    {
        var plan = Plan("1.0.0", "1.1.0");
        var app = Write(Path.Combine(_root, "Hearth app"), OldFiles);
        var staged = Write(Path.Combine(_root, "staged"), plan.Carried.ToDictionary(f => f.Path, f => NewFiles[f.Path]));
        return (app, staged, plan);
    }

    private void Run(string script)
    {
        var path = Path.Combine(_root, "apply.cmd");
        File.WriteAllText(path, script);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{path}\"") { CreateNoWindow = true, UseShellExecute = false })!;
        Assert.True(process.WaitForExit(60_000));
    }

    private static int ExitedPid()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        process.WaitForExit();
        return process.Id;
    }

    private static string? RegistryVersion(string key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue("DisplayVersion") as string;
    }

    private static void DeleteKey(string key)
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
    }

    /// <summary>The API reply, the delta package and the setup, by address; counts what is asked for.</summary>
    private sealed class Server(string reply, byte[] delta, byte[] setup) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public int SetupRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            HttpContent content;
            if (url.Contains("api.github.com", StringComparison.Ordinal))
            {
                content = new StringContent(reply, Encoding.UTF8, "application/json");
            }
            else if (url.EndsWith(".exe", StringComparison.Ordinal))
            {
                SetupRequests++;
                content = new ByteArrayContent(setup);
            }
            else
            {
                Requests++;
                content = new ByteArrayContent(delta);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
