using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Pages;

/// <summary>
/// The standard and Wild: the player's decks and stats, the meta's tiers, decks and matchups (its data: <see cref="MainViewModel"/>). The window keeps the state that
/// outlives it (the tab shown) and reaches the named parts it switches through this control.
/// </summary>
public partial class ConstructedPage : UserControl
{
    public ConstructedPage() => InitializeComponent();

    /// <summary>
    /// A tab button was checked or unchecked: the window shows that tab's view (true: checked, so it is also scrolled into
    /// sight).
    /// </summary>
    public event Action<bool>? TabChanged;

    private void OnTab(object sender, RoutedEventArgs e) => TabChanged?.Invoke(sender is RadioButton { IsChecked: true });
}
