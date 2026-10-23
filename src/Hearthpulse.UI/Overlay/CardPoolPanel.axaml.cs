using Avalonia;
using Avalonia.Controls;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a play, a hovered card), often over the board, so it lets
/// clicks through to the game on every desktop, like the other click-through panels: the overlay host reads the pointer
/// and mouse buttons through its platform layer and passes them in (<see cref="OverlayView.UpdateCardPoolPointer"/>).
/// </summary>
public partial class CardPoolPanel : UserControl
{
    public CardPoolPanel()
    {
        InitializeComponent();
    }

    /// <summary>The pointer in <paramref name="view"/> coordinates (null outside the overlay) and the mouse buttons.</summary>
    public void UpdatePointer(Visual view, Point? pointer, (bool Left, bool Right) buttons)
    {
        if (DataContext is not OverlayViewModel vm)
        {
            return;
        }

        var over = pointer is { } p && Card.IsEffectivelyVisible && view.TranslatePoint(p, Card) is { } q
                   && q.X >= 0 && q.Y >= 0 && q.X <= Card.Bounds.Width && q.Y <= Card.Bounds.Height;
        vm.UpdateCardPoolPointer(over, buttons.Left, buttons.Right);
    }
}
