using System.ComponentModel;
using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The tables of the main window open with their essential columns; «Все столбцы» shows the rest and the views follow
/// the switch (TASK-2026-11-07-declutter). The Battlegrounds hero tier list has no switch any more: the site's service
/// publishes no games per hero, so nothing is left to hide (TASK-2026-11-09-light-ui-site-stats, design §2).
/// </summary>
public sealed class PageColumnsTests
{
    [Fact]
    public void Hero_tables_start_with_the_essential_columns_and_the_switch_tells_the_views()
    {
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-columns-{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App);
        Assert.False(model.BgHeroAllColumns);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)model).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.BgHeroAllColumns = true;
        Assert.True(model.BgHeroAllColumns);
        Assert.Contains(nameof(MainViewModel.BgHeroAllColumns), changed);
    }
}
