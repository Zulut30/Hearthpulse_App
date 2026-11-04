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
    /// A board of seven in a card as wide as at the minimum window (1040: the window less nav 250, page margins 80 and the
    /// list card's border and padding 34, about 676 px; the scroll bar overlays) shows one whole minion and "+6" naming
    /// the rest; at the default window (1260: about 896) five and "+2"; all seven from a card of 910 (a window of about
    /// 1275). The caption may wrap to two lines; both "+N" pills are named for screen readers.
    /// </summary>
    [Fact]
    public void History_battlegrounds_row_shows_whole_minions_and_plus_n() => Headless.Run(() =>
    {
        var theme = Theme();
        var view = new ReplayLibraryView();
        var card = new LibraryCard(ReplayShelf.Recent, "a.hpr", "key", battlegrounds: true) { Title = "Hero", Caption = "Уровень 5 · 14 ходов", When = "Today 12:49 · 27 min" };
        card.SetSummary(new[] { "A", "B", "C", "D", "E" }.Select(n => new LibraryChip("TB_BaconShop_HP_061", n)).ToList(),
            Enumerable.Range(1, 7).Select(i => new LibraryMinion("BG21_00" + i, "136/63", false, false, false, "Minion " + i)).ToList());
        var row = Assert.IsAssignableFrom<IDataTemplate>(view.Resources["LibraryCard"]).Build(card)!;
        row.DataContext = card;
        var window = new Window { Height = 200, Content = row };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var caption = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "BgCaption");
            Assert.Equal((Avalonia.Media.TextWrapping.Wrap, 2, Avalonia.Media.TextTrimming.CharacterEllipsis), (caption.TextWrapping, caption.MaxLines, caption.TextTrimming));
            var chipsMore = row.GetVisualDescendants().OfType<Border>().Single(b => Equals(ToolTip.GetTip(b), "D, E"));
            Assert.Equal("D, E", Avalonia.Automation.AutomationProperties.GetName(chipsMore));

            foreach (var (width, shown, more) in new[] { (676.0, 1, "+6"), (896.0, 5, "+2"), (914.0, 7, string.Empty) })
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
                    var rest = string.Join("\n", Enumerable.Range(shown + 1, 7 - shown).Select(i => "Minion " + i));
                    Assert.Equal(more, pill.GetVisualDescendants().OfType<TextBlock>().Single().Text);
                    Assert.Equal(rest, ToolTip.GetTip(pill));
                    Assert.Equal(rest, Avalonia.Automation.AutomationProperties.GetName(pill));
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
