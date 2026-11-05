using System.ComponentModel;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>The setup wizard (D4, MainViewModel.Setup.cs): its steps, when it opens by itself, the keys and the logs fix.</summary>
public sealed class SetupWizardTests : IDisposable
{
    private readonly string _dir = TestDataFolder.Scratch("setup-wizard");

    public void Dispose() => TestDataFolder.TryDelete(_dir);

    private static MainViewModel Vm(AppSettings? settings = null) =>
        new(HistoryStore.InMemory(), settings ?? new AppSettings { MigrationOffered = true }, TestDataFolder.App);

    /// <summary>A view model whose Hearthstone is the test's folder and whose log.config is a file in it.</summary>
    private MainViewModel WithLogs(out string logConfig)
    {
        var vm = Vm(new AppSettings { MigrationOffered = true, HearthstonePath = _dir });
        logConfig = Path.Combine(_dir, "Blizzard", "Hearthstone", "log.config");
        vm.SetupLogConfigFile = logConfig;
        return vm;
    }

    /// <summary>Presses "Turn logs on" and waits for its note (the fix runs off the UI thread).</summary>
    private static async Task FixAsync(MainViewModel vm)
    {
        var noted = new TaskCompletionSource();
        PropertyChangedEventHandler handler = (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SetupCanFixLogs) && vm.SetupLogsNote.Length > 0)
            {
                noted.TrySetResult();
            }
        };
        vm.PropertyChanged += handler;
        vm.FixLogs.Execute(null);
        await noted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.PropertyChanged -= handler;
    }

    [Fact]
    public void Steps_go_forward_and_back_and_stop_at_both_ends()
    {
        var vm = Vm();
        vm.OpenSetup.Execute(null);

        Assert.True(vm.SetupOpen);
        Assert.True(vm.SetupOnLogs);
        Assert.False(vm.SetupCanGoBack);
        vm.SetupBack.Execute(null);
        Assert.Equal(0, vm.SetupStep);

        for (var i = 0; i < MainViewModel.SetupStepKeys.Count + 2; i++)
        {
            vm.SetupNext.Execute(null);
        }

        Assert.True(vm.SetupOnDone);
        Assert.Equal(MainViewModel.SetupStepKeys.Count - 1, vm.SetupStep);
        Assert.True(vm.SetupSteps[^1].Current);
        Assert.Equal(Loc.T("Setup.Step.Done"), vm.SetupSteps[^1].Title);

        vm.SetupGo.Execute("3");
        Assert.True(vm.SetupOnLook);
        vm.SetupBack.Execute(null);
        Assert.True(vm.SetupOnLayout);
    }

    [Fact]
    public void A_marker_is_ticked_only_when_its_step_is_done_and_says_so()
    {
        var vm = Vm();
        var markers = vm.SetupSteps;
        vm.OpenSetup.Execute(null);
        vm.SetupNext.Execute(null);
        vm.SetupNext.Execute(null);
        vm.SetupNext.Execute(null);
        vm.SetupNext.Execute(null);

        // Passed but not done: no Hearthstone, so the logs are not on; the panels were not arranged. The look was seen.
        Assert.Same(markers, vm.SetupSteps);
        Assert.False(markers[0].Done);
        Assert.False(markers[2].Done);
        Assert.True(markers[3].Done);
        Assert.Equal(Loc.F("Setup.StepName", "1", "5", Loc.T("Setup.Step.Logs")), markers[0].AccessibleName);
        Assert.Equal(Loc.F("Setup.StepNameDone", "4", "5", Loc.T("Setup.Step.Look")), markers[3].AccessibleName);
        Assert.Equal(Loc.F("Setup.StepNameCurrent", "5", "5", Loc.T("Setup.Step.Done")), markers[4].AccessibleName);

        vm.SetupArranged();
        Assert.True(markers[2].Done);
    }

    [Fact]
    public void Esc_closes_an_open_wizard_like_skip_and_leaves_a_closed_one_alone()
    {
        var settings = new AppSettings { MigrationOffered = true };
        var vm = Vm(settings);
        Assert.False(vm.CloseSetupOnEscape());

        vm.OpenSetup.Execute(null);
        Assert.True(vm.CloseSetupOnEscape());

        Assert.False(vm.SetupOpen);
        Assert.True(settings.SetupDone);
        Assert.False(vm.CloseSetupOnEscape());
    }

    [Fact]
    public void Skipping_or_finishing_closes_it_for_good()
    {
        var settings = new AppSettings { MigrationOffered = true };
        var vm = Vm(settings);
        vm.OpenSetup.Execute(null);

        vm.CloseSetup.Execute(null);

        Assert.False(vm.SetupOpen);
        Assert.True(settings.SetupDone);
    }

    [Fact]
    public async Task It_opens_by_itself_once_for_a_player_with_no_games()
    {
        var vm = Vm();
        await vm.RefreshAsync();
        Assert.True(vm.SetupOpen);
        Assert.True(vm.SetupOnLogs);

        vm.CloseSetup.Execute(null);
        await vm.RefreshAsync();
        Assert.False(vm.SetupOpen);

        var done = Vm(new AppSettings { MigrationOffered = true, SetupDone = true });
        await done.RefreshAsync();
        Assert.False(done.SetupOpen);
    }

    [Fact]
    public void Without_Hearthstone_the_logs_step_says_so_and_offers_no_fix()
    {
        var vm = Vm();
        vm.OpenSetup.Execute(null);

        Assert.False(vm.SetupLogsOk);
        Assert.False(vm.SetupCanFixLogs);
        Assert.Equal(Loc.T("Setup.Logs.NotFound"), vm.SetupLogsStatus);
    }

    [Fact]
    public async Task Turning_the_logs_on_writes_log_config_and_ticks_the_step()
    {
        var vm = WithLogs(out var logConfig);
        vm.OpenSetup.Execute(null);
        Assert.False(vm.SetupLogsOk);
        Assert.Equal(Loc.T("Setup.Logs.Missing"), vm.SetupLogsStatus);
        Assert.True(vm.SetupCanFixLogs);

        await FixAsync(vm);

        Assert.True(vm.SetupLogsOk);
        Assert.False(vm.SetupCanFixLogs);
        Assert.Equal(Loc.T("Setup.Logs.Ready"), vm.SetupLogsStatus);
        Assert.Equal(Loc.T("Setup.Logs.Fixed"), vm.SetupLogsNote);
        Assert.Contains("[Power]", File.ReadAllText(logConfig), StringComparison.Ordinal);
        Assert.True(vm.SetupSteps[0].Done);
    }

    [Fact]
    public async Task With_Hearthstone_running_the_fix_asks_for_one_restart()
    {
        var vm = WithLogs(out _);
        vm.Apply(LiveSnapshot.Empty with { HearthstoneRunning = true });
        vm.OpenSetup.Execute(null);

        await FixAsync(vm);

        Assert.True(vm.SetupLogsOk);
        Assert.Equal(Loc.T("Setup.Logs.Restart"), vm.SetupLogsNote);
    }

    [Fact]
    public async Task A_log_config_that_cannot_be_written_says_so_and_stays_off()
    {
        var vm = WithLogs(out var logConfig);
        Directory.CreateDirectory(logConfig); // a folder where the file should be: writing it fails
        vm.OpenSetup.Execute(null);

        await FixAsync(vm);

        Assert.False(vm.SetupLogsOk);
        Assert.Equal(Loc.T("Setup.Logs.Failed"), vm.SetupLogsNote);
        Assert.True(vm.SetupCanFixLogs);
        Assert.False(vm.SetupSteps[0].Done);
    }

    [Fact]
    public void The_background_characters_switch_turns_the_page_hero_off()
    {
        var settings = new AppSettings { MigrationOffered = true };
        var vm = Vm(settings);
        Assert.NotNull(vm.PageHero);

        vm.SetupPageHeroes = false;

        Assert.False(settings.PageHeroes);
        Assert.Null(vm.PageHero);
    }
}
