using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Meta;

/// <summary>The Battlegrounds tier lists; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class BattlegroundsTiersView : UserControl
{
    public BattlegroundsTiersView() => InitializeComponent();

    /// <summary>"Heroes", "Comps", "Trinkets", "Cards" or "Mine"; returns the card now shown.</summary>
    public FrameworkElement ShowTab(string tab)
    {
        HeroTierCard.Visibility = tab == "Heroes" ? Visibility.Visible : Visibility.Collapsed;
        CompTierCard.Visibility = tab == "Comps" ? Visibility.Visible : Visibility.Collapsed;
        TrinketTierCard.Visibility = tab == "Trinkets" ? Visibility.Visible : Visibility.Collapsed;
        CardStatsCard.Visibility = tab == "Cards" ? Visibility.Visible : Visibility.Collapsed;
        MyHeroesCard.Visibility = tab == "Mine" ? Visibility.Visible : Visibility.Collapsed;
        return tab switch
        {
            "Comps" => CompTierCard,
            "Trinkets" => TrinketTierCard,
            "Cards" => CardStatsCard,
            "Mine" => MyHeroesCard,
            _ => HeroTierCard,
        };
    }
}
