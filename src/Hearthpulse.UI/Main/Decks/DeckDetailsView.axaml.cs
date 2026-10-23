using Avalonia.Controls;

namespace Hearthpulse.UI.Main.Decks;

public partial class DeckDetailsView : UserControl
{
    public DeckDetailsView() => InitializeComponent();

    /// <summary>Shows the hover card of one chart point (the UI render check uses it; -1 hides it).</summary>
    public void HoverGame(int index) => WinRateChart.HoverIndex = index;
}
