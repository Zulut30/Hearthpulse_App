using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// The panel layout the player keeps in <see cref="AppSettings"/>: where each panel sits, its size and opacity, the
/// overlay's own scale and opacity, and how long the end-of-game toast and the move tip stay up. The overlay view applies
/// these to its panels and saves the settings when it raises its layout event.
/// </summary>
public sealed partial class OverlayViewModel
{
    /// <summary>How long the end-of-game toast and the one-time move tip stay up.</summary>
    public static readonly TimeSpan ToastTime = TimeSpan.FromSeconds(9);

    /// <summary>The overlay's scale (Settings: overlay size), kept to 0.6-1.6 whatever the settings file says.</summary>
    public static double SurfaceScale(AppSettings settings) => Math.Clamp(settings.OverlayScale, 0.6, 1.6);

    /// <summary>
    /// The opacity of the panels' backgrounds (Settings: panel background opacity), kept to 0.3-1 so a panel never
    /// vanishes. Text, numbers and icons stay opaque (R06): only <see cref="BackdropBrushes"/> fade.
    /// </summary>
    public static double SurfaceOpacity(AppSettings settings) => Math.Clamp(settings.OverlayOpacity, 0.3, 1);

    /// <summary>
    /// The theme brushes behind the overlay's text: panel and card fills and badge shades. The overlay puts faded copies
    /// of them in its own resources, which the overlay's markup takes as dynamic resources.
    /// </summary>
    public static IReadOnlyList<string> BackdropBrushes { get; } = ["OverlayCardFill", "OverlayPanelSolid", "OverlayPanel", "OverlayBadgeShade"];

    /// <summary>
    /// Whether this change just showed the toast or the move tip, so the view starts their <see cref="ToastTime"/>
    /// over (a second toast before the first one hid gets its full time).
    /// </summary>
    public bool ToastJustShown(string? propertyName) =>
        (propertyName == nameof(ShowToast) && ShowToast) || (propertyName == nameof(ShowMoveTip) && ShowMoveTip);

    /// <summary>The toast and the move tip had their time.</summary>
    public void HideToasts()
    {
        ShowToast = false;
        ShowMoveTip = false;
    }

    /// <summary>A panel was moved or resized (null: put back where it starts), as the player left it.</summary>
    public static void SavePlacement(AppSettings settings, string panelId, PanelPlacement? placement)
    {
        if (placement is null)
        {
            settings.Panels.Remove(panelId);
        }
        else
        {
            settings.Panels[panelId] = placement;
        }

        Diag.Debug(PanelArming.Placed(panelId, placement));
    }

    /// <summary>A panel's own size and opacity (null: back to the overlay's).</summary>
    public static void SaveLook(AppSettings settings, string panelId, PanelLook? look)
    {
        if (look is null)
        {
            settings.PanelLooks.Remove(panelId);
        }
        else
        {
            settings.PanelLooks[panelId] = look;
        }
    }

    /// <summary>Every panel back where it starts, with the overlay's size and opacity.</summary>
    public static void ClearLayout(AppSettings settings)
    {
        settings.Panels.Clear();
        settings.PanelLooks.Clear();
    }
}
