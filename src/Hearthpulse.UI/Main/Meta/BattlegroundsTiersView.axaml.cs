using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Hearthpulse.UI.Main.Meta;

/// <summary>
/// The Battlegrounds tier tabs; the page's tab bar picks which one shows (<see cref="ShowTab"/>). A tab's card is built
/// from its template the first time the tab is opened, so a page opened on My stats builds none of them.
/// </summary>
public partial class BattlegroundsTiersView : UserControl
{
    private readonly Dictionary<string, Control> _cards = new(StringComparer.Ordinal);

    public BattlegroundsTiersView() => InitializeComponent();

    /// <summary>The tabs whose cards exist ("Heroes", "Comps", "Trinkets", "Cards").</summary>
    public IReadOnlyCollection<string> BuiltTabs => _cards.Keys;

    /// <summary>
    /// "Heroes", "Comps", "Trinkets" or "Cards": shows that tab's card, building it on its first opening. Any other tab
    /// (My stats) hides the cards and builds nothing.
    /// </summary>
    public void ShowTab(string tab)
    {
        if (tab is "Heroes" or "Comps" or "Trinkets" or "Cards" && !_cards.ContainsKey(tab)
            && this.FindResource(tab + "Card") is IDataTemplate template && template.Build(null) is { } built)
        {
            _cards[tab] = built;
            Cards.Children.Add(built);
        }

        foreach (var (name, card) in _cards)
        {
            card.IsVisible = name == tab;
        }
    }
}
