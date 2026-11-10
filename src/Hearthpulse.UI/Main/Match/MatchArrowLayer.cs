using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Main.Match;

/// <summary>
/// The arrow of the Constructed table (plan R2): drawn over the table between the step's actor and its target, found by
/// their replay ids (<see cref="RefProperty"/> on the cards, hero portraits and chips of the panel beside it). It never
/// takes the pointer, so the card previews under it work. Geometry: <see cref="MatchArrowLayout"/>. The arrow is worked
/// out once after the layout pass that follows a change of the step (<see cref="Version"/> and the ends), of the
/// layer's size or of its visibility, and drawn from that: a layout pass anywhere else in the window (LayoutUpdated
/// fires for all of them) costs it nothing. Before, every pass placed both ends and built the geometry again.
/// </summary>
public sealed class MatchArrowLayer : Control
{
    public static readonly StyledProperty<int> FromProperty = AvaloniaProperty.Register<MatchArrowLayer, int>(nameof(From));

    public static readonly StyledProperty<int> ToProperty = AvaloniaProperty.Register<MatchArrowLayer, int>(nameof(To));

    public static readonly StyledProperty<bool> IsAttackProperty = AvaloniaProperty.Register<MatchArrowLayer, bool>(nameof(IsAttack));

    public static readonly StyledProperty<bool> LocalProperty = AvaloniaProperty.Register<MatchArrowLayer, bool>(nameof(Local));

    /// <summary>
    /// The table's change count (<see cref="MatchTable.Version"/>): a step can move the ends without changing their ids
    /// (a minion summoned beside the attacker, the other side at the bottom).
    /// </summary>
    public static readonly StyledProperty<int> VersionProperty = AvaloniaProperty.Register<MatchArrowLayer, int>(nameof(Version));

    /// <summary>The replay id of a table element an arrow can start or end at (0: none).</summary>
    public static readonly AttachedProperty<int> RefProperty = AvaloniaProperty.RegisterAttached<MatchArrowLayer, Control, int>("Ref");

    /// <summary>What the marked element is, for where its anchor sits.</summary>
    public static readonly AttachedProperty<MatchArrowAnchor> AnchorProperty =
        AvaloniaProperty.RegisterAttached<MatchArrowLayer, Control, MatchArrowAnchor>("Anchor", MatchArrowAnchor.Minion);

    /// <summary>The step bar: an arrow with both ends on one side bends towards it.</summary>
    public static readonly AttachedProperty<bool> MiddleProperty = AvaloniaProperty.RegisterAttached<MatchArrowLayer, Control, bool>("Middle");

    // The arrow worked out after the last layout pass that followed a change, and what Render draws.
    private MatchArrow? _arrow;

    // A change is waiting for its layout pass (LayoutUpdated is listened to only meanwhile).
    private bool _pending;

    // The ends found by the last walk of the table: walked again only when the step's ends change, the layer's size
    // changes, or a kept end no longer carries its id or is hidden (review m4: not on every layout pass).
    private Control? _from;
    private Control? _to;
    private Control? _middle;
    private (int From, int To, Size Size) _found;

    public MatchArrowLayer() => IsHitTestVisible = false;

    /// <summary>How many times the geometry was worked out (tests: at most twice a step, never on a layout pass alone).</summary>
    internal int Computed { get; private set; }

    public int Version { get => GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FromProperty || change.Property == ToProperty || change.Property == IsAttackProperty || change.Property == LocalProperty
            || change.Property == VersionProperty || change.Property == BoundsProperty || change.Property == IsVisibleProperty)
        {
            Invalidate();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Invalidate();
    }

    /// <summary>
    /// The step, the size or the visibility changed: the arrow is worked out once the layout that follows has placed
    /// the cards (a pass is asked for, in case nothing else needs one).
    /// </summary>
    private void Invalidate()
    {
        if (_pending)
        {
            return;
        }

        _pending = true;
        LayoutUpdated += OnLayoutUpdated;
        InvalidateArrange();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        _pending = false;
        var arrow = Compute();
        if (!Equals(arrow, _arrow))
        {
            _arrow = arrow;
            InvalidateVisual();
        }
    }

    /// <summary>The arrow as drawn, or null (no target, an end not on the table, actor and target the same).</summary>
    internal MatchArrow? Arrow() => _arrow;

    private MatchArrow? Compute()
    {
        Computed++;
        if (From == 0 || To == 0 || From == To || this.GetVisualParent() is not { } parent)
        {
            return null;
        }

        if (_found != (From, To, Bounds.Size) || !Holds(_from, From) || !Holds(_to, To))
        {
            Control? found = null, target = null, bar = null;
            Find(parent, ref found, ref target, ref bar);
            (_from, _to, _middle, _found) = (found, target, bar, (From, To, Bounds.Size));
        }

        var (from, to, middle) = (_from, _to, _middle);
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

    /// <summary>A kept end still shows the element of that id.</summary>
    private static bool Holds(Control? element, int id) => element is { IsVisible: true } && GetRef(element) == id && element.GetVisualParent() is not null;

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
        if (_arrow is not { } arrow)
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
