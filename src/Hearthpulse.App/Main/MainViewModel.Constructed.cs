using System.Runtime.InteropServices;
using System.Windows;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    partial void CopyDeckCode(MetaDeckRow deck)
    {
        try
        {
            Clipboard.SetText(deck.Code);
            DeckCodeCopied(deck.Name, true);
        }
        catch (COMException)
        {
            DeckCodeCopied(deck.Name, false);
        }
    }
}
