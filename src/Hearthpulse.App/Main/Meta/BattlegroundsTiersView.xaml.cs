using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Meta;

/// <summary>
/// The Battlegrounds tier tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>). A tab's card is built
/// from its template the first time the tab is opened, so a page opened on My stats builds none of them.
/// </summary>
public partial class BattlegroundsTiersView : UserControl
{
    private readonly Dictionary<string, FrameworkElement> _cards = new(StringComparer.Ordinal);

    public BattlegroundsTiersView() => InitializeComponent();

    /// <summary>The tabs whose cards exist ("Heroes", "Comps", "Trinkets", "Cards").</summary>
    public IReadOnlyCollection<string> BuiltTabs => _cards.Keys;

    /// <summary>
    /// "Heroes", "Comps", "Trinkets" or "Cards": shows that tab's card, building it on its first opening, and returns
    /// it. Any other tab (My stats) hides the cards, builds nothing and returns the view itself.
    /// </summary>
    public FrameworkElement ShowTab(string tab)
    {
        var known = tab is "Heroes" or "Comps" or "Trinkets" or "Cards";
        if (known && !_cards.ContainsKey(tab))
        {
            var card = (FrameworkElement)((DataTemplate)FindResource(tab + "Card")).LoadContent();
            _cards[tab] = card;
            Cards.Children.Add(card);
        }

        foreach (var (name, card) in _cards)
        {
            var shown = name == tab;
            card.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            // A list under a collapsed card is still laid out when its rows change (it was measured once): collapsed
            // itself, a hidden tab's list builds no rows on a refresh or a language change.
            if (card.FindName(ListName(name)) is ItemsControl list)
            {
                list.Visibility = card.Visibility;
            }
        }

        return known ? _cards[tab] : this;
    }

    private static string ListName(string tab) => tab switch
    {
        "Heroes" => "HeroTierList",
        "Comps" => "CompTierList",
        "Trinkets" => "TrinketTierList",
        _ => "CardTierList",
    };
}
