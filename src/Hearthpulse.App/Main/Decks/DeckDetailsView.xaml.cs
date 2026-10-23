using System.Windows.Controls;

namespace Hearthpulse.App.Main.Decks;

public partial class DeckDetailsView : UserControl
{
    public DeckDetailsView()
    {
        InitializeComponent();
    }

    /// <summary>The win-rate chart, so the UI render check can show a hover card.</summary>
    public Controls.StatsLineChart Chart => WinRateChart;
}
