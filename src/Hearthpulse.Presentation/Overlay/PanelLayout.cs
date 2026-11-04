using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Where a movable panel goes and how big it is, the same in both overlays' <c>MovablePanel</c>: the spot a saved
/// placement gives, a panel kept on the screen, a drag that snaps and stays on it, the wheel's size step and the
/// placement saved from where the panel is. Sizes and spots are in surface units; the panels hand in what their UI
/// measured (a panel's size on screen, with its scale).
/// </summary>
public static class PanelLayout
{
    /// <summary>One wheel notch or size button changes the panel's size by this much.</summary>
    public const double ScaleStep = 0.05;

    /// <summary>The size one wheel notch or size button gives, in whole percents between the size limits.</summary>
    public static double StepScale(double scale, bool up) =>
        Math.Clamp(Math.Round(scale + (up ? ScaleStep : -ScaleStep), 2), PanelSnap.MinScale, PanelSnap.MaxScale);

    /// <summary>The size a saved placement gives, within the size limits whatever the settings file says.</summary>
    public static double PlacedScale(PanelPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return Math.Clamp(placement.Scale, PanelSnap.MinScale, PanelSnap.MaxScale);
    }

    /// <summary>The top-left corner a saved placement (fractions of the surface) gives on a surface of this size.</summary>
    public static (double X, double Y) PlacedSpot(PanelPlacement placement, double surfaceWidth, double surfaceHeight)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return (placement.X * surfaceWidth, placement.Y * surfaceHeight);
    }

    /// <summary>
    /// What a panel's default margin is divided by: a panel anchored to the game's own screen (the mulligan strip under
    /// the offered cards) keeps its spot at any overlay scale, so its margin undoes that scale; others keep theirs.
    /// </summary>
    public static double DefaultMarginDivisor(bool gameAnchored, double overlayScale) => gameAnchored && overlayScale > 0 ? overlayScale : 1;

    /// <summary>
    /// The saved spot pulled back inside the surface for a panel of <paramref name="width"/> x <paramref name="height"/>
    /// on screen (its content grew past an edge, or the surface got smaller).
    /// </summary>
    public static (double Left, double Top) KeepInside(double x, double y, double width, double height, double surfaceWidth, double surfaceHeight) =>
        (Math.Clamp(x, 0, Math.Max(0, surfaceWidth - width)), Math.Clamp(y, 0, Math.Max(0, surfaceHeight - height)));

    /// <summary>
    /// Where a dragged panel goes: its spot when the drag began moved by the pointer (<paramref name="dx"/>,
    /// <paramref name="dy"/>), pulled to the screen edges, the centre line and the other panels' edges
    /// (<see cref="PanelSnap.Snap(double, double, double, ReadOnlySpan{ValueTuple{double, double}})"/>), and kept on the
    /// surface.
    /// </summary>
    public static (double Left, double Top) DragSpot(
        double startX, double startY, double dx, double dy, double width, double height, double surfaceWidth, double surfaceHeight,
        ReadOnlySpan<(double Start, double End)> across, ReadOnlySpan<(double Start, double End)> down) =>
        (Math.Clamp(PanelSnap.Snap(startX + dx, width, surfaceWidth, across), 0, Math.Max(0, surfaceWidth - width)),
         Math.Clamp(PanelSnap.Snap(startY + dy, height, surfaceHeight, down), 0, Math.Max(0, surfaceHeight - height)));

    /// <summary>The placement saved for a panel whose top-left corner is at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public static PanelPlacement Placement(double x, double y, double surfaceWidth, double surfaceHeight, double scale) => new()
    {
        X = surfaceWidth > 0 ? x / surfaceWidth : 0,
        Y = surfaceHeight > 0 ? y / surfaceHeight : 0,
        Scale = scale,
    };

    /// <summary>The opacity a panel's look gives (null: opaque), never fainter than <see cref="PanelLook.MinOpacity"/>.</summary>
    public static double LookOpacity(PanelLook? look) => Math.Clamp(look?.Opacity ?? 1, PanelLook.MinOpacity, 1);

    /// <summary>
    /// Whether another panel's edges pull a dragged one: it shows with a size, as a panel or as a layout-mode placeholder
    /// of its full size. A wide row's placeholder is only a label at one end of the row: its bounds are not where it shows.
    /// </summary>
    public static bool PullsDrags(bool visible, double width, bool shown, bool editing, bool stretchedPlaceholder) =>
        visible && width > 0 && (shown || (editing && stretchedPlaceholder));
}
