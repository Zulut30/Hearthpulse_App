using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>The one-time tip that tells the player panels move and resize with Ctrl held during a game.</summary>
public sealed partial class OverlayViewModel
{
    private bool _showMoveTip;

    /// <summary>
    /// The move tip shows at the top centre for a few seconds (the host hides it), not in the end-of-game toast above
    /// the hand: the app may start in the middle of a match.
    /// </summary>
    public bool ShowMoveTip { get => _showMoveTip; set => Set(ref _showMoveTip, value); }

    /// <summary>
    /// Shows the move tip the first time the overlay appears over the game, and marks it shown in
    /// <paramref name="settings"/>. Returns true when it showed now, so the host saves the settings.
    /// </summary>
    public bool OfferMoveTip(AppSettings settings)
    {
        if (settings.OverlayMoveTipShown || LayoutEditing)
        {
            return false;
        }

        settings.OverlayMoveTipShown = true;
        ShowMoveTip = true;
        return true;
    }
}
