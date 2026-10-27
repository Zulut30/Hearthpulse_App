namespace Hearthpulse.UI.Main;

public sealed partial class MainViewModel
{
    /// <summary>
    /// Asks the view to copy a deck code (Avalonia's clipboard belongs to the top level); it reports back through
    /// <see cref="DeckCodeCopied"/>.
    /// </summary>
    public event Action<MetaDeckRow>? DeckCodeCopyRequested;

    partial void CopyDeckCode(MetaDeckRow deck) => DeckCodeCopyRequested?.Invoke(deck);
}
