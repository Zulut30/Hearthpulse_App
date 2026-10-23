namespace Hearthpulse.App.Overlay;

/// <summary>Deck panel extras: where the record under the deck name comes from.</summary>
public sealed partial class OverlayViewModel
{
    private string _deckRecordTip = string.Empty;

    /// <summary>Hover text for the record line: whose games it counts, or which community stats it quotes.</summary>
    public string DeckRecordTip { get => _deckRecordTip; private set => Set(ref _deckRecordTip, value); }
}
