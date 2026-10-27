using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Meta;

/// <summary>The Standard/Wild community tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>).</summary>
public partial class ConstructedMetaView : UserControl
{
    public ConstructedMetaView() => InitializeComponent();

    /// <summary>"Tiers", "Top" or "Matchups"; anything else hides the view (the "My stats" tab).</summary>
    public FrameworkElement? ShowTab(string? tab)
    {
        MetaTiersPanel.Visibility = tab == "Tiers" ? Visibility.Visible : Visibility.Collapsed;
        MetaDecksPanel.Visibility = tab == "Top" ? Visibility.Visible : Visibility.Collapsed;
        MetaMatchupsPanel.Visibility = tab == "Matchups" ? Visibility.Visible : Visibility.Collapsed;
        Visibility = tab is "Tiers" or "Top" or "Matchups" ? Visibility.Visible : Visibility.Collapsed;
        return tab switch
        {
            "Tiers" => MetaTiersPanel,
            "Top" => MetaDecksPanel,
            "Matchups" => MetaMatchupsPanel,
            _ => null,
        };
    }
}
