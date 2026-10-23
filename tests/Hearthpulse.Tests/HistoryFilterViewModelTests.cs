using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// "Show in History" from a deck page switches the filter to All and then selects the game. The view waits for
/// <see cref="MainViewModel.ShowAllGamesAsync"/>: it must end only once the All rows are there, so the game can be
/// selected (the WPF and Avalonia view models are the same code).
/// </summary>
public sealed class HistoryFilterViewModelTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-history-vm-{Guid.NewGuid():N}.db");
    private readonly HistoryStore _history;

    public HistoryFilterViewModelTests()
    {
        _history = new HistoryStore(_db);
        var start = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        foreach (var (key, mode, placement) in new[] { ("ranked-1", "Ranked Standard", 0), ("bg-1", "Battlegrounds", 3), ("ranked-2", "Ranked Wild", 0) })
        {
            start = start.AddMinutes(30);
            _history.Add(new GameRecord
            {
                Key = key,
                StartedAt = start,
                EndedAt = start.AddMinutes(10),
                Mode = mode,
                PlayerName = "Me",
                HeroCardId = "HERO_01",
                Result = placement > 0 ? string.Empty : "Win",
                Placement = placement,
            });
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    [Fact]
    public async Task With_the_games_in_memory_all_rows_are_there_when_the_task_ends()
    {
        var vm = new MainViewModel(_history, new AppSettings());
        await vm.RefreshAsync();
        vm.SetHistoryFilter.Execute("Battlegrounds");
        Assert.Equal(["bg-1"], vm.HistoryRows.Select(r => r.Key));

        var shown = vm.ShowAllGamesAsync();

        // Applied from memory: no reload to wait for, so the view selects the game at once.
        Assert.True(shown.IsCompleted);
        await shown;
        Assert.Equal("All", vm.HistoryFilter);
        Assert.Equal(["ranked-2", "bg-1", "ranked-1"], vm.HistoryRows.Select(r => r.Key));
        Assert.Equal("ranked-1", vm.HistoryRowFor("ranked-1")?.Key);
    }

    [Fact]
    public async Task Before_the_first_refresh_the_task_ends_after_the_reload()
    {
        var vm = new MainViewModel(_history, new AppSettings());
        Assert.Empty(vm.HistoryRows);

        await vm.ShowAllGamesAsync();

        Assert.Equal("All", vm.HistoryFilter);
        Assert.Equal(["ranked-2", "bg-1", "ranked-1"], vm.HistoryRows.Select(r => r.Key));
    }

    [Fact]
    public async Task A_filter_change_uses_the_games_already_read()
    {
        var vm = new MainViewModel(_history, new AppSettings());
        await vm.RefreshAsync();
        var changes = 0;
        vm.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(MainViewModel.HistoryRows) ? 1 : 0;

        vm.SetHistoryFilter.Execute("Ranked");

        // The rows change synchronously, before the command returns.
        Assert.Equal(1, changes);
        Assert.Equal(["ranked-2", "ranked-1"], vm.HistoryRows.Select(r => r.Key));
    }
}
