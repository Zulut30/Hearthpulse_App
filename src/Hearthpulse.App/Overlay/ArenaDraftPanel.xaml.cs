using System.Windows.Controls;

namespace Hearthpulse.App.Overlay;

/// <summary>The Arena draft panel: deck so far, curve, average score, class win rates (bound to <see cref="OverlayViewModel"/>).</summary>
public partial class ArenaDraftPanel : UserControl
{
    public ArenaDraftPanel() => InitializeComponent();
}
