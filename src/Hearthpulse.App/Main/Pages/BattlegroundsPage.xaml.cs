using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Pages;

/// <summary>
/// The battlegrounds: community tiers (heroes, comps, trinkets, cards) and the player's own stats, with the bracket picker (its data: <see cref="MainViewModel"/>). The window keeps the state that
/// outlives it (the tab shown) and reaches the named parts it switches through this control.
/// </summary>
public partial class BattlegroundsPage : UserControl
{
    public BattlegroundsPage() => InitializeComponent();

    /// <summary>
    /// A tab button was checked or unchecked: the window shows that tab's view (true: checked, so it is also scrolled into
    /// sight).
    /// </summary>
    public event Action<bool>? TabChanged;

    private void OnTab(object sender, RoutedEventArgs e) => TabChanged?.Invoke(sender is RadioButton { IsChecked: true });
}
