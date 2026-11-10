using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Hearthpulse.App.Controls;

/// <summary>
/// The whole chrome of an underline segment (the theme's <c>Segment</c> look) as one element: the hover tint with its
/// rounded top, the padding around the label and the gold line that grows from its middle. The theme's template takes
/// a Grid and three Borders for it; a filter bar of thirty segments is cheaper to build with one. The template
/// animates <see cref="HoverOpacity"/> and <see cref="LineScale"/> as it animated the layers.
/// </summary>
public sealed class SegmentPlate : Border
{
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.Register(
        nameof(HoverBrush), typeof(Brush), typeof(SegmentPlate), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HoverOpacityProperty = DependencyProperty.Register(
        nameof(HoverOpacity), typeof(double), typeof(SegmentPlate), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(SegmentPlate), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineScaleProperty = DependencyProperty.Register(
        nameof(LineScale), typeof(double), typeof(SegmentPlate), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The corner radius of the tint's top corners.</summary>
    private const double Corner = 6;

    /// <summary>The line: 2 high, 4 in from each side, 1 below the segment's bottom edge (it lies on the bar's common line).</summary>
    private const double LineHeight = 2;
    private const double LineInset = 4;
    private const double LineDrop = 1;

    /// <summary>The tint under the pointer (the pressed one while pressed).</summary>
    public Brush? HoverBrush { get => (Brush?)GetValue(HoverBrushProperty); set => SetValue(HoverBrushProperty, value); }

    /// <summary>How much of the tint shows, 0 to 1.</summary>
    public double HoverOpacity { get => (double)GetValue(HoverOpacityProperty); set => SetValue(HoverOpacityProperty, value); }

    /// <summary>The line under the chosen segment.</summary>
    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    /// <summary>How much of the line's width shows, grown from its middle: 0 to 1.</summary>
    public double LineScale { get => (double)GetValue(LineScaleProperty); set => SetValue(LineScaleProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var width = RenderSize.Width;
        var height = RenderSize.Height;
        if (HoverBrush is { } hover && HoverOpacity > 0 && width > 0 && height > 0)
        {
            var radius = Math.Min(Corner, Math.Min(width / 2, height));
            var tint = new StreamGeometry();
            using (var figure = tint.Open())
            {
                figure.BeginFigure(new Point(0, height), isFilled: true, isClosed: true);
                figure.LineTo(new Point(0, radius), isStroked: false, isSmoothJoin: false);
                figure.ArcTo(new Point(radius, 0), new Size(radius, radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
                figure.LineTo(new Point(width - radius, 0), isStroked: false, isSmoothJoin: false);
                figure.ArcTo(new Point(width, radius), new Size(radius, radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
                figure.LineTo(new Point(width, height), isStroked: false, isSmoothJoin: false);
            }

            tint.Freeze();
            dc.PushOpacity(Math.Min(1, HoverOpacity));
            dc.DrawGeometry(hover, null, tint);
            dc.Pop();
        }

        // The Border's own fill (transparent, so the whole segment takes the pointer).
        base.OnRender(dc);

        var room = width - (2 * LineInset);
        if (LineBrush is { } line && LineScale > 0 && room > 0)
        {
            var length = room * Math.Min(1, LineScale);
            var rect = new Rect((width - length) / 2, height - LineHeight + LineDrop, length, LineHeight);
            var guides = new GuidelineSet([rect.Left, rect.Right], [rect.Top, rect.Bottom]);
            guides.Freeze();
            dc.PushGuidelineSet(guides);
            dc.DrawRoundedRectangle(line, null, rect, LineHeight / 2, LineHeight / 2);
            dc.Pop();
        }
    }
}
