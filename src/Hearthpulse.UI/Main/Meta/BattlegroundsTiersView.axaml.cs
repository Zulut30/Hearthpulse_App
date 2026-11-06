using Avalonia.Controls;

namespace Hearthpulse.UI.Main.Meta;

/// <summary>The Battlegrounds tier lists; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class BattlegroundsTiersView : UserControl
{
    public BattlegroundsTiersView() => InitializeComponent();

    /// <summary>"Heroes", "Comps", "Trinkets" or "Cards" (anything else: heroes).</summary>
    public void ShowTab(string tab)
    {
        HeroTierCard.IsVisible = tab is not ("Comps" or "Trinkets" or "Cards");
        CompTierCard.IsVisible = tab == "Comps";
        TrinketTierCard.IsVisible = tab == "Trinkets";
        CardStatsCard.IsVisible = tab == "Cards";
    }
}
