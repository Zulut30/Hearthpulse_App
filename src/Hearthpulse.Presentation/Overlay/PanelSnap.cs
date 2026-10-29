using System.Globalization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Layout-mode helpers both <c>MovablePanel</c>s share: a dragged panel snaps to the screen edges and centre line and
/// to the edges of the other panels, and its name tag shows the size and opacity the player gave it.
/// </summary>
public static class PanelSnap
{
    /// <summary>How close (surface units) a panel edge must come to a guide to snap to it.</summary>
    public const double Distance = 10;

    /// <summary>Gap the default panel spots keep from the screen edge; a snapped panel lines up with them.</summary>
    public const double Gutter = 12;

    /// <summary>Gap a panel snapped beside another keeps from it, as between the default spots.</summary>
    public const double Spacing = 8;

    /// <summary>The smallest and largest size the player can give a panel (wheel, buttons, corner).</summary>
    public const double MinScale = 0.6;

    public const double MaxScale = 1.6;

    /// <summary>
    /// The size of a panel whose bottom-right corner was dragged by (<paramref name="dx"/>, <paramref name="dy"/>) from
    /// where it was at <paramref name="scale"/>, <paramref name="width"/> x <paramref name="height"/> on screen. The drag
    /// is taken along the panel's diagonal, so the corner follows the pointer while the top-left corner stays; in 5 %
    /// steps like the wheel, between <see cref="MinScale"/> and <see cref="MaxScale"/>.
    /// </summary>
    public static double CornerScale(double scale, double width, double height, double dx, double dy)
    {
        var diagonal = (width * width) + (height * height);
        if (diagonal <= 0)
        {
            return scale;
        }

        var grown = scale * (1 + (((dx * width) + (dy * height)) / diagonal));
        return Math.Clamp(Math.Round(grown * 20, MidpointRounding.AwayFromZero) / 20, MinScale, MaxScale);
    }

    /// <summary>
    /// The start of a panel of <paramref name="size"/> dragged to <paramref name="start"/> along a surface of
    /// <paramref name="surface"/>: moved onto the edge gutter or the centre line when one is within
    /// <see cref="Distance"/>, otherwise unchanged.
    /// </summary>
    public static double Snap(double start, double size, double surface) => Snap(start, size, surface, []);

    /// <summary>
    /// As <see cref="Snap(double, double, double)"/>, and also onto the other panels along the same axis
    /// (<paramref name="others"/>: their start and end): the panel's start or end lines up with theirs, or it sits
    /// <see cref="Spacing"/> before or after one. The nearest guide within <see cref="Distance"/> wins.
    /// </summary>
    public static double Snap(double start, double size, double surface, ReadOnlySpan<(double Start, double End)> others)
    {
        if (surface <= 0 || size <= 0)
        {
            return start;
        }

        var best = start;
        var bestGap = Distance;
        if (size + (2 * Gutter) <= surface)
        {
            ReadOnlySpan<double> guides = [Gutter, surface - Gutter - size, (surface - size) / 2];
            foreach (var guide in guides)
            {
                Consider(guide);
            }
        }

        foreach (var (otherStart, otherEnd) in others)
        {
            Consider(otherStart);
            Consider(otherEnd - size);
            Consider(otherEnd + Spacing);
            Consider(otherStart - Spacing - size);
        }

        return best;

        void Consider(double guide)
        {
            var gap = Math.Abs(start - guide);
            if (gap <= bestGap)
            {
                best = guide;
                bestGap = gap;
            }
        }
    }

    /// <summary>The panel opacities the opacity button steps through, from opaque down.</summary>
    private static readonly double[] OpacitySteps = [1, 0.85, 0.7, 0.55, 0.4];

    /// <summary>The next opacity after <paramref name="opacity"/> for the panel's opacity button (back to opaque after the faintest).</summary>
    public static double NextOpacity(double opacity)
    {
        foreach (var step in OpacitySteps)
        {
            if (step < opacity - 0.01)
            {
                return step;
            }
        }

        return 1;
    }

    /// <summary>Opacity after one wheel notch (Shift+wheel): 5 % steps between <see cref="Hearthpulse.Core.PanelLook.MinOpacity"/> and opaque.</summary>
    public static double WheelOpacity(double opacity, bool up) => Math.Clamp(Math.Round(opacity + (up ? 0.05 : -0.05), 2), Hearthpulse.Core.PanelLook.MinOpacity, 1);

    /// <summary>"70%" for a see-through panel's tag; empty when it is opaque.</summary>
    public static string OpacityText(double opacity) =>
        opacity >= 0.999 ? string.Empty : opacity.ToString("P0", CultureInfo.CurrentCulture);

    /// <summary>"115%" for a resized panel's tag; empty at the default size.</summary>
    public static string ScaleText(double scale) =>
        Math.Abs(scale - 1) < 0.001 ? string.Empty : scale.ToString("P0", CultureInfo.CurrentCulture);
}
