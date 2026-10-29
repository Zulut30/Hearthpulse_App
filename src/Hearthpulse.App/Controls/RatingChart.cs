using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.App.Controls;

/// <summary>Rating over time: a smooth gold curve with a soft glow and area, min/max guides and the latest value.</summary>
public sealed class RatingChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<RatingPoint>), typeof(RatingChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public RatingChart()
    {
        // The empty text is drawn here, so it follows a language switch only by redrawing. WPF may raise Loaded again
        // without an Unloaded in between: one subscription at most, or the app-wide event keeps a closed window.
        Loaded += (_, _) =>
        {
            Loc.Instance.LanguageChanged -= InvalidateVisual;
            Loc.Instance.LanguageChanged += InvalidateVisual;
        };
        Unloaded += (_, _) => Loc.Instance.LanguageChanged -= InvalidateVisual;
    }

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
            var empty = new FormattedText(Loc.T("Stats.Bg.RatingEmpty"), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 12, faint, dpi);
            empty.MaxTextWidth = Math.Max(10, w - 20);
            empty.TextAlignment = TextAlignment.Center;
            dc.DrawText(empty, new Point(10, (h - empty.Height) / 2));
            return;
        }

        const double left = ChartGeometry.RatingLeft;
        const double right = ChartGeometry.RatingRight;
        const double bottom = ChartGeometry.RatingBottom;
        var plot = ChartGeometry.Rating(points, w, h);
        foreach (var (value, guideY) in new[] { (plot.Max, plot.MaxY), (plot.Min, plot.MinY) })
        {
            var y = Math.Round(guideY) + 0.5;
            dc.DrawLine(grid, new Point(left, y), new Point(w - right, y));
            var label = new FormattedText(value.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 11, muted, dpi);
            dc.DrawText(label, new Point(left - label.Width - 8, y - (label.Height / 2)));
        }

        var at = Array.ConvertAll(plot.At, p => new Point(p.X, p.Y));
        var curve = ChartGeometry.MonotoneCurve(plot.At);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(at[0], false, false);
            Curve(ctx, curve);
        }

        line.Freeze();
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(at[0].X, h - bottom), true, true);
            ctx.LineTo(at[0], false, true);
            Curve(ctx, curve);
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
        var (bx, by, bw, bh) = ChartGeometry.Tag((last.X, last.Y), tag.Width, tag.Height, left, right, w);
        dc.DrawRoundedRectangle((Brush)FindResource("PrimaryFill"), null, new Rect(bx, by, bw, bh), 6, 6);
        dc.DrawText(tag, new Point(bx + ChartGeometry.TagPaddingX, by + ChartGeometry.TagPaddingY));
    }

    /// <summary>The smooth line through the points (<see cref="ChartGeometry.MonotoneCurve"/>).</summary>
    private static void Curve(StreamGeometryContext ctx, CurveSegment[] curve)
    {
        foreach (var s in curve)
        {
            ctx.BezierTo(new Point(s.X1, s.Y1), new Point(s.X2, s.Y2), new Point(s.X, s.Y), true, true);
        }
    }
}
