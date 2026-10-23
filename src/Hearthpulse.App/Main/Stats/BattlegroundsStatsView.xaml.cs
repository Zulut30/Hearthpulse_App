using System.Windows.Controls;

namespace Hearthpulse.App.Main.Stats;

public partial class BattlegroundsStatsView : UserControl
{
    public BattlegroundsStatsView() => InitializeComponent();

    /// <summary>Shows the hover card of one chart point (the UI render check uses it; -1 hides it).</summary>
    public void HoverGame(int index) => RatingChart.HoverIndex = index;
}
