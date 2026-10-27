using System.Globalization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// Layout-mode helpers both <c>MovablePanel</c>s share: a dragged panel snaps to the screen edges and centre line,
/// and its name tag shows the size the player gave it.
/// </summary>
public static class PanelSnap
{
    /// <summary>How close (surface units) a panel edge must come to a guide to snap to it.</summary>
    public const double Distance = 10;

    /// <summary>Gap the default panel spots keep from the screen edge; a snapped panel lines up with them.</summary>
    public const double Gutter = 12;

    /// <summary>
    /// The start of a panel of <paramref name="size"/> dragged to <paramref name="start"/> along a surface of
    /// <paramref name="surface"/>: moved onto the edge gutter or the centre line when one is within
    /// <see cref="Distance"/>, otherwise unchanged.
    /// </summary>
    public static double Snap(double start, double size, double surface)
    {
        if (surface <= 0 || size <= 0 || size + (2 * Gutter) > surface)
        {
            return start;
        }

        var best = start;
        var bestGap = Distance;
        ReadOnlySpan<double> guides = [Gutter, surface - Gutter - size, (surface - size) / 2];
        foreach (var guide in guides)
        {
            var gap = Math.Abs(start - guide);
            if (gap <= bestGap)
            {
                best = guide;
                bestGap = gap;
            }
        }

        return best;
    }

    /// <summary>"115%" for a resized panel's tag; empty at the default size.</summary>
    public static string ScaleText(double scale) =>
        Math.Abs(scale - 1) < 0.001 ? string.Empty : scale.ToString("P0", CultureInfo.CurrentCulture);
}
