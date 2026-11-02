#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>What a table element marked as an arrow's end is: where its anchor sits and how far the arrow stays off it.</summary>
public enum MatchArrowAnchor
{
    /// <summary>A minion or location render (86 x 129): the anchor is the middle of its art.</summary>
    Minion,

    /// <summary>A hero portrait (62 x 62): its centre.</summary>
    Hero,

    /// <summary>A hero power, weapon or Secret chip (40 x 40): its centre.</summary>
    Chip,
}

/// <summary>A point in the arrow layer's pixels.</summary>
public readonly record struct MatchArrowPoint(double X, double Y);

/// <summary>
/// An arrow as drawn: the shaft from <see cref="Start"/> to <see cref="ShaftEnd"/> (a quadratic curve through
/// <see cref="Control"/> when both ends are on one side), the head at <see cref="Tip"/> with its two corners. An attack's
/// head is a filled triangle, a play's an open chevron.
/// </summary>
public sealed record MatchArrow(
    MatchArrowPoint Start,
    MatchArrowPoint? Control,
    MatchArrowPoint ShaftEnd,
    MatchArrowPoint Tip,
    MatchArrowPoint HeadLeft,
    MatchArrowPoint HeadRight,
    bool Attack,
    bool Local);

/// <summary>
/// The arrow of the Constructed table in both UIs (<c>MatchArrowLayer</c>, plan R2, <c>docs/match-replay.md</c>): where an
/// anchor sits in its element, the shaft between the step's actor and target, the bend on one side and the head. Each UI
/// finds the elements and draws.
/// </summary>
internal static class MatchArrowLayout
{
    /// <summary>The middle of a minion render's art, from its top.</summary>
    public const double MinionArtY = 46;

    /// <summary>The attack's shaft and the shade under it.</summary>
    public const double AttackStroke = 4;

    public const double AttackShade = 8;

    /// <summary>A play's dashed shaft (dash and gap in stroke widths) and the solid shade under it.</summary>
    public const double PlayStroke = 3;

    public const double PlayShade = 7;

    public const double PlayDash = 2.5;

    public const double PlayGap = 2;

    /// <summary>The attack head: a triangle this long and wide at its base, outlined in the shade by this much.</summary>
    public const double AttackHead = 14;

    public const double AttackHeadShade = 2;

    /// <summary>The play head: two arms this long at this angle off the shaft.</summary>
    public const double PlayArm = 11;

    public const double PlayArmDegrees = 32;

    /// <summary>The bend of an arrow between two ends on one side: this share of its length, at most <see cref="MaxBend"/>.</summary>
    public const double BendShare = 0.2;

    public const double MaxBend = 48;

    /// <summary>The theme brushes: the shaft and head by whose step it is, the shade under them.</summary>
    public static string StrokeBrush(bool local) => local ? "Accent" : "Accent2";

    public const string ShadeBrush = "OverlayBadgeShade";

    /// <summary>How far the shaft starts off its tail's anchor and stops off its head's (the head does not cover the change badge).</summary>
    public static (double Tail, double Tip) Insets(MatchArrowAnchor anchor) => anchor switch
    {
        MatchArrowAnchor.Minion => (20, 34),
        MatchArrowAnchor.Hero => (20, 36),
        _ => (10, 24),
    };

    /// <summary>The anchor of an element at (<paramref name="x"/>, <paramref name="y"/>) of the given size.</summary>
    public static MatchArrowPoint Anchor(MatchArrowAnchor anchor, double x, double y, double width, double height) =>
        anchor == MatchArrowAnchor.Minion ? new(x + width / 2, y + MinionArtY) : new(x + width / 2, y + height / 2);

