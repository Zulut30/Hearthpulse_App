namespace Hearthpulse.Core.CardPool;

/// <summary>Where the card a pool belongs to sits in the game.</summary>
public enum PoolAnchorZone
{
    /// <summary>A card in your hand (the game shows its enlarged preview above it).</summary>
    Hand,

    /// <summary>The open Discover options, a row in the middle of the screen.</summary>
    Choice,
}

/// <summary>
/// The card a pool panel belongs to, from the game state the tracker already has (zone, position, counts): the panel is
/// placed next to it instead of at its saved spot.
/// </summary>
/// <param name="Index">0-based slot from the left in the hand; 0 for a choice.</param>
/// <param name="Count">Cards in the hand, or Discover options.</param>
public sealed record CardPoolAnchor(PoolAnchorZone Zone, int Index, int Count);

/// <summary>
/// Places the pool panel next to its card without covering the game's own enlarged preview of it. Works in units of a
/// 1080-high game screen centred horizontally, like Hearthstone's board; the geometry is measured on 16:9 screenshots.
/// </summary>
public static class PoolPlacement
{
    public const double ScreenHeight = 1080;

    private const double Gap = 14;

    private const double Edge = 8;

    // Hand: cards fan out at the bottom; the hovered card's preview (about 300 wide) rises above it.
    private const double HandSlot = 110;
    private const double HandSpan = 560;
    private const double HandPreviewHalf = 150;
    private const double HandTop = 900;

    // The game's keyword tooltips ("Battlecry", "Excavate") stand beside the enlarged preview on the side of the screen
    // centre, about this wide (measured on a 1080p screenshot).
    private const double TooltipColumn = 370;

    /// <summary>The popup is drawn no smaller than this share of its size at overlay scale 1, so it stays readable.</summary>
    public const double MinPopupScale = 0.85;

    // Discover: the options are about 300 apart, centred at about y 470.
    private const double ChoiceSlot = 300;
    private const double ChoiceRow = 470;

    /// <summary>Centre x of a hand slot, relative to the screen centre.</summary>
    public static double SlotOffset(CardPoolAnchor anchor)
    {
        var count = Math.Max(1, anchor.Count);
        var index = Math.Clamp(anchor.Index, 0, count - 1);
        var slot = anchor.Zone == PoolAnchorZone.Hand && count > 1 ? Math.Min(HandSlot, HandSpan / (count - 1)) : 0;
        return (index - ((count - 1) / 2.0)) * slot;
    }

    /// <summary>The panel's top-left in game units.</summary>
    /// <param name="width">Screen width in game units (1080 high: 1920 on 16:9).</param>
    /// <param name="panelWidth">Panel size in game units (as drawn, after the player's overlay scale).</param>
    public static (double X, double Y) Place(CardPoolAnchor anchor, double width, double panelWidth, double panelHeight)
    {
        var centre = width / 2;
        double x, y;
        switch (anchor.Zone)
        {
            case PoolAnchorZone.Hand:
            {
                var card = centre + SlotOffset(anchor);
                // Away from the middle of the screen, beside the preview: the game's keyword tooltips take the side towards
                // the middle. If that side is off the screen, towards the middle past the tooltips; else clamped (below).
                var right = card > centre;
                var outer = right ? card + HandPreviewHalf + Gap : card - HandPreviewHalf - Gap - panelWidth;
                var inner = right ? card - HandPreviewHalf - TooltipColumn - Gap - panelWidth : card + HandPreviewHalf + TooltipColumn + Gap;
                x = Fits(outer, width, panelWidth) || !Fits(inner, width, panelWidth) ? outer : inner;
                // The panel's bottom stays above the hand.
                y = HandTop - Gap - panelHeight;
                break;
            }

            default:
            {
                var half = Math.Max(1, anchor.Count) * ChoiceSlot / 2;
                var right = centre + half + Gap;
                x = right + panelWidth <= width - Edge ? right : centre - half - Gap - panelWidth;
                y = ChoiceRow - (panelHeight / 2);
                break;
            }
        }

        return (Math.Clamp(x, Edge, Math.Max(Edge, width - Edge - panelWidth)),
            Math.Clamp(y, Edge, Math.Max(Edge, ScreenHeight - Edge - panelHeight)));
    }

    private static bool Fits(double x, double width, double panelWidth) => x >= Edge && x <= width - Edge - panelWidth;

    /// <summary>
    /// The extra scale of the popup over the player's overlay <paramref name="scale"/>, so it is drawn at least at
    /// <see cref="MinPopupScale"/>: 1 at scale 0.85 and above.
    /// </summary>
    public static double PopupFloor(double scale)
    {
        scale = AppSettings.ClampScale(scale);
        return scale >= MinPopupScale ? 1 : MinPopupScale / scale;
    }

    /// <summary>
    /// The same in overlay surface units: the surface is <paramref name="surfaceWidth"/> wide and 1080 / scale high, and the
    /// panel's own size there grows with the player's overlay <paramref name="scale"/>, while the game's cards do not.
    /// </summary>
    public static (double X, double Y) PlaceOnSurface(CardPoolAnchor anchor, double surfaceWidth, double scale, double panelWidth, double panelHeight)
    {
        scale = AppSettings.ClampScale(scale);
        var (x, y) = Place(anchor, surfaceWidth * scale, panelWidth * scale, panelHeight * scale);
        return (x / scale, y / scale);
    }
}
