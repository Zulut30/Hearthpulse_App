using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>
/// Hover preview of a whole card. Any control sets <c>CardPreview.CardId</c> (and optionally <c>Caption</c>); while
/// the pointer is over it, the <see cref="CardPreviewHost"/> of the same top level shows the card render next to it.
/// One preview per top level: moving from row to row swaps the card in place instead of closing and reopening it, so
/// the preview does not flicker. Controls inside the click-through overlay also need <c>OverlayHit.Interactive</c>.
/// </summary>
public static class CardPreview
{
    public static readonly AttachedProperty<string?> CardIdProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("CardId", typeof(CardPreview));

    public static readonly AttachedProperty<string?> CaptionProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Caption", typeof(CardPreview));

    public static readonly AttachedProperty<CardPreviewPlacement> PlacementProperty =
        AvaloniaProperty.RegisterAttached<Control, CardPreviewPlacement>("Placement", typeof(CardPreview));

    /// <summary>The card is a Battlegrounds one (minion or tavern spell): its Battlegrounds render, without a mana cost.</summary>
    public static readonly AttachedProperty<bool> BattlegroundsProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Battlegrounds", typeof(CardPreview));

    static CardPreview()
    {
        // Renders load on the first hover: fetching one for every row would crowd out the tiles' downloads and cache.
        CardIdProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
        CaptionProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
    }

    public static string? GetCardId(Control c) => c.GetValue(CardIdProperty);

    public static void SetCardId(Control c, string? value) => c.SetValue(CardIdProperty, value);

    public static string? GetCaption(Control c) => c.GetValue(CaptionProperty);

    public static void SetCaption(Control c, string? value) => c.SetValue(CaptionProperty, value);

    public static CardPreviewPlacement GetPlacement(Control c) => c.GetValue(PlacementProperty);

    public static void SetPlacement(Control c, CardPreviewPlacement value) => c.SetValue(PlacementProperty, value);

    public static bool GetBattlegrounds(Control c) => c.GetValue(BattlegroundsProperty);

    public static void SetBattlegrounds(Control c, bool value) => c.SetValue(BattlegroundsProperty, value);

    /// <summary>Whether the control has anything to preview.</summary>
    public static bool HasPreview(Control c) => !string.IsNullOrEmpty(GetCardId(c)) || !string.IsNullOrEmpty(GetCaption(c));

    private static void Hook(Control control)
    {
        control.PointerEntered -= OnEnter;
        control.PointerExited -= OnLeave;
        control.PointerEntered += OnEnter;
        control.PointerExited += OnLeave;
    }

    private static void OnEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control control && HasPreview(control))
        {
            CardPreviewHost.For(control)?.Show(control);
        }
    }

    private static void OnLeave(object? sender, PointerEventArgs e)
    {
        if (sender is Control control)
        {
            CardPreviewHost.For(control)?.HideSoon(control);
        }
    }
}

/// <summary>
/// The layer that draws the hover preview: a transparent canvas laid over the whole overlay surface (last child, so
/// it sits on top). It never takes the pointer. The preview is placed beside the hovered control's panel (in a window
/// page: beside the row's list, at the row), so it never covers the row being read, and is pulled back inside the
/// canvas near the window edges.
/// </summary>
public sealed class CardPreviewHost : Canvas
{
    private const double CardWidth = 236;
    private const double CardHeight = CardWidth * 388 / 256;

    private static readonly List<WeakReference<CardPreviewHost>> Hosts = new();

    private readonly StackPanel _frame;
    private readonly Border _captionPlate;
    private readonly TextBlock _caption;
    private readonly Image _image;
    private readonly Image _bgImage;
    private readonly DispatcherTimer _hide;
    private Control? _target;

    public CardPreviewHost()
    {
        IsHitTestVisible = false;
        // The render is a card shape on a transparent square, so it needs no frame; the caption gets its own plate.
        _caption = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _captionPlate = new Border { Padding = new Thickness(9, 6, 9, 7), Child = _caption };
        _captionPlate.Classes.Add("overlayCard");
        _image = new Image { Width = CardWidth, Height = CardHeight, Stretch = Stretch.Uniform };
        // Battlegrounds cards have their own render; a second image keeps the two loads from racing on one source.
        _bgImage = new Image { Width = CardWidth, Height = CardHeight, Stretch = Stretch.Uniform, IsVisible = false };
        _frame = new StackPanel { Width = CardWidth, IsVisible = false };
        _frame.Children.Add(_captionPlate);
        _frame.Children.Add(_image);
        _frame.Children.Add(_bgImage);
        Children.Add(_frame);

        // A short grace period: the pointer crossing the gap between two rows must not close and reopen the preview.
        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _hide.Tick += (_, _) => Hide();
        AttachedToVisualTree += (_, _) => Register(this);
        DetachedFromVisualTree += (_, _) => Unregister(this);
    }

