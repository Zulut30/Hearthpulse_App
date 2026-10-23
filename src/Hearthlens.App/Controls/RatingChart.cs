using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Hearthlens.Core.Data;

namespace Hearthlens.App.Controls;

/// <summary>Rating over time: a smooth gold curve with a soft glow and area, min/max guides and the latest value.</summary>
public sealed class RatingChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<RatingPoint>), typeof(RatingChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<RatingPoint>? Points
    {
        get => (IReadOnlyList<RatingPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 40 || h < 40)
        {
            return;
        }

        var accent = (Brush)FindResource("Accent");
        var faint = (Brush)FindResource("Faint");
        var muted = (Brush)FindResource("Muted");
        var grid = new Pen((Brush)FindResource("Border"), 1) { DashStyle = new DashStyle([3, 4], 0) };
        var typeface = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var points = Points;
        if (points is null || points.Count < 2)
        {
            var empty = new FormattedText("Play a few rated Battlegrounds games to see your rating curve.", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 12, faint, dpi);
            dc.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
            return;
        }

        const double left = 44;
        const double right = 12;
        const double top = 12;
        const double bottom = 10;
        var min = points.Min(p => p.Rating);
        var max = points.Max(p => p.Rating);
        var pad = Math.Max(20, (max - min) * 0.12);
        var lo = min - pad;
        var hi = max + pad;
        double X(int i) => left + ((w - left - right) * i / (points.Count - 1));
        double Y(int rating) => top + ((h - top - bottom) * (1 - ((rating - lo) / (hi - lo))));

        foreach (var value in new[] { max, min })
        {
            var y = Math.Round(Y(value)) + 0.5;
            dc.DrawLine(grid, new Point(left, y), new Point(w - right, y));
            var label = new FormattedText(value.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 11, muted, dpi);
            dc.DrawText(label, new Point(left - label.Width - 8, y - (label.Height / 2)));
        }

        var at = new Point[points.Count];
        for (var i = 0; i < at.Length; i++)
        {
            at[i] = new Point(X(i), Y(points[i].Rating));
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(at[0], false, false);
            Curve(ctx, at);
        }

        line.Freeze();
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(at[0].X, h - bottom), true, true);
            ctx.LineTo(at[0], false, true);
            Curve(ctx, at);
            ctx.LineTo(new Point(at[^1].X, h - bottom), false, false);
        }

        area.Freeze();
        var gold = accent is SolidColorBrush solid ? solid.Color : Color.FromRgb(0xE9, 0xB9, 0x49);
        Color Gold(byte alpha) => Color.FromArgb(alpha, gold.R, gold.G, gold.B);
        dc.DrawGeometry(new LinearGradientBrush(Gold(0x4A), Gold(0x00), 90), null, area);

        // A wide faint stroke under the line reads as a glow without a bitmap effect.
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Gold(0x2E)), 7) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        var deep = TryFindResource("AccentDeep") is SolidColorBrush d ? d.Color : Color.FromRgb(0xC4, 0x8E, 0x2C);
        var light = Color.FromRgb((byte)(((gold.R * 2) + 255) / 3), (byte)(((gold.G * 2) + 255) / 3), (byte)(((gold.B * 2) + 255) / 3));
        var stroke = new LinearGradientBrush(deep, light, 0);
        dc.DrawGeometry(null, new Pen(stroke, 2.4) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);

        var last = at[^1];
        dc.DrawEllipse(new SolidColorBrush(Gold(0x30)), null, last, 10, 10);
        dc.DrawEllipse((Brush)FindResource("Bg"), new Pen(accent, 2.4), last, 4.5, 4.5);

        // The latest rating sits in a small gold tag above its point.
        var tag = new FormattedText(points[^1].Rating.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            12, (Brush)FindResource("OnAccent"), dpi);
        var box = new Rect(0, 0, tag.Width + 14, tag.Height + 4);
        box.X = Math.Clamp(last.X - (box.Width / 2), left, w - right - box.Width);
        box.Y = last.Y - box.Height - 14 < 0 ? last.Y + 14 : last.Y - box.Height - 14;
        dc.DrawRoundedRectangle((Brush)FindResource("PrimaryFill"), null, box, 6, 6);
        dc.DrawText(tag, new Point(box.X + 7, box.Y + 2));
    }

    /// <summary>
    /// Smooth line through the points as monotone cubic Béziers (Fritsch-Carlson): the curve never overshoots a
    /// point, so a dip or a peak is drawn exactly as deep as the rating really went.
    /// </summary>
    private static void Curve(StreamGeometryContext ctx, Point[] p)
    {
        var n = p.Length;
        var secant = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            secant[i] = (p[i + 1].Y - p[i].Y) / (p[i + 1].X - p[i].X);
        }

        var slope = new double[n];
        slope[0] = secant[0];
        slope[n - 1] = secant[n - 2];
        for (var i = 1; i < n - 1; i++)
        {
            slope[i] = secant[i - 1] * secant[i] <= 0 ? 0 : (secant[i - 1] + secant[i]) / 2;
        }

        for (var i = 0; i < n - 1; i++)
        {
            if (secant[i] == 0)
            {
                slope[i] = 0;
                slope[i + 1] = 0;
                continue;
            }

            var a = slope[i] / secant[i];
            var b = slope[i + 1] / secant[i];
            var s = (a * a) + (b * b);
            if (s > 9)
            {
                var t = 3 / Math.Sqrt(s);
                slope[i] = t * a * secant[i];
                slope[i + 1] = t * b * secant[i];
            }
        }

        for (var i = 0; i < n - 1; i++)
        {
            var third = (p[i + 1].X - p[i].X) / 3;
            ctx.BezierTo(new Point(p[i].X + third, p[i].Y + (slope[i] * third)),
                new Point(p[i + 1].X - third, p[i + 1].Y - (slope[i + 1] * third)), p[i + 1], true, true);
        }
    }
}
