using System.Windows.Controls;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// Badges beside the Battlegrounds leaderboard tiles (DataContext: the overlay view model): each opponent's tavern tier
/// and main tribe. Placed by the leaderboard calibration and never hit-tested, so clicks go to the game.
/// </summary>
public partial class BgBadgesLayer : UserControl
{
    public BgBadgesLayer()
    {
        InitializeComponent();
    }
}
