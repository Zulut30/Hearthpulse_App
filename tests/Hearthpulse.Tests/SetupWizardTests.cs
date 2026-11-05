using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>The setup wizard (D4, MainViewModel.Setup.cs): its steps, when it opens by itself, and what it changes.</summary>
public sealed class SetupWizardTests
{
    private static MainViewModel Vm(AppSettings? settings = null) =>
        new(HistoryStore.InMemory(), settings ?? new AppSettings { MigrationOffered = true }, TestDataFolder.App);

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
        Assert.All(vm.SetupSteps.SkipLast(1), s => Assert.True(s.Passed && !s.Current));
        Assert.True(vm.SetupSteps[^1].Current);
        Assert.Equal(Loc.T("Setup.Step.Done"), vm.SetupSteps[^1].Title);

        vm.SetupGo.Execute("3");
        Assert.True(vm.SetupOnLook);
        vm.SetupBack.Execute(null);
        Assert.True(vm.SetupOnLayout);
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
