using System.ComponentModel;
using Hearthpulse.Core;
using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Main;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// The rewards editor on My Stats → Arena through the page's view model, over a history of two made-up runs
/// (<see cref="ArenaRunsScene"/>): it survives a rebuild of the list, stays open when a save fails, closes when its run
/// leaves the period, and gives the focus back to the run's chip once.
/// </summary>
public sealed class ArenaRewardsPageTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-arena-page-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _history;

    public ArenaRewardsPageTests()
    {
        _history = new HistoryStore(_db);
        foreach (var game in ArenaRunsScene.Games())
        {
            _history.Add(game);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_db);
        }
        catch (IOException)
        {
            // My Stats may still be reading in the background; the temp file goes with the temp folder.
        }
    }

    [Fact]
    public async Task The_editor_survives_a_rebuild_keeps_a_failed_save_and_closes_with_its_run()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        await Runs(vm, () => vm.RefreshAsync(), rows => rows.Count == 2);
        // Four numbers at the top of the page; time played and the rewards wait under «Подробнее».
        Assert.Equal(MainViewModel.HeadlineTiles, vm.ArenaStatTiles.Count);
        Assert.Equal(2, vm.ArenaMoreTiles.Count);

        vm.EditArenaRewards.Execute(ArenaRunsScene.FinishedKey);
        var edit = Row(vm).Edit!;
        Assert.True(Row(vm).Editing);
        Assert.True(edit.TakeFocus());
        Assert.False(edit.TakeFocus());

        // A rebuild (the period chosen again) keeps the same editor with what was typed.
        edit.Gold = "75";
        await Runs(vm, () => vm.SetArenaPeriod.Execute("All"), rows => rows.Count == 2);
        Assert.Same(edit, Row(vm).Edit);
        Assert.Equal("75", Row(vm).Edit!.Gold);

        // A save the history refuses keeps the editor open with its error.
        vm.SaveArenaRewards(edit, new ArenaRewards(-1, 0, 0, 0, 0));
        Assert.Same(edit, Row(vm).Edit);
        Assert.NotEmpty(edit.Error);

        // The run leaves the period (the made-up runs are from October 1): the editor closes unsaved.
        await Runs(vm, () => vm.SetArenaPeriod.Execute("Week"), rows => rows.Count == 0);
        await Runs(vm, () => vm.SetArenaPeriod.Execute("All"), rows => rows.Count == 2);
        Assert.Null(Row(vm).Edit);
        Assert.Empty(_history.ArenaRewards());
    }

    [Fact]
    public async Task A_save_shows_the_rewards_and_gives_the_focus_back_to_the_chip()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        await Runs(vm, () => vm.RefreshAsync(), rows => rows.Count == 2);

        vm.EditArenaRewards.Execute(ArenaRunsScene.FinishedKey);
        var edit = Row(vm).Edit!;
        edit.Gold = "150";
        edit.Dust = "40";
        await Runs(vm, () => edit.Save.Execute(null), rows => rows.Any(r => r.Key == ArenaRunsScene.FinishedKey && r.HasRewards));

        Assert.Equal(new ArenaRewards(150, 40, 0, 0, 0), _history.ArenaRewards()[ArenaRunsScene.FinishedKey]);
        Assert.Null(Row(vm).Edit);
        Assert.True(vm.TakeArenaRewardsFocus(ArenaRunsScene.FinishedKey));
        Assert.False(vm.TakeArenaRewardsFocus(ArenaRunsScene.FinishedKey));
        Assert.Equal("150", vm.ArenaMoreTiles[^1].Value);

        // Cancel gives the focus back at once.
        vm.EditArenaRewards.Execute(ArenaRunsScene.FinishedKey);
        Row(vm).Edit!.Cancel.Execute(null);
        Assert.True(vm.TakeArenaRewardsFocus(ArenaRunsScene.FinishedKey));
    }

    private static ArenaRunRow Row(MainViewModel vm) => vm.ArenaRuns.Single(r => r.Key == ArenaRunsScene.FinishedKey);

    /// <summary>Runs <paramref name="act"/> and waits until the run list it makes (built in the background) matches.</summary>
    private static async Task Runs(MainViewModel vm, Func<Task> act, Func<IReadOnlyList<ArenaRunRow>, bool> done)
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ArenaRuns) && done(vm.ArenaRuns))
            {
                ready.TrySetResult();
            }
        }

        vm.PropertyChanged += Changed;
        try
        {
            // Every act here rebuilds the list in the background: wait for a list that matches, after the act.
            await act();
            await ready.Task.WaitAsync(Wait);
        }
        finally
        {
            vm.PropertyChanged -= Changed;
        }
    }

    private static Task Runs(MainViewModel vm, Action act, Func<IReadOnlyList<ArenaRunRow>, bool> done) =>
        Runs(vm, () =>
        {
            act();
            return Task.CompletedTask;
        }, done);
}
