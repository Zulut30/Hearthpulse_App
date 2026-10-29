#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Controls;
#else
namespace Hearthpulse.UI.Controls;
#endif

/// <summary>Where the hover preview opens relative to the hovered element.</summary>
public enum CardPreviewPlacement
{
    /// <summary>
    /// Beside the panel that holds the element, on the side facing the screen centre (deck and counter rows); on a
    /// window page beside the element's list, right of it when there is room, at the element's height.
    /// </summary>
    Beside,

    /// <summary>Under the element itself (small marks along the top of the screen).</summary>
    Below,

    /// <summary>
    /// Right against the element itself, right of it when there is room, at its height (cards in a grid: the preview
    /// opens next to the hovered card instead of beside the whole panel).
    /// </summary>
    Adjacent,
}

/// <summary>A rectangle in the preview host's pixels.</summary>
public readonly record struct PreviewRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>
/// Where <c>CardPreviewHost</c> puts the hover preview of a card, in the host's pixels: beside the hovered element's
/// panel (in a window page: beside the row's list, at the row), so it never covers the row being read, and pulled
/// back inside the host near its edges. Each UI measures the preview and the elements and passes them in.
/// </summary>
public static class CardPreviewLayout
{
    /// <summary>Gap between the preview and what it opens beside.</summary>
    public const double Gap = 10;

    /// <summary>Gap of an <see cref="CardPreviewPlacement.Adjacent"/> preview: right against the hovered card.</summary>
    public const double TightGap = 4;

    /// <summary>The preview keeps this far from the host's edges.</summary>
    public const double Edge = 4;

    /// <summary>
    /// The preview's top-left corner for a hovered <paramref name="row"/>. <paramref name="panel"/> (the panel that holds
    /// the row) is asked for only by <see cref="CardPreviewPlacement.Beside"/>, and <paramref name="list"/> (the list that
    /// holds the row) only when that panel is a window page, wider than half the host.
    /// </summary>
    public static (double Left, double Top) Place(
        CardPreviewPlacement placement, PreviewRect row, double width, double height, double hostWidth, double hostHeight,
        Func<PreviewRect> panel, Func<PreviewRect> list)
    {
        double left, top;
        if (placement == CardPreviewPlacement.Below)
        {
            left = row.X + (row.Width / 2) - (width / 2);
            top = row.Bottom + Gap;
        }
        else if (placement == CardPreviewPlacement.Adjacent)
        {
            left = row.Right + TightGap + width <= hostWidth - Edge ? row.Right + TightGap : row.X - TightGap - width;
            top = row.Y + (row.Height / 2) - (height / 2);
        }
        else
        {
            var anchor = panel();
            if (anchor.Width > hostWidth / 2)
            {
                // A page rather than a floating panel: right beside the list that holds the row (left of it when the
                // right has no room), else beside the row itself, else under or over the row, never on top of it.
                var owner = list();
                top = row.Y + (row.Height / 2) - (height / 2);
                if ((Beside(owner, width, hostWidth) ?? Beside(row, width, hostWidth)) is { } x)
                {
                    left = x;
                }
                else
                {
                    left = row.X + Gap;
                    top = row.Bottom + Gap + height <= hostHeight - Edge ? row.Bottom + Gap : row.Y - Gap - height;
                }
            }
            else
            {
                var onRight = anchor.X + (anchor.Width / 2) > hostWidth / 2;
                left = onRight ? anchor.X - width - Gap : anchor.Right + Gap;
                top = row.Y + (row.Height / 2) - (height / 2);
            }
        }

        return (Math.Clamp(left, Edge, Math.Max(Edge, hostWidth - width - Edge)), Math.Clamp(top, Edge, Math.Max(Edge, hostHeight - height - Edge)));
    }

    /// <summary>Left edge of a preview right of <paramref name="rect"/>, or left of it; null when neither side has room.</summary>
    private static double? Beside(PreviewRect rect, double width, double hostWidth) =>
        rect.Right + Gap + width <= hostWidth - Edge ? rect.Right + Gap
        : rect.X - Gap - width >= Edge ? rect.X - Gap - width
        : null;
}
