using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.UI.Infrastructure;

namespace Hearthpulse.UI.Controls;

/// <summary>Where the hover preview opens relative to the hovered element.</summary>
public enum CardPreviewPlacement
{
    /// <summary>Beside the panel that holds the element, on the side facing the screen centre (deck and counter rows).</summary>
    Beside,

    /// <summary>Under the element itself (small marks along the top of the screen).</summary>
    Below,
}

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

    static CardPreview()
    {
        CardIdProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            Hook(c);
            // Fetch the render while the list is on screen, so the first hover does not wait for the download.
            if (e.NewValue is string { Length: > 0 } cardId)
            {
                _ = CardArt.GetAsync(ArtKind.Card, cardId);
            }
        });
        CaptionProperty.Changed.AddClassHandler<Control>((c, _) => Hook(c));
    }

    public static string? GetCardId(Control c) => c.GetValue(CardIdProperty);

    public static void SetCardId(Control c, string? value) => c.SetValue(CardIdProperty, value);

    public static string? GetCaption(Control c) => c.GetValue(CaptionProperty);

    public static void SetCaption(Control c, string? value) => c.SetValue(CaptionProperty, value);

    public static CardPreviewPlacement GetPlacement(Control c) => c.GetValue(PlacementProperty);

    public static void SetPlacement(Control c, CardPreviewPlacement value) => c.SetValue(PlacementProperty, value);

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
/// it sits on top). It never takes the pointer. The preview is placed beside the hovered control's panel, so it never
/// covers the row being read, and is pulled back inside the canvas near the screen edges.
/// </summary>
public sealed class CardPreviewHost : Canvas
{
    private const double CardWidth = 236;
    private const double CardHeight = CardWidth * 388 / 256;
    private const double Gap = 10;

    private static readonly List<WeakReference<CardPreviewHost>> Hosts = new();

    private readonly StackPanel _frame;
    private readonly Border _captionPlate;
    private readonly TextBlock _caption;
    private readonly Image _image;
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
        _frame = new StackPanel { Width = CardWidth, IsVisible = false };
        _frame.Children.Add(_captionPlate);
        _frame.Children.Add(_image);
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
        _image.IsVisible = !string.IsNullOrEmpty(cardId);
        CardArt.SetCard(_image, cardId);
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
        var row = new Rect(control.Bounds.Size).TransformToAABB(toHost);
        double left, top;
        if (CardPreview.GetPlacement(control) == CardPreviewPlacement.Below)
        {
            left = row.X + (row.Width / 2) - (size.Width / 2);
            top = row.Bottom + Gap;
        }
        else
        {
            var panel = PanelOf(control, surface);
            var anchor = panel.TransformToVisual(this) is { } toAnchor ? new Rect(panel.Bounds.Size).TransformToAABB(toAnchor) : row;
            var onRight = anchor.X + (anchor.Width / 2) > Bounds.Width / 2;
            left = onRight ? anchor.X - size.Width - Gap : anchor.Right + Gap;
            top = row.Y + (row.Height / 2) - (size.Height / 2);
        }

        SetLeft(_frame, Math.Clamp(left, 4, Math.Max(4, Bounds.Width - size.Width - 4)));
        SetTop(_frame, Math.Clamp(top, 4, Math.Max(4, Bounds.Height - size.Height - 4)));
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
