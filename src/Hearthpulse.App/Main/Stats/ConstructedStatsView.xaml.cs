using System.Windows.Controls;

namespace Hearthpulse.App.Main.Stats;

public partial class ConstructedStatsView : UserControl
{
    public ConstructedStatsView() => InitializeComponent();

    /// <summary>Shows the hover card of one chart point (the UI render check uses it; -1 hides it).</summary>
    public void HoverGame(int index) => RankChart.HoverIndex = index;
}
