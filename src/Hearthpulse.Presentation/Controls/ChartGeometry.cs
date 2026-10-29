#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Controls;
#else
namespace Hearthpulse.UI.Controls;
#endif

/// <summary>A point of <see cref="StatsLineChart"/>: its value and the text shown for it (axis guides, latest tag).</summary>
public interface IChartValue
{
    double ChartValue { get; }

    string ChartLabel { get; }
}

/// <summary>
/// Where the custom charts of both UIs put things, in pixels of the control: the points, ticks, hover card and latest-value
/// tag of <see cref="StatsLineChart"/>, and the tag of <see cref="RatingChart"/>. Numbers only: each UI measures its own
/// text, passes the widths in and draws with its own framework.
/// </summary>
public static class ChartGeometry
{
    /// <summary>Line chart plot insets: room right of the last point, above the highest and for the game numbers below.</summary>
    public const double LineRight = 14;

    public const double LineTop = 16;

    public const double LineBottom = 26;

    /// <summary>Gap between the hovered point and its card.</summary>
    public const double CardGap = 18;

    /// <summary>Points are drawn as dots only while they are at least this far apart.</summary>
    public const double DotSpacing = 9;

    /// <summary>Space around the latest-value tag's text.</summary>
    public const double TagPaddingX = 7;

    public const double TagPaddingY = 2;

    /// <summary>How far the latest-value tag sits above (or below) its point.</summary>
    private const double TagOffset = 14;

    /// <summary>
    /// Where each point of a line chart sits, the plot's left edge (room for the wider of the two guide labels, at the
    /// lowest and the highest point) and those two points. A flat line gets a little room above and below.
    /// </summary>
    public static LinePlot Line(IReadOnlyList<IChartValue> points, double width, double height, Func<string, double> labelWidth)
    {
        int lowest = 0, highest = 0;
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].ChartValue < points[lowest].ChartValue) lowest = i;
            if (points[i].ChartValue > points[highest].ChartValue) highest = i;
        }

        var left = points.Count == 0
            ? 10
            : Math.Ceiling(Math.Max(labelWidth(points[lowest].ChartLabel), labelWidth(points[highest].ChartLabel))) + 16;
        var min = points.Count == 0 ? 0 : points[lowest].ChartValue;
        var max = points.Count == 0 ? 0 : points[highest].ChartValue;
        var pad = Math.Max((max - min) * 0.12, Math.Max(1, Math.Abs(max) * 0.004));
        var lo = min - pad;
        var hi = max + pad;
        var at = new (double X, double Y)[points.Count];
        for (var i = 0; i < at.Length; i++)
        {
            var x = points.Count == 1 ? (left + width - LineRight) / 2 : left + ((width - left - LineRight) * i / (points.Count - 1));
            at[i] = (x, LineTop + ((height - LineTop - LineBottom) * (1 - ((points[i].ChartValue - lo) / (hi - lo)))));
        }

        return new LinePlot(at, left, lowest, highest);
    }

    /// <summary>The point nearest to <paramref name="x"/> across (the first of equals); 0 when there are none.</summary>
    public static int Nearest(IReadOnlyList<(double X, double Y)> at, double x)
    {
        var nearest = 0;
        for (var i = 1; i < at.Count; i++)
        {
            if (Math.Abs(at[i].X - x) < Math.Abs(at[nearest].X - x))
            {
                nearest = i;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The hover card's top-left corner: right of the point, or left of it when it would not fit; centred on the point
    /// up and down, inside the control.
    /// </summary>
    public static (double X, double Y) Card((double X, double Y) point, double cardWidth, double cardHeight, double width, double height)
    {
        var x = point.X + CardGap + cardWidth <= width ? point.X + CardGap : Math.Max(0, point.X - CardGap - cardWidth);
        var y = Math.Clamp(point.Y - (cardHeight / 2), 0, Math.Max(0, height - cardHeight));
        return (x, y);
    }

    /// <summary>The points that get a game number under the x axis: all of up to two, else the first, middle and last.</summary>
    public static IReadOnlyList<int> Ticks(int count) => count < 3 ? [.. Enumerable.Range(0, count)] : [0, (count - 1) / 2, count - 1];

    /// <summary>The left of a game number centred under its tick, kept between the plot's left edge and the control's right.</summary>
    public static double TickLabelX(double tickX, double labelWidth, double left, double width) =>
        Math.Max(left - (labelWidth / 2), Math.Min(tickX - (labelWidth / 2), width - labelWidth));

    /// <summary>True while the points stay far enough apart to be drawn as dots; a long history is just the line.</summary>
    public static bool DotsFit(IReadOnlyList<(double X, double Y)> at) =>
        at.Count <= 1 || (at[^1].X - at[0].X) / (at.Count - 1) >= DotSpacing;

    /// <summary>
    /// The latest value's tag: centred over its point (under it when there is no room above), kept between the plot's
    /// left edge and <paramref name="right"/> from the control's right edge; at the left edge when the plot is narrower
    /// than the tag. The text goes at <see cref="TagPaddingX"/>, <see cref="TagPaddingY"/> inside.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Tag(
        (double X, double Y) point, double textWidth, double textHeight, double left, double right, double width)
    {
        var w = textWidth + (2 * TagPaddingX);
        var h = textHeight + (2 * TagPaddingY);
        var x = Math.Clamp(point.X - (w / 2), left, Math.Max(left, width - right - w));
        var y = point.Y - h - TagOffset < 0 ? point.Y + TagOffset : point.Y - h - TagOffset;
        return (x, y, w, h);
    }
}

/// <summary>A laid-out line chart: each point's position, the plot's left edge and the lowest and highest point.</summary>
public sealed record LinePlot((double X, double Y)[] At, double Left, int Lowest, int Highest)
{
    public static LinePlot Empty { get; } = new([], 10, 0, 0);
}
