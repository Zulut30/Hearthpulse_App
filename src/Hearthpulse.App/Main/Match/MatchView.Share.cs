using System.Windows;
using Hearthpulse.App.Main.Share;

namespace Hearthpulse.App.Main.Match;

/// <summary>"Deck image" in the header (proposal C1): the share window of the game's deck picture.</summary>
public partial class MatchView
{
    private void OnDeckImage(object sender, RoutedEventArgs e)
    {
        if (_model?.DeckShare() is { } share)
        {
            ShareWindow.Open(Window.GetWindow(this), share);
        }
    }
}
