using System.ComponentModel;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Sortable tables (column heads over the deck page's matchups and My Stats' class tables): the sort itself, the
/// best and worst marks, and the view models re-sorting on a head click, in both compiled versions.
/// </summary>
public sealed class TableSortContractTests
{
    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    private static GameRecord Game(int number, string opponent, string result) => new()
    {
        Key = "table-sort-game-" + number,
        StartedAt = new DateTime(2026, 10, 24, 10, 0, 0).AddMinutes(number * 10),
        EndedAt = new DateTime(2026, 10, 24, 10, 6, 0).AddMinutes(number * 10),
        Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = result,
        PlayerClass = "MAGE", OpponentClass = opponent, DeckName = "Sorted Mage",
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sort_puts_empty_values_last_both_ways_and_a_second_click_turns_it_round(bool wpf)
    {
        var changes = 0;
        dynamic sort = wpf
            ? new Hearthpulse.App.Main.TableSort("Games", () => changes++, "Name")
            : new Hearthpulse.UI.Main.TableSort("Games", () => changes++, "Name");
        var rows = new (string Name, double? Rate)[] { ("b", 0.4), ("a", null), ("c", 0.6) };
        IComparable? Value((string Name, double? Rate) r, string column) => column == "Name" ? r.Name : r.Rate;
        string Order() => string.Concat(((IEnumerable<(string Name, double? Rate)>)sort.Apply(rows, (Func<(string Name, double? Rate), string, IComparable?>)Value))
            .Select(r => r.Name));

        Assert.True((bool)sort.Descending);
        sort.Toggle("Rate");
        Assert.Equal("cba", Order());
        sort.Toggle("Rate");
        Assert.False((bool)sort.Descending);
        Assert.Equal("bca", Order());
        // Names start from A.
        sort.Toggle("Name");
        Assert.Equal("Name", (string)sort.Column);
        Assert.Equal("abc", Order());
        sort.By.Execute("Name");
        Assert.Equal("cba", Order());
        Assert.Equal(4, changes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Best_and_worst_need_three_counted_rows_with_different_values(bool wpf)
    {
        int[] Marks(double?[] values, bool[] counts)
        {
            var rows = values.Select((v, i) => (Value: v, Counts: counts[i])).ToList();
            return wpf
                ? Hearthpulse.App.Main.TableSort.BestAndWorst(rows, r => r.Value, r => r.Counts)
                : Hearthpulse.UI.Main.TableSort.BestAndWorst(rows, r => r.Value, r => r.Counts);
        }

        Assert.Equal([1, 0, -1, 0], Marks([0.7, 0.5, 0.3, 0.9], [true, true, true, false]));
        Assert.Equal([0, 0, 0], Marks([0.7, null, 0.3], [true, true, true]));
        Assert.Equal([0, 0, 0], Marks([0.5, 0.5, 0.5], [true, true, true]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deck_matchups_sort_by_the_clicked_head_and_mark_the_best_and_worst_class(bool wpf)
    {
        // Warrior 5–0, Priest 3–2, Rogue 0–5, Druid 1 game (too few to mark).
        var games = new List<GameRecord>();
        void Add(string opponent, int wins, int losses)
        {
            for (var i = 0; i < wins + losses; i++)
            {
                games.Add(Game(games.Count, opponent, i < wins ? "Win" : "Loss"));
            }
        }

        Add("WARRIOR", 5, 0);
        Add("PRIEST", 3, 2);
        Add("ROGUE", 0, 5);
        Add("DRUID", 0, 1);
        var history = History();
        dynamic owner = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        dynamic model = wpf ? (object)new Hearthpulse.App.Main.Decks.DeckDetailsViewModel(owner)
            : new Hearthpulse.UI.Main.Decks.DeckDetailsViewModel(owner);
        var families = DeckFamilies.Build(DeckFamilies.Versions(games), []);
        await Changed((INotifyPropertyChanged)model, "Matchups", () => model.Show(families.Single(), families, Array.Empty<MergeSuggestion>(), null, CardDb.Empty));

        List<dynamic> Rows() => ((IEnumerable<object>)model.Matchups).Cast<dynamic>().ToList();
        Assert.Equal("Games", (string)model.MatchupSort.Column);
        Assert.Equal(1, (int)Rows().Single(r => (string)r.Key == "WARRIOR").WinMark);
        Assert.Equal(-1, (int)Rows().Single(r => (string)r.Key == "ROGUE").WinMark);
        Assert.Equal(0, (int)Rows().Single(r => (string)r.Key == "DRUID").WinMark);
        Assert.Equal("DRUID", (string)Rows()[^1].Key);

        // The same order as by games here: the list does not notify, the sort flag does on every rebuild.
        await Changed((INotifyPropertyChanged)model, "SortedByWinRate", () => model.MatchupSort.Toggle("WinRate"));
        Assert.Equal(new[] { "WARRIOR", "PRIEST", "ROGUE", "DRUID" }, Rows().Select(r => (string)r.Key));
        Assert.True((bool)model.SortedByWinRate);
        await Changed((INotifyPropertyChanged)model, "Matchups", () => model.MatchupSort.Toggle("WinRate"));
        // Ties keep the most played first in either direction.
        Assert.Equal(new[] { "ROGUE", "DRUID", "PRIEST", "WARRIOR" }, Rows().Select(r => (string)r.Key));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task My_stats_class_tables_sort_by_their_heads(bool wpf)
    {
        var history = History();
        var games = new[] { Game(0, "WARRIOR", "Win"), Game(1, "WARRIOR", "Win"), Game(2, "PRIEST", "Loss"), Game(3, "ROGUE", "Win") };
        foreach (var game in games)
        {
            Assert.True(history.Add(game));
        }

        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        await Changed((INotifyPropertyChanged)model, "CtOpponents", () => model.RefreshAsync());
        List<dynamic> Rows() => ((IEnumerable<object>)model.CtOpponents).Cast<dynamic>().ToList();
        Assert.Equal(2, (int)Rows()[0].GameCount);

        model.CtOpponentsSort.Toggle("Name");
        var names = Rows().Select(r => (string)r.Label).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
        model.CtOpponentsSort.Toggle("Name");
        Assert.Equal(names.AsEnumerable().Reverse(), Rows().Select(r => (string)r.Label));
    }

    private static async Task Changed(INotifyPropertyChanged model, string property, Action change)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChange(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == property)
            {
                completion.TrySetResult();
            }
        }

        model.PropertyChanged += OnChange;
        try
        {
            change();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            model.PropertyChanged -= OnChange;
        }
    }
}
