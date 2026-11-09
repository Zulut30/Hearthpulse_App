using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Localization;
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

    /// <summary>
    /// False where the previewed card stands for a hidden one (the card that created a card in the opponent's hand):
    /// its related cards would be a guess at that card, so the preview stays one card.
    /// </summary>
    public static readonly AttachedProperty<bool> ShowRelatedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("ShowRelated", typeof(CardPreview), defaultValue: true);

    /// <summary>Related cards of a previewed card id; set once by the host, null = no related block.</summary>
    public static Func<string, IReadOnlyList<MatchListRow>>? Related { get; set; }

    public static bool GetShowRelated(Control c) => c.GetValue(ShowRelatedProperty);

    public static void SetShowRelated(Control c, bool value) => c.SetValue(ShowRelatedProperty, value);

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
/// canvas near the window edges. The card's related cards (<see cref="CardPreview.Related"/>) stand in a grid beside it,
/// on the side away from the row (<see cref="CardPreviewLayout.PlaceRelated"/>).
/// </summary>
public sealed class CardPreviewHost : Canvas
{
    private const double CardWidth = CardPreviewLayout.CardWidth;
    private const double CardHeight = CardPreviewLayout.CardHeight;

    private static readonly List<WeakReference<CardPreviewHost>> Hosts = new();

    private readonly StackPanel _frame;
    private readonly Border _captionPlate;
    private readonly TextBlock _caption;
    private readonly Image _image;
    private readonly Image _bgImage;
    private readonly DispatcherTimer _hide;
    private readonly StackPanel _related;
    private readonly Border _relatedPlate;
    private readonly TextBlock _relatedTitle;
    private readonly Canvas _relatedGrid;
    private readonly Image[] _relatedImages = new Image[CardPreviewLayout.MaxRelated];
    private IReadOnlyList<MatchListRow> _relatedRows = [];
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

        // The related cards: a "Related cards" plate, then the renders in a grid (fixed sizes: a missing render leaves
        // its place empty and the grid does not jump).
        _relatedTitle = new TextBlock { FontWeight = FontWeight.SemiBold };
        _relatedTitle.Bind(TextBlock.FontSizeProperty, _relatedTitle.GetResourceObservable("FontSizeLabel"));
        _relatedTitle.Bind(TextBlock.ForegroundProperty, _relatedTitle.GetResourceObservable("Muted"));
        _relatedPlate = new Border { Padding = new Thickness(8, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left, Child = _relatedTitle };
        _relatedPlate.Classes.Add("overlayCard");
        _relatedGrid = new Canvas { Margin = new Thickness(0, CardPreviewLayout.TightGap, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        for (var i = 0; i < _relatedImages.Length; i++)
        {
            _relatedImages[i] = new Image { Stretch = Stretch.Uniform };
            _relatedGrid.Children.Add(_relatedImages[i]);
        }

        _related = new StackPanel { IsVisible = false };
        _related.Children.Add(_relatedPlate);
        _related.Children.Add(_relatedGrid);
        Children.Add(_related);

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
        ShowRelated(battlegrounds || string.IsNullOrEmpty(cardId) || !CardPreview.GetShowRelated(control) ? null : cardId);
        Place(control);
    }

    /// <summary>The card ids in the related grid now (empty when it is hidden).</summary>
    internal IReadOnlyList<string> RelatedShown => _related.IsVisible ? _relatedRows.Select(r => r.CardId).ToList() : [];

    /// <summary>The related grid's width of one card now (0 when hidden).</summary>
    internal double RelatedCardWidth => _related.IsVisible ? _relatedImages[0].Width : 0;

    /// <summary>The related cards' renders for <paramref name="cardId"/>; none for a Battlegrounds card, too many or none.</summary>
    private void ShowRelated(string? cardId)
    {
        var rows = cardId is not null && CardPreview.Related is { } source ? source(cardId) : [];
        _relatedRows = rows.Count <= CardPreviewLayout.MaxRelated ? rows : [];
        _related.IsVisible = false;
        _relatedTitle.Text = Loc.T("Related.Title");
        for (var i = 0; i < _relatedImages.Length; i++)
        {
            CardArt.SetCard(_relatedImages[i], i < _relatedRows.Count ? _relatedRows[i].CardId : null);
        }
    }

    /// <summary>The grid beside the placed big card, or hidden when it has no room (the preview stays one card).</summary>
    private void PlaceRelated(PreviewRect row, double frameLeft, double frameTop)
    {
        _related.IsVisible = false;
        if (_relatedRows.Count == 0 || !_image.IsVisible)
        {
            return;
        }

        var captionHeight = _captionPlate.IsVisible ? _captionPlate.DesiredSize.Height : 0;
        var card = new PreviewRect(frameLeft, frameTop + captionHeight, CardWidth, CardHeight);
        _relatedPlate.Measure(Size.Infinity);
        // How large the host is drawn (the overlay surface scale; 1 in a window page).
        var scale = this.TransformToVisual(RootOf(this)) is { } toRoot ? toRoot.M11 : 1;
        if (CardPreviewLayout.PlaceRelated(_relatedRows.Count, card, row, Bounds.Width, Bounds.Height, _relatedPlate.DesiredSize.Height, scale) is not { } at)
        {
            return;
        }

        for (var i = 0; i < _relatedImages.Length; i++)
        {
            var image = _relatedImages[i];
            image.IsVisible = i < _relatedRows.Count;
            image.Width = at.CardWidth;
            image.Height = at.CardHeight;
            SetLeft(image, i % at.Columns * (at.CardWidth + CardPreviewLayout.TightGap));
            SetTop(image, i / at.Columns * (at.CardHeight + CardPreviewLayout.TightGap));
        }

        _relatedGrid.Width = (at.Columns * at.CardWidth) + ((at.Columns - 1) * CardPreviewLayout.TightGap);
        _relatedGrid.Height = (at.Rows * at.CardHeight) + ((at.Rows - 1) * CardPreviewLayout.TightGap);
        SetLeft(_related, at.Left);
        SetTop(_related, at.Top);
        _related.IsVisible = true;
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
        _related.IsVisible = false;
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
        PlaceRelated(row, left, top);
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
