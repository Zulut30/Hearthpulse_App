using System.Windows;
using Hearthpulse.App.Main.Share;

namespace Hearthpulse.App.Main.Decks;

/// <summary>"Deck image" on the list card (proposal C1): the share window of the deck's picture.</summary>
public partial class DeckDetailsView
{
    private void OnDeckImage(object sender, RoutedEventArgs e)
    {
        if (DataContext is DeckDetailsViewModel { } page && page.DeckShare() is { } share)
        {
            ShareWindow.Open(Window.GetWindow(this), share);
        }
    }
}
