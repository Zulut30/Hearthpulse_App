using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Unlocked panels: during a game every panel off the game's cards moves and resizes with the mouse, without Ctrl,
/// until the player locks them again. Kept in <see cref="AppSettings.OverlayPanelsUnlocked"/>.
/// </summary>
public sealed partial class OverlayViewModel
{
    private bool _panelsUnlocked;

    /// <summary>Panels are unlocked: each shows its frame, and the one under the pointer moves like with Ctrl held.</summary>
    public bool PanelsUnlocked { get => _panelsUnlocked; private set => Set(ref _panelsUnlocked, value); }

    private void UpdatePanelLock(AppSettings settings) => PanelsUnlocked = settings.OverlayPanelsUnlocked;

    /// <summary>The lock button on a panel: flips the setting (the host saves it) and the panels follow at once.</summary>
    public void TogglePanelLock(AppSettings settings)
    {
        settings.OverlayPanelsUnlocked = !settings.OverlayPanelsUnlocked;
        PanelsUnlocked = settings.OverlayPanelsUnlocked;
        Diag.Info(PanelsUnlocked ? "Overlay panels unlocked from the overlay" : "Overlay panels locked from the overlay");
    }
}
