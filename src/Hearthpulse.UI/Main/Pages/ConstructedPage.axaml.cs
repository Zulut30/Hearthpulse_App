using Avalonia.Controls;

namespace Hearthpulse.UI.Main.Pages;

/// <summary>
/// The standard and Wild: the player's decks and stats, the meta's tiers, decks and matchups (its data: <see cref="MainViewModel"/>). The view keeps the state that
/// outlives it (the tab shown) and reaches the named parts it switches through this control.
/// </summary>
public partial class ConstructedPage : UserControl
{
    public ConstructedPage() => InitializeComponent();
}
