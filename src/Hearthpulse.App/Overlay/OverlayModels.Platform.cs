using System.Windows;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// One mark under a card of the opponent's hand. <see cref="Offset"/> places it inside the opponent-hand panel;
/// <see cref="PortraitId"/> is the known card (or the Coin), else the card that created it.
/// </summary>
public sealed record HandMark(Thickness Offset, string Label, string? PortraitId, bool Known, bool Created, bool Coin, string? PreviewCardId, string Hint);

/// <summary>A tribe chip in the game plan: build around it (Key is the race key; empty for any tribe).</summary>
public sealed record FocusChoice(string Key, string Name, bool Selected);
