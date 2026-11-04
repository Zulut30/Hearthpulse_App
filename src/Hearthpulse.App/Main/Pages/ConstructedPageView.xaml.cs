using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Pages;

/// <summary>
/// The standard and Wild: the player's decks and stats, the meta's tiers, decks and matchups (its data: <see cref="MainViewModel"/>). The window keeps the state that
/// outlives it (the tab shown) and reaches the named parts it switches through this control.
/// </summary>
public partial class ConstructedPageView : UserControl
{
    public ConstructedPageView() => InitializeComponent();

    /// <summary>
    /// A tab button was checked (only <c>Checked</c> is subscribed): the window shows that tab's view. The flag is the
    /// button's state, true unless it changed again before the handler ran.
    /// </summary>
    public event Action<bool>? TabChanged;

    private void OnTab(object sender, RoutedEventArgs e) => TabChanged?.Invoke(sender is RadioButton { IsChecked: true });
}
