using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>Rating over time: a smooth gold curve with a soft glow and area, min/max guides and the latest value.</summary>
public sealed class RatingChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<RatingPoint>?> PointsProperty =
        AvaloniaProperty.Register<RatingChart, IReadOnlyList<RatingPoint>?>(nameof(Points));

    static RatingChart()
    {
        AffectsRender<RatingChart>(PointsProperty);
    }

    public IReadOnlyList<RatingPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    // The empty text is drawn here, so it follows a language switch only by redrawing.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.Instance.LanguageChanged += InvalidateVisual;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Loc.Instance.LanguageChanged -= InvalidateVisual;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40)
        {
            return;
        }

        var accent = ThemeBrushes.Get("Accent");
        var faint = ThemeBrushes.Get("Faint");
        var muted = ThemeBrushes.Get("Muted");
        var grid = new Pen(ThemeBrushes.Get("Border"), 1, new DashStyle([3, 4], 0));
        var family = this.TryFindResource("UiFont", ActualThemeVariant, out var font) && font is FontFamily f ? f : FontFamily.Default;
        var typeface = new Typeface(family);

        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        var points = Points;
        if (points is null || points.Count < 2)
        {
            var empty = new FormattedText(Loc.T("Stats.Bg.RatingEmpty"), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 12, faint);
            empty.MaxTextWidth = Math.Max(10, w - 20);
            empty.TextAlignment = TextAlignment.Center;
            context.DrawText(empty, new Point(10, (h - empty.Height) / 2));
            return;
        }

        const double left = ChartGeometry.RatingLeft;
        const double right = ChartGeometry.RatingRight;
        const double bottom = ChartGeometry.RatingBottom;
        var plot = ChartGeometry.Rating(points, w, h);
        foreach (var (value, guideY) in new[] { (plot.Max, plot.MaxY), (plot.Min, plot.MinY) })
        {
            var y = Math.Round(guideY) + 0.5;
            context.DrawLine(grid, new Point(left, y), new Point(w - right, y));
            var label = new FormattedText(value.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 11, muted);
            context.DrawText(label, new Point(left - label.Width - 8, y - (label.Height / 2)));
        }

        var curve = ChartGeometry.MonotoneCurve(plot.At);
        var first = new Point(plot.At[0].X, plot.At[0].Y);
        var last = new Point(plot.At[^1].X, plot.At[^1].Y);
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(first, false);
            Curve(ctx, curve);
            ctx.EndFigure(false);
        }

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(first.X, h - bottom), true);
            ctx.LineTo(first);
            Curve(ctx, curve);
            ctx.LineTo(new Point(last.X, h - bottom));
            ctx.EndFigure(true);
        }

        var gold = accent is ISolidColorBrush solid ? solid.Color : Color.FromRgb(0xE9, 0xB9, 0x49);
        Color Gold(byte alpha) => Color.FromArgb(alpha, gold.R, gold.G, gold.B);
        context.DrawGeometry(Gradient(Gold(0x4A), Gold(0x00), new RelativePoint(0, 1, RelativeUnit.Relative)), null, area);

        // A wide faint stroke under the line reads as a glow without a bitmap effect.
        context.DrawGeometry(null, new Pen(new SolidColorBrush(Gold(0x2E)), 7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
        var deep = this.TryFindResource("AccentDeep", ActualThemeVariant, out var found) && found is ISolidColorBrush d ? d.Color : Color.FromRgb(0xC4, 0x8E, 0x2C);
        var light = Color.FromRgb((byte)(((gold.R * 2) + 255) / 3), (byte)(((gold.G * 2) + 255) / 3), (byte)(((gold.B * 2) + 255) / 3));
        var stroke = Gradient(deep, light, new RelativePoint(1, 0, RelativeUnit.Relative));
        context.DrawGeometry(null, new Pen(stroke, 2.4, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);

        context.DrawEllipse(new SolidColorBrush(Gold(0x30)), null, last, 10, 10);
        context.DrawEllipse(ThemeBrushes.Get("Bg"), new Pen(accent, 2.4), last, 4.5, 4.5);

        // The latest rating sits in a small gold tag above its point.
        var tag = new FormattedText(points[^1].Rating.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(family, FontStyle.Normal, FontWeight.Bold), 12, ThemeBrushes.Get("OnAccent"));
        var (bx, by, bw, bh) = ChartGeometry.Tag((last.X, last.Y), tag.Width, tag.Height, left, right, w);
        context.DrawRectangle(ThemeBrushes.Get("PrimaryFill"), null, new Rect(bx, by, bw, bh), 6, 6);
        context.DrawText(tag, new Point(bx + ChartGeometry.TagPaddingX, by + ChartGeometry.TagPaddingY));
    }

    /// <summary>The smooth line through the points (<see cref="ChartGeometry.MonotoneCurve"/>).</summary>
    private static void Curve(StreamGeometryContext ctx, CurveSegment[] curve)
    {
        foreach (var s in curve)
        {
            ctx.CubicBezierTo(new Point(s.X1, s.Y1), new Point(s.X2, s.Y2), new Point(s.X, s.Y));
        }
    }

    /// <summary>A two-stop gradient from the top-left corner to <paramref name="end"/> (relative to what it fills).</summary>
    private static LinearGradientBrush Gradient(Color from, Color to, RelativePoint end) => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = end,
        GradientStops = { new GradientStop(from, 0), new GradientStop(to, 1) },
    };
}