    /// <summary>
    /// The arrow from the actor's anchor to the target's, or null when they are the same place or too close for one.
    /// Ends on one side of <paramref name="middleY"/> (the step bar) bend towards it, so the arrow does not cut through
    /// the cards of their row.
    /// </summary>
    public static MatchArrow? Build(MatchArrowPoint from, MatchArrowAnchor fromKind, MatchArrowPoint to, MatchArrowAnchor toKind, double middleY, bool attack, bool local)
    {
        var (tail, _) = Insets(fromKind);
        var (_, tip) = Insets(toKind);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < tail + tip + 8)
        {
            return null;
        }

        MatchArrowPoint? control = null;
        double startX = dx / length, startY = dy / length, endX = startX, endY = startY;
        if ((from.Y - middleY) * (to.Y - middleY) > 0)
        {
            var bend = Math.Min(BendShare * length, MaxBend);
            var (nx, ny) = (-dy / length, dx / length);
            var midY = (from.Y + to.Y) / 2;
            if (ny * (middleY - midY) < 0)
            {
                (nx, ny) = (-nx, -ny);
            }

            var c = new MatchArrowPoint((from.X + to.X) / 2 + nx * bend, midY + ny * bend);
            control = c;
            (startX, startY) = Unit(c.X - from.X, c.Y - from.Y);
            (endX, endY) = Unit(to.X - c.X, to.Y - c.Y);
        }

        var start = new MatchArrowPoint(from.X + startX * tail, from.Y + startY * tail);
        var head = new MatchArrowPoint(to.X - endX * tip, to.Y - endY * tip);
        MatchArrowPoint left, right, shaftEnd;
        if (attack)
        {
            // A triangle: the shaft stops inside its base, so its round cap never shows past the point.
            var baseX = head.X - endX * AttackHead;
            var baseY = head.Y - endY * AttackHead;
            left = new(baseX - endY * AttackHead / 2, baseY + endX * AttackHead / 2);
            right = new(baseX + endY * AttackHead / 2, baseY - endX * AttackHead / 2);
            shaftEnd = new(head.X - endX * (AttackHead - 2), head.Y - endY * (AttackHead - 2));
        }
        else
        {
            var angle = PlayArmDegrees * Math.PI / 180;
            left = Arm(head, -endX, -endY, angle);
            right = Arm(head, -endX, -endY, -angle);
            shaftEnd = head;
        }

        return new MatchArrow(start, control, shaftEnd, head, left, right, attack, local);
    }

    private static (double X, double Y) Unit(double x, double y)
    {
        var length = Math.Sqrt(x * x + y * y);
        return length < 1e-6 ? (0, 0) : (x / length, y / length);
    }

    private static MatchArrowPoint Arm(MatchArrowPoint from, double x, double y, double angle) =>
        new(from.X + (x * Math.Cos(angle) - y * Math.Sin(angle)) * PlayArm, from.Y + (x * Math.Sin(angle) + y * Math.Cos(angle)) * PlayArm);
}

/// <summary>
/// The event feed keeps its current row in view in both UIs (plan R2): a row out of sight comes to a third of the way down
/// (the actions before it and its turn's header stay visible), a row close to an edge moves just far enough to stand
/// <see cref="Edge"/> off it, so the feed creeps along during playback instead of jumping.
/// </summary>
internal static class MatchFeedLayout
{
    public const double Edge = 40;

    /// <summary>
    /// The feed's new offset for a row from <paramref name="top"/> to <paramref name="bottom"/> (relative to the visible
    /// part's top; with the turn's header when the row is the turn's first action), or null to stay.
    /// </summary>
    public static double? Offset(double offset, double viewport, double top, double bottom)
    {
        if (viewport <= 0)
        {
            return null;
        }

        if (bottom <= 0 || top >= viewport)
        {
            return Math.Max(0, offset + top - viewport / 3);
        }

        var edge = Math.Min(Edge, Math.Max(0, (viewport - (bottom - top)) / 2));
        if (top < edge)
        {
            return Math.Max(0, offset + top - edge);
        }

        return bottom > viewport - edge ? offset + bottom - (viewport - edge) : null;
    }
}
