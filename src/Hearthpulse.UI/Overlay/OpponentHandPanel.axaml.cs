using Avalonia.Controls;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// Content of the "opponent-hand-fan" overlay panel: one mark on each card of the opponent's hand fan. Renamed from
/// "opponent-hand" when the marks moved onto the cards, so a placement made for the old row does not shift them.
/// </summary>
public partial class OpponentHandPanel : UserControl
{
    public OpponentHandPanel()
    {
        InitializeComponent();
    }
}
