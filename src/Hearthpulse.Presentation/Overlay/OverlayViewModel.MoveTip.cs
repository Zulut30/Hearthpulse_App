using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>The one-time toast that tells the player panels move and resize with Ctrl held during a game.</summary>
public sealed partial class OverlayViewModel
{
    /// <summary>
    /// Shows the move tip in the toast the first time the overlay appears over the game, and marks it shown in
    /// <paramref name="settings"/>. Returns true when it showed now, so the host saves the settings.
    /// </summary>
    public bool OfferMoveTip(AppSettings settings)
    {
        if (settings.OverlayMoveTipShown || LayoutEditing)
        {
            return false;
        }

        settings.OverlayMoveTipShown = true;
        // A language switch rebuilds the toast from the last game's outcome: not over the tip.
        _lastOutcome = null;
        ToastTitle = Loc.T("Overlay.Move.TipTitle");
        ToastDetail = Loc.T("Overlay.Move.Tip");
        ToastResult = string.Empty;
        ShowToast = true;
        return true;
    }
}
