using System.Windows;
using System.Windows.Media;

namespace Hearthpulse.App.Main.Match;

/// <summary>
/// The arrow of the Constructed table (plan R2): drawn over the table between the step's actor and its target, found by
/// their replay ids (<see cref="RefProperty"/> on the cards, hero portraits and chips of the panel beside it). It never
/// takes the mouse, so the card previews under it work. Geometry: <see cref="MatchArrowLayout"/>. The arrow is worked
/// out once after the layout pass that follows a change of the step (<see cref="Version"/> and the ends), of the
/// layer's size or of its visibility, and drawn from that: a layout pass anywhere else in the window (LayoutUpdated
/// fires for all of them) costs it nothing. Before, every pass placed both ends and built the geometry again.
/// </summary>
public sealed class MatchArrowLayer : FrameworkElement
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(nameof(From), typeof(int), typeof(MatchArrowLayer),
        new FrameworkPropertyMetadata(0, OnStepChanged));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(nameof(To), typeof(int), typeof(MatchArrowLayer),
        new FrameworkPropertyMetadata(0, OnStepChanged));

    public static readonly DependencyProperty IsAttackProperty = DependencyProperty.Register(nameof(IsAttack), typeof(bool), typeof(MatchArrowLayer),
        new FrameworkPropertyMetadata(false, OnStepChanged));

    public static readonly DependencyProperty LocalProperty = DependencyProperty.Register(nameof(Local), typeof(bool), typeof(MatchArrowLayer),
        new FrameworkPropertyMetadata(false, OnStepChanged));

    /// <summary>
    /// The table's change count (<see cref="MatchTable.Version"/>): a step can move the ends without changing their ids
    /// (a minion summoned beside the attacker, the other side at the bottom).
    /// </summary>
    public static readonly DependencyProperty VersionProperty = DependencyProperty.Register(nameof(Version), typeof(int), typeof(MatchArrowLayer),
        new FrameworkPropertyMetadata(0, OnStepChanged));

    /// <summary>The replay id of a table element an arrow can start or end at (0: none).</summary>
    public static readonly DependencyProperty RefProperty = DependencyProperty.RegisterAttached("Ref", typeof(int), typeof(MatchArrowLayer), new PropertyMetadata(0));

    /// <summary>What the marked element is, for where its anchor sits.</summary>
    public static readonly DependencyProperty AnchorProperty = DependencyProperty.RegisterAttached("Anchor", typeof(MatchArrowAnchor), typeof(MatchArrowLayer),
        new PropertyMetadata(MatchArrowAnchor.Minion));

    /// <summary>The step bar: an arrow with both ends on one side bends towards it.</summary>
    public static readonly DependencyProperty MiddleProperty = DependencyProperty.RegisterAttached("Middle", typeof(bool), typeof(MatchArrowLayer), new PropertyMetadata(false));

    // The arrow worked out after the last layout pass that followed a change, and what OnRender draws.
    private MatchArrow? _arrow;

    // A change is waiting for its layout pass (LayoutUpdated is listened to only meanwhile).
    private bool _pending;

    // The ends found by the last walk of the table: walked again only when the step's ends change, the layer's size
    // changes, or a kept end no longer carries its id or is hidden (review m4: not on every layout pass).
    private FrameworkElement? _from;
    private FrameworkElement? _to;
    private FrameworkElement? _middle;
    private (int From, int To, Size Size) _found;

    public MatchArrowLayer()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Invalidate();
        Loaded += (_, _) => Invalidate();
    }

    /// <summary>How many times the geometry was worked out (tests: at most twice a step, never on a layout pass alone).</summary>
    internal int Computed { get; private set; }

    public int Version { get => (int)GetValue(VersionProperty); set => SetValue(VersionProperty, value); }

    public int From { get => (int)GetValue(FromProperty); set => SetValue(FromProperty, value); }

    public int To { get => (int)GetValue(ToProperty); set => SetValue(ToProperty, value); }

    public bool IsAttack { get => (bool)GetValue(IsAttackProperty); set => SetValue(IsAttackProperty, value); }

    /// <summary>Your step (the accent) or the opponent's (the second accent).</summary>
    public bool Local { get => (bool)GetValue(LocalProperty); set => SetValue(LocalProperty, value); }

    public static int GetRef(DependencyObject element) => (int)element.GetValue(RefProperty);

    public static void SetRef(DependencyObject element, int value) => element.SetValue(RefProperty, value);

    public static MatchArrowAnchor GetAnchor(DependencyObject element) => (MatchArrowAnchor)element.GetValue(AnchorProperty);

    public static void SetAnchor(DependencyObject element, MatchArrowAnchor value) => element.SetValue(AnchorProperty, value);

    public static bool GetMiddle(DependencyObject element) => (bool)element.GetValue(MiddleProperty);

    public static void SetMiddle(DependencyObject element, bool value) => element.SetValue(MiddleProperty, value);

    private static void OnStepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((MatchArrowLayer)d).Invalidate();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
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
        // Not only while IsVisible: renders and tests lay the view out off screen.
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
        if (From == 0 || To == 0 || From == To || VisualTreeHelper.GetParent(this) is not FrameworkElement parent)
        {
            return null;
        }

        if (_found != (From, To, RenderSize) || !Holds(_from, From) || !Holds(_to, To) || (_middle is not null && VisualTreeHelper.GetParent(_middle) is null))
        {
            FrameworkElement? found = null, target = null, bar = null;
            Find(parent, ref found, ref target, ref bar);
            (_from, _to, _middle, _found) = (found, target, bar, (From, To, RenderSize));
        }

        var (from, to, middle) = (_from, _to, _middle);
        if (from is null || to is null)
        {
            return null;
        }

        var middleY = middle is null ? ActualHeight / 2 : Place(middle).Y + middle.ActualHeight / 2;
        return MatchArrowLayout.Build(AnchorOf(from), GetAnchor(from), AnchorOf(to), GetAnchor(to), middleY, IsAttack, Local);
    }

    /// <summary>A kept end still shows the element of that id.</summary>
    private static bool Holds(FrameworkElement? element, int id) =>
        element is { Visibility: Visibility.Visible } && GetRef(element) == id && VisualTreeHelper.GetParent(element) is not null;

    private void Find(DependencyObject node, ref FrameworkElement? from, ref FrameworkElement? to, ref FrameworkElement? middle)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            // Visibility, not IsVisible: the walk skips a collapsed branch whole, and it works off screen too (renders, tests).
            if (VisualTreeHelper.GetChild(node, i) is not FrameworkElement child || ReferenceEquals(child, this) || child.Visibility != Visibility.Visible)
            {
                continue;
            }

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

            Find(child, ref from, ref to, ref middle);
        }
    }

    // The ends are the layer's siblings' descendants, not its own: a transform through their common parent.
    private Point Place(FrameworkElement element) => element.TransformToVisual(this).Transform(default);

    private MatchArrowPoint AnchorOf(FrameworkElement element)
    {
        var at = Place(element);
        return MatchArrowLayout.Anchor(GetAnchor(element), at.X, at.Y, element.ActualWidth, element.ActualHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_arrow is not { } arrow || TryFindResource(MatchArrowLayout.StrokeBrush(arrow.Local)) is not Brush stroke
            || TryFindResource(MatchArrowLayout.ShadeBrush) is not Brush shade)
        {
            return;
        }

        var shaft = Shaft(arrow);
        var head = Head(arrow, closed: arrow.Attack);
        if (arrow.Attack)
        {
            drawingContext.DrawGeometry(null, Pen(shade, MatchArrowLayout.AttackShade), shaft);
            drawingContext.DrawGeometry(null, Pen(shade, MatchArrowLayout.AttackHeadShade * 2), head);
            drawingContext.DrawGeometry(null, Pen(stroke, MatchArrowLayout.AttackStroke), shaft);
            drawingContext.DrawGeometry(stroke, null, head);
        }
        else
        {
            var dashed = Pen(stroke, MatchArrowLayout.PlayStroke);
            dashed.DashStyle = new DashStyle([MatchArrowLayout.PlayDash, MatchArrowLayout.PlayGap], 0);
            dashed.DashCap = PenLineCap.Round;
            drawingContext.DrawGeometry(null, Pen(shade, MatchArrowLayout.PlayShade), shaft);
            drawingContext.DrawGeometry(null, Pen(shade, MatchArrowLayout.PlayShade), head);
            drawingContext.DrawGeometry(null, dashed, shaft);
            drawingContext.DrawGeometry(null, Pen(stroke, MatchArrowLayout.PlayStroke), head);
        }
    }

    private static Pen Pen(Brush brush, double thickness) =>
        new(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    private static Point P(MatchArrowPoint point) => new(point.X, point.Y);

    private static StreamGeometry Shaft(MatchArrow arrow)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(arrow.Start), isFilled: false, isClosed: false);
            if (arrow.Control is { } control)
            {
                context.QuadraticBezierTo(P(control), P(arrow.ShaftEnd), isStroked: true, isSmoothJoin: true);
            }
            else
            {
                context.LineTo(P(arrow.ShaftEnd), isStroked: true, isSmoothJoin: false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>The head: a closed triangle (an attack) or the two arms of a chevron through the tip.</summary>
    private static StreamGeometry Head(MatchArrow arrow, bool closed)
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(P(arrow.HeadLeft), isFilled: closed, isClosed: closed);
            context.LineTo(P(arrow.Tip), isStroked: true, isSmoothJoin: true);
            context.LineTo(P(arrow.HeadRight), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }
}
