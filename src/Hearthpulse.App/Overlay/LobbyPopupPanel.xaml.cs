using System.Windows.Controls;

namespace Hearthpulse.App.Overlay;

/// <summary>The overlay's "lobby-popup" panel: the opponent under the mouse on the leaderboard (bound to <see cref="OverlayViewModel.Lobby"/>).</summary>
public partial class LobbyPopupPanel : UserControl
{
    public LobbyPopupPanel() => InitializeComponent();
}
