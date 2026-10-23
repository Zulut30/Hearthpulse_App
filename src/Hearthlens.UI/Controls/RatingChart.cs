using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Hearthlens.Core.Data;
using Hearthlens.UI.Infrastructure;

namespace Hearthlens.UI.Controls;

/// <summary>Rating over time: soft area, crisp line, min/max guides and the latest value.</summary>
public sealed class RatingChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<RatingPoint>?> PointsProperty =
        AvaloniaProperty.Register<RatingChart, IReadOnlyList<RatingPoint>?>(nameof(Points));

    static RatingChart()
    {
        AffectsRender<RatingChart>(PointsProperty);
    }

    public IReadOnlyList<RatingPoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 40 || h < 40)
        {
            return;
        }

        var accent = ThemeBrushes.Get("Accent");
        var muted = ThemeBrushes.Get("Faint");
        var grid = new Pen(ThemeBrushes.Get("Border"), 1, new DashStyle([3, 4], 0));
        var typeface = new Typeface(this.TryFindResource("UiFont", ActualThemeVariant, out var font) && font is FontFamily family ? family : FontFamily.Default);

        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        var points = Points;
        if (points is null || points.Count < 2)
        {
            var empty = new FormattedText("Play a few rated Battlegrounds games to see your rating curve.", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 12, muted);
            context.DrawText(empty, new Point((w - empty.Width) / 2, (h - empty.Height) / 2));
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
            context.DrawLine(grid, new Point(left, y), new Point(w - right, y));
            var label = new FormattedText(value.ToString("N0", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 11, muted);
            context.DrawText(label, new Point(left - label.Width - 8, y - (label.Height / 2)));
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(0), Y(points[0].Rating)), false);
            for (var i = 1; i < points.Count; i++)
            {
                ctx.LineTo(new Point(X(i), Y(points[i].Rating)));
            }

            ctx.EndFigure(false);
        }

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(X(0), h - bottom), true);
            for (var i = 0; i < points.Count; i++)
            {
                ctx.LineTo(new Point(X(i), Y(points[i].Rating)));
            }

            ctx.LineTo(new Point(X(points.Count - 1), h - bottom));
            ctx.EndFigure(true);
        }

        var color = accent is ISolidColorBrush solid ? solid.Color : Color.FromRgb(0xE9, 0xB9, 0x49);
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x40, color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb(0x00, color.R, color.G, color.B), 1),
            },
        };
        context.DrawGeometry(fill, null, area);
        context.DrawGeometry(null, new Pen(accent, 2, lineJoin: PenLineJoin.Round), line);

        var last = new Point(X(points.Count - 1), Y(points[^1].Rating));
        context.DrawEllipse(ThemeBrushes.Get("Bg"), new Pen(accent, 2), last, 4, 4);
    }
}
