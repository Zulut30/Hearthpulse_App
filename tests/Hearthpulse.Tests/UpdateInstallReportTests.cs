using System.Text.Json;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Updates;

namespace Hearthpulse.Tests;

/// <summary>
/// The next start tells what became of the install the last run started: decided by the running version (so a PC
/// switched off mid-install counts too), with the helper's exit code and rollback as details. Two failed installs of
/// one version leave it to a manual install until a newer version is out. The player's data is never touched.
/// </summary>
[Collection(nameof(UpdateStagingCollection))]
public sealed class UpdateInstallReportTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"hp-report-{Guid.NewGuid():N}");

    private string State => Path.Combine(_data, "update-state");

    private string App => Path.Combine(_data, "Programs", "Hearthpulse");

    public UpdateInstallReportTests()
    {
        Directory.CreateDirectory(App);
        File.WriteAllText(Path.Combine(_data, "history.db"), "games");
        File.WriteAllText(Path.Combine(_data, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_data, "updates"));
        File.WriteAllText(Path.Combine(_data, "updates", "Hearthpulse-Setup-0.15.0-win-x64.exe"), "staged setup");
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public void No_marker_means_no_report()
    {
        Assert.Null(InstallState.Take(State, "0.14.0", App));
    }

    [Fact]
    public void Reaching_the_version_is_a_success_that_clears_the_marker_and_the_backup()
    {
        InstallState.WritePending(State, "0.15.0", "0.14.0", "Hearthpulse-Setup-0.15.0-win-x64.exe", DateTimeOffset.UtcNow);
        File.WriteAllLines(Path.Combine(State, InstallState.ResultFile), ["exit=0", "attempts=1", "backup=1", "restored=0"]);
        Directory.CreateDirectory(Path.Combine(App + ".setup-backup", "runtimes"));

        var report = InstallState.Take(State, "0.15.0", App);

        Assert.Equal(new InstallReport("0.15.0", Succeeded: true, ExitCode: 0, FailedAttempts: 0, RolledBack: false), report);
        Assert.Equal(string.Empty, report!.Message());
        Assert.False(File.Exists(Path.Combine(State, InstallState.PendingFile)));
        Assert.False(File.Exists(Path.Combine(State, InstallState.ResultFile)));
        Assert.False(Directory.Exists(App + ".setup-backup"));
        Assert.Null(InstallState.Take(State, "0.15.0", App));
        DataUntouched();
    }

    [Fact]
    public void A_newer_running_version_also_counts_as_installed()
    {
        InstallState.WritePending(State, "0.15.0", "0.14.0", "setup", DateTimeOffset.UtcNow);
        Assert.True(InstallState.Take(State, "0.15.1", App)!.Succeeded);
    }

    [Fact]
    public void Staying_on_the_old_version_is_a_failure_with_the_helper_s_details()
    {
        InstallState.WritePending(State, "0.15.0", "0.14.0", "Hearthpulse-Setup-0.15.0-win-x64.exe", DateTimeOffset.UtcNow);
        File.WriteAllLines(Path.Combine(State, InstallState.ResultFile), ["exit=2", "attempts=2", "backup=1", "restored=1"]);

        var report = InstallState.Take(State, "0.14.0", App);

        Assert.Equal(new InstallReport("0.15.0", Succeeded: false, ExitCode: 2, FailedAttempts: 1, RolledBack: true), report);
        Assert.Equal(Loc.F("Core.Update.InstallFailed", "0.15.0"), report!.Message());
        Assert.False(File.Exists(Path.Combine(State, InstallState.PendingFile)));
        Assert.False(File.Exists(Path.Combine(State, InstallState.ResultFile)));
        // The staged file stays: the install is tried again at the next exit.
        Assert.True(File.Exists(Path.Combine(_data, "updates", "Hearthpulse-Setup-0.15.0-win-x64.exe")));
        DataUntouched();
    }

    [Fact]
    public void A_pc_switched_off_mid_install_leaves_no_exit_code_and_the_second_failure_asks_for_a_manual_install()
    {
        InstallState.WritePending(State, "0.15.0", "0.14.0", "setup", DateTimeOffset.UtcNow);
        var first = InstallState.Take(State, "0.14.0", App)!;
        Assert.Equal(new InstallReport("0.15.0", false, null, 1, false), first);

        InstallState.WritePending(State, "0.15.0", "0.14.0", "setup", DateTimeOffset.UtcNow);
        var second = InstallState.Take(State, "0.14.0", App)!;
        Assert.Equal(2, second.FailedAttempts);
        Assert.Equal(Loc.F("Core.Update.InstallFailedManual", "0.15.0"), second.Message());
        Assert.Equal(2, InstallState.FailedAttempts(State, "0.15.0"));
        DataUntouched();
    }

    [Fact]
    public void A_damaged_marker_is_dropped_without_a_report()
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, InstallState.PendingFile), "{ broken");
        Assert.Null(InstallState.Take(State, "0.14.0", App));
        Assert.False(File.Exists(Path.Combine(State, InstallState.PendingFile)));
    }

    [Fact]
    public void A_later_success_clears_the_failure_count()
    {
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, InstallState.FailuresFile), JsonSerializer.Serialize(new Dictionary<string, int> { ["0.15.0"] = 1 }));
        InstallState.WritePending(State, "0.15.0", "0.14.0", "setup", DateTimeOffset.UtcNow);
        Assert.True(InstallState.Take(State, "0.15.0", App)!.Succeeded);
        Assert.Equal(0, InstallState.FailedAttempts(State, "0.15.0"));
    }

    [Fact]
    public async Task A_version_that_failed_twice_is_not_staged_again_until_a_newer_one_is_out()
    {
        var context = UpdateTests.Context() with { StateDirectory = State };
        Directory.CreateDirectory(State);
        File.WriteAllText(Path.Combine(State, InstallState.FailuresFile), JsonSerializer.Serialize(new Dictionary<string, int> { ["99.6.0"] = 2 }));
        var blocked = "blocked 99.6.0"u8.ToArray();
        var next = "next 99.6.1"u8.ToArray();
        var server = new UpdateServer()
            .Latest(UpdateServer.Release("99.6.0", [(UpdateTests.Setup("99.6.0"), blocked)]))
            .Latest(UpdateServer.Release("99.6.1", [(UpdateTests.Setup("99.6.1"), next)]))
            .File(UpdateTests.Setup("99.6.0"), blocked)
            .File(UpdateTests.Setup("99.6.1"), next);
        var events = new UpdateTests.Events();
        await using (UpdateTests.Loop(server, events, new UpdateTrigger(), UpdateTests.Options(context), automatic: true))
        {
            await events.WaitAsync(e => e.Contains("staged 99.6.1"));
        }

        Assert.Equal(["checked Available 99.6.0", "failed " + Loc.F("Core.Update.InstallFailedManual", "99.6.0"), "checked Available 99.6.1", "staged 99.6.1"],
            events.Log.Take(4));
        Assert.Equal(0, server.Count(UpdateTests.Setup("99.6.0")));
    }

    private void DataUntouched()
    {
        Assert.Equal("games", File.ReadAllText(Path.Combine(_data, "history.db")));
        Assert.Equal("{}", File.ReadAllText(Path.Combine(_data, "settings.json")));
        Assert.True(Directory.Exists(App));
    }
}
