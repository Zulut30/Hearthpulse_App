using Avalonia.Controls;

namespace Hearthpulse.UI.Main.Pages;

/// <summary>
/// The battlegrounds: community tiers (heroes, comps, trinkets, cards) and the player's own stats, with the bracket picker (its data: <see cref="MainViewModel"/>). The view keeps the state that
/// outlives it (the tab shown) and reaches the named parts it switches through this control.
/// </summary>
public partial class BattlegroundsPage : UserControl
{
    public BattlegroundsPage() => InitializeComponent();
}