    /// <summary>The host under the same top level as <paramref name="control"/>, if there is one.</summary>
    public static CardPreviewHost? For(Visual control)
    {
        var root = RootOf(control);
        lock (Hosts)
        {
            foreach (var reference in Hosts)
            {
                if (reference.TryGetTarget(out var host) && RootOf(host) == root)
                {
                    return host;
                }
            }
        }

        return null;
    }

    private static Visual RootOf(Visual visual)
    {
        while (visual.GetVisualParent() is { } parent)
        {
            visual = parent;
        }

        return visual;
    }

    /// <summary>Shows the preview for a control carrying <see cref="CardPreview"/> properties.</summary>
    public void Show(Control control)
    {
        _hide.Stop();
        _target = control;
        var cardId = CardPreview.GetCardId(control);
        var caption = CardPreview.GetCaption(control);
        _caption.Text = caption ?? string.Empty;
        _captionPlate.IsVisible = !string.IsNullOrEmpty(caption);
        var battlegrounds = CardPreview.GetBattlegrounds(control);
        _image.IsVisible = !string.IsNullOrEmpty(cardId) && !battlegrounds;
        _bgImage.IsVisible = !string.IsNullOrEmpty(cardId) && battlegrounds;
        CardArt.SetCard(_image, battlegrounds ? null : cardId);
        CardArt.SetBattlegroundsCard(_bgImage, battlegrounds ? cardId : null);
        _frame.IsVisible = true;
        Place(control);
    }

    /// <summary>Hides the preview shortly, unless another control takes it over first.</summary>
    public void HideSoon(Control control)
    {
        if (_target == control)
        {
            _hide.Stop();
            _hide.Start();
        }
    }

    /// <summary>Hides the preview now (the overlay went click-through or was hidden).</summary>
    public void Hide()
    {
        _hide.Stop();
        _target = null;
        _frame.IsVisible = false;
    }

    /// <summary>
    /// Shows the preview for the first visible control under <paramref name="root"/> that has one and passes
    /// <paramref name="filter"/> (off-screen renders, which have no pointer). Returns false when there is none.
    /// </summary>
    public bool ShowFirst(Visual root, Func<Control, bool>? filter = null)
    {
        var control = root.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.IsEffectivelyVisible && CardPreview.HasPreview(c) && (filter is null || filter(c)));
        if (control is null)
        {
            return false;
        }

        Show(control);
        return true;
    }

    private void Place(Control control)
    {
        if (this.GetVisualParent() is not { } surface || !surface.IsVisualAncestorOf(control)
            || control.TransformToVisual(this) is not { } toHost)
        {
            return;
        }

        _frame.Measure(Size.Infinity);
        var size = _frame.DesiredSize;
        var row = Box(new Rect(control.Bounds.Size).TransformToAABB(toHost));
        Visual? panel = null;
        var (left, top) = CardPreviewLayout.Place(CardPreview.GetPlacement(control), row, size.Width, size.Height, Bounds.Width, Bounds.Height,
            () => BoundsIn(panel ??= PanelOf(control, surface)) ?? row,
            () => ListOf(control, panel ??= PanelOf(control, surface)) is { } owner && BoundsIn(owner) is { } list ? list : row);
        SetLeft(_frame, left);
        SetTop(_frame, top);
    }

    /// <summary>Where <paramref name="visual"/> is on this host; null when it cannot be mapped onto it.</summary>
    private PreviewRect? BoundsIn(Visual visual) =>
        visual.TransformToVisual(this) is { } to ? Box(new Rect(visual.Bounds.Size).TransformToAABB(to)) : null;

    private static PreviewRect Box(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>The nearest list (items control) holding <paramref name="control"/> below <paramref name="panel"/>, if any.</summary>
    private static Visual? ListOf(Control control, Visual panel)
    {
        Visual node = control;
        while (node.GetVisualParent() is { } parent && parent != panel)
        {
            if (parent is ItemsControl list)
            {
                return list;
            }

            node = parent;
        }

        return null;
    }

    /// <summary>The control's top-level panel: its ancestor that sits directly on the same surface as this host.</summary>
    private static Visual PanelOf(Control control, Visual surface)
    {
        Visual node = control;
        while (node.GetVisualParent() is { } parent && parent != surface)
        {
            node = parent;
        }

        return node;
    }

    private static void Register(CardPreviewHost host)
    {
        lock (Hosts)
        {
            Hosts.RemoveAll(r => !r.TryGetTarget(out var h) || h == host);
            Hosts.Add(new WeakReference<CardPreviewHost>(host));
        }
    }

    private static void Unregister(CardPreviewHost host)
    {
        lock (Hosts)
        {
            Hosts.RemoveAll(r => !r.TryGetTarget(out var h) || h == host);
        }
    }
}
