using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main.Match;

/// <summary>
/// The arrow of the Constructed table (plan R2): drawn over the table between the step's actor and its target, found by
/// their replay ids (<see cref="RefProperty"/> on the cards, hero portraits and chips of the panel beside it). It never
/// takes the pointer, so the card previews under it work. Geometry: <see cref="MatchArrowLayout"/>; drawn again whenever
/// the table's layout moves an end.
/// </summary>
public sealed class MatchArrowLayer : Control
{
    public static readonly StyledProperty<int> FromProperty = AvaloniaProperty.Register<MatchArrowLayer, int>(nameof(From));

    public static readonly StyledProperty<int> ToProperty = AvaloniaProperty.Register<MatchArrowLayer, int>(nameof(To));

    public static readonly StyledProperty<bool> IsAttackProperty = AvaloniaProperty.Register<MatchArrowLayer, bool>(nameof(IsAttack));

    public static readonly StyledProperty<bool> LocalProperty = AvaloniaProperty.Register<MatchArrowLayer, bool>(nameof(Local));

    /// <summary>The replay id of a table element an arrow can start or end at (0: none).</summary>
    public static readonly AttachedProperty<int> RefProperty = AvaloniaProperty.RegisterAttached<MatchArrowLayer, Control, int>("Ref");

    /// <summary>What the marked element is, for where its anchor sits.</summary>
    public static readonly AttachedProperty<MatchArrowAnchor> AnchorProperty =
        AvaloniaProperty.RegisterAttached<MatchArrowLayer, Control, MatchArrowAnchor>("Anchor", MatchArrowAnchor.Minion);

    /// <summary>The step bar: an arrow with both ends on one side bends towards it.</summary>
    public static readonly AttachedProperty<bool> MiddleProperty = AvaloniaProperty.RegisterAttached<MatchArrowLayer, Control, bool>("Middle");

    private MatchArrow? _drawn;

    static MatchArrowLayer() => AffectsRender<MatchArrowLayer>(FromProperty, ToProperty, IsAttackProperty, LocalProperty);

    public MatchArrowLayer()
    {
        IsHitTestVisible = false;
        LayoutUpdated += (_, _) =>
        {
            if (IsEffectivelyVisible && !Equals(Arrow(), _drawn))
            {
                InvalidateVisual();
            }
        };
    }

    public int From { get => GetValue(FromProperty); set => SetValue(FromProperty, value); }

    public int To { get => GetValue(ToProperty); set => SetValue(ToProperty, value); }

    public bool IsAttack { get => GetValue(IsAttackProperty); set => SetValue(IsAttackProperty, value); }

    /// <summary>Your step (the accent) or the opponent's (the second accent).</summary>
    public bool Local { get => GetValue(LocalProperty); set => SetValue(LocalProperty, value); }

    public static int GetRef(Control element) => element.GetValue(RefProperty);

    public static void SetRef(Control element, int value) => element.SetValue(RefProperty, value);

    public static MatchArrowAnchor GetAnchor(Control element) => element.GetValue(AnchorProperty);

    public static void SetAnchor(Control element, MatchArrowAnchor value) => element.SetValue(AnchorProperty, value);

    public static bool GetMiddle(Control element) => element.GetValue(MiddleProperty);

    public static void SetMiddle(Control element, bool value) => element.SetValue(MiddleProperty, value);

    /// <summary>The arrow as it would be drawn now, or null (no target, an end not on the table, actor and target the same).</summary>
    internal MatchArrow? Arrow()
    {
        if (From == 0 || To == 0 || From == To || this.GetVisualParent() is not { } parent)
        {
            return null;
        }

        Control? from = null, to = null, middle = null;
        Find(parent, ref from, ref to, ref middle);
        if (from is null || to is null || Place(from) is not { } fromAt || Place(to) is not { } toAt)
        {
            return null;
        }

        var middleY = middle is not null && Place(middle) is { } at ? at.Y + middle.Bounds.Height / 2 : Bounds.Height / 2;
        return MatchArrowLayout.Build(
            MatchArrowLayout.Anchor(GetAnchor(from), fromAt.X, fromAt.Y, from.Bounds.Width, from.Bounds.Height), GetAnchor(from),
            MatchArrowLayout.Anchor(GetAnchor(to), toAt.X, toAt.Y, to.Bounds.Width, to.Bounds.Height), GetAnchor(to),
            middleY, IsAttack, Local);
    }

    private void Find(Visual node, ref Control? from, ref Control? to, ref Control? middle)
    {
        foreach (var visual in node.GetVisualChildren())
        {
            if (ReferenceEquals(visual, this) || !visual.IsVisible)
            {
                continue;
            }

            if (visual is Control child)
            {
                var id = GetRef(child);
                if (id != 0 && id == From)
                {
                    from ??= child;
                }
                else if (id != 0 && id == To)
                {
                    to ??= child;
                }
                else if (GetMiddle(child))
                {
                    middle ??= child;
                }
            }

            Find(visual, ref from, ref to, ref middle);
        }
    }

    private Point? Place(Control element) => element.TranslatePoint(default, this);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _drawn = Arrow();
        if (_drawn is not { } arrow)
        {
            return;
        }

        var stroke = ThemeBrushes.Get(MatchArrowLayout.StrokeBrush(arrow.Local));
        var shade = ThemeBrushes.Get(MatchArrowLayout.ShadeBrush);
        var shaft = Shaft(arrow);
        var head = Head(arrow, closed: arrow.Attack);
        if (arrow.Attack)
        {
            context.DrawGeometry(null, Pen(shade, MatchArrowLayout.AttackShade), shaft);
            context.DrawGeometry(null, Pen(shade, MatchArrowLayout.AttackHeadShade * 2), head);
            context.DrawGeometry(null, Pen(stroke, MatchArrowLayout.AttackStroke), shaft);
            context.DrawGeometry(stroke, null, head);
        }
        else
        {
            var dashed = new Pen(stroke, MatchArrowLayout.PlayStroke, new DashStyle([MatchArrowLayout.PlayDash, MatchArrowLayout.PlayGap], 0), PenLineCap.Round, PenLineJoin.Round);
            context.DrawGeometry(null, Pen(shade, MatchArrowLayout.PlayShade), shaft);
            context.DrawGeometry(null, Pen(shade, MatchArrowLayout.PlayShade), head);
            context.DrawGeometry(null, dashed, shaft);
            context.DrawGeometry(null, Pen(stroke, MatchArrowLayout.PlayStroke), head);
        }
    }

    private static Pen Pen(IBrush brush, double thickness) => new(brush, thickness, null, PenLineCap.Round, PenLineJoin.Round);

    private static Point P(MatchArrowPoint point) => new(point.X, point.Y);

    private static StreamGeometry Shaft(MatchArrow arrow)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(arrow.Start), isFilled: false);
            if (arrow.Control is { } control)
            {
                context.QuadraticBezierTo(P(control), P(arrow.ShaftEnd));
            }
            else
            {
                context.LineTo(P(arrow.ShaftEnd));
            }

            context.EndFigure(isClosed: false);
        }

        return geometry;
    }

    /// <summary>The head: a closed triangle (an attack) or the two arms of a chevron through the tip.</summary>
    private static StreamGeometry Head(MatchArrow arrow, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(arrow.HeadLeft), isFilled: closed);
            context.LineTo(P(arrow.Tip));
            context.LineTo(P(arrow.HeadRight));
            context.EndFigure(isClosed: closed);
        }

        return geometry;
    }
}
