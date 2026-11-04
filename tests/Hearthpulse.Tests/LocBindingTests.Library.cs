using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core.Replay;
using Hearthpulse.UI.Main.Match;

namespace Hearthpulse.Tests;

/// <summary>The Avalonia History row: the Battlegrounds board fits its room with whole minions and "+N".</summary>
public sealed partial class LocBindingTests
{
    /// <summary>
    /// A board of seven in a row as wide as the minimum window's (1040: about 678 px) shows two whole minions and "+5"
    /// naming the rest; as wide as the default window's (1260: about 898 px) all seven and no "+N".
    /// </summary>
    [Fact]
    public void History_battlegrounds_row_shows_whole_minions_and_plus_n() => Headless.Run(() =>
    {
        var theme = Theme();
        var view = new ReplayLibraryView();
        var card = new LibraryCard(ReplayShelf.Recent, "a.hpr", "key", battlegrounds: true) { Title = "Hero", Caption = "Tier 5 · 14 turns", When = "Today 12:49 · 27 min" };
        card.SetSummary([new LibraryChip("TB_BaconShop_HP_061", "Power")],
            Enumerable.Range(1, 7).Select(i => new LibraryMinion("BG21_00" + i, "136/63", false, false, false, "Minion " + i)).ToList());
        var row = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["LibraryCard"]).Build(card)!;
        row.DataContext = card;
        var window = new Window { Height = 200, Content = row };
        try
        {
            window.Show();
            foreach (var (width, shown, more) in new[] { (702.0, 2, "+5"), (922.0, 7, string.Empty) })
            {
                window.Width = width;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                var minions = row.GetVisualDescendants().OfType<StackPanel>().Where(p => p.DataContext is LibraryMinion && p.IsEffectivelyVisible).ToList();
                Assert.Equal(shown, minions.Count);
                var pill = row.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.DataContext is LibraryBoardView && b.Classes.Contains("pill"));
                Assert.NotNull(pill);
                Assert.Equal(more.Length > 0, pill.IsEffectivelyVisible);
                if (more.Length > 0)
                {
                    Assert.Equal(more, pill.GetVisualDescendants().OfType<TextBlock>().Single().Text);
                    Assert.Equal("Minion 3\nMinion 4\nMinion 5\nMinion 6\nMinion 7", ToolTip.GetTip(pill));
                }

                // Whole minions only: the last one ends inside the board's room.
                var room = row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "BoardRoom");
                var last = minions[^1];
                var end = last.TranslatePoint(new Point(last.Bounds.Width, 0), room)!.Value.X;
                Assert.True(end <= room.Bounds.Width + 0.5, $"{width}: the last minion ends at {end}, the room is {room.Bounds.Width}");
            }
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });
}
