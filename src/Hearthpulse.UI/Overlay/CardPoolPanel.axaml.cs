using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.UI.Controls;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a hovered card in hand), often over the board, so it lets
/// clicks through to the game on every desktop, like the other click-through panels: the overlay host reads the pointer
/// and mouse buttons through its platform layer and passes them in (<see cref="OverlayView.UpdateCardPoolPointer"/>).
/// With <see cref="Anchored"/> it sits in a Canvas over the whole surface and stands next to its card.
/// It fades in when it first appears (<see cref="Reveal"/>); another card's pool replaces it without a fade.
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private static readonly TimeSpan RevealTime = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherTimer _reveal = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _revealClock = new();
    private OverlayViewModel? _subscribed;
    private bool _shown;
    private Control? _previewRow;
    private readonly List<(Control Row, Rect Bounds)> _rows = [];
    private bool _rowsDirty = true;

    public CardPoolPanel()
    {
        InitializeComponent();
        ApplyAnchored();
        _reveal.Tick += (_, _) => StepReveal();
        // Any layout pass may move, add or hide a row: the cached rows are collected again on the next update over the panel.
        Card.LayoutUpdated += (_, _) => _rowsDirty = true;
        SizeChanged += (_, _) => Place();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                Place();
            }
        };
    }

    /// <summary>Off for --render-ui, which captures the overlay right after an update.</summary>
    public static bool Motion { get; set; } = true;

    /// <summary>The panel is the popup next to its card (in a Canvas over the surface), not the movable panel's content.</summary>
    public bool Anchored
    {
        get => _anchored;
        set
        {
            _anchored = value;
            ApplyAnchored();
        }
    }

    private bool _anchored;
    private double _floor = 1;

    /// <summary>
    /// The popup's header note leaves out the card's name (the card stands beside it) and its fill does not fade with the
    /// overlay opacity; the panel at its own spot names the card and fades like the others.
    /// </summary>
    private void ApplyAnchored()
    {
        var (shown, hidden, path) = _anchored ? (PopupNote, PanelNote, nameof(OverlayViewModel.CardPoolMeta)) : (PanelNote, PopupNote, nameof(OverlayViewModel.CardPoolSourceNote));
        hidden.IsVisible = false;
        shown.Bind(IsVisibleProperty, new Binding(path) { Converter = (IValueConverter)Resources["Truthy"]! });
        Card.Classes.Set("popup", _anchored);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnViewModelChanged;
        }

        _subscribed = DataContext as OverlayViewModel;
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnViewModelChanged;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The game window was resized: the surface (and the card's spot on it) changes, the panel does not.
        if (Anchored && _layer is null && Parent is Control layer)
        {
            _layer = layer;
            layer.SizeChanged += OnLayerSizeChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // The overlay is rebuilt or closed: the old layer must not keep this panel (and place it) after that.
        if (_layer is not null)
        {
            _layer.SizeChanged -= OnLayerSizeChanged;
            _layer = null;
        }
    }

    // The layer an anchored panel follows while it is in the tree.
    private Control? _layer;

    private void OnLayerSizeChanged(object? sender, SizeChangedEventArgs e) => Place();

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not OverlayViewModel vm)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.CardPoolAnchor):
                Place();
                break;
            case nameof(OverlayViewModel.ShowCardPoolPanel) or nameof(OverlayViewModel.ShowCardPoolPopup):
                // The movable panel hides its content through its own host, so the view model says when this one shows.
                var shown = Anchored ? vm.ShowCardPoolPopup : vm.ShowCardPoolPanel;
                if (shown && !_shown)
                {
                    Place();
                    Reveal();
                }

                _shown = shown;
                break;
        }
    }

    /// <summary>
    /// A short fade (ease-out, opacity only) when the panel appears; only with the Windows animation setting on, like
    /// the pool browser. The position is set before, so the panel never slides.
    /// </summary>
    private void Reveal()
    {
        if (!Motion || !Infrastructure.Motion.On)
        {
            _reveal.Stop();
            Card.Opacity = 1;
            return;
        }

        Card.Opacity = 0;
        _revealClock.Restart();
        _reveal.Start();
    }

    private void StepReveal()
    {
        var t = Math.Min(1, _revealClock.Elapsed / RevealTime);
        var eased = 1 - Math.Pow(1 - t, 3);
        Card.Opacity = eased;
        if (t >= 1)
        {
            _reveal.Stop();
        }
    }

    /// <summary>Stands next to the card: <see cref="PoolPlacement"/> in surface units, from the panel's measured size.</summary>
    public void Place()
    {
        if (!Anchored || !IsVisible || DataContext is not OverlayViewModel { CardPoolAnchor: { } anchor } vm || Parent is not Control { Bounds.Width: > 0 } layer
            || Bounds.Width <= 0)
        {
            return;
        }

        // Drawn no smaller than PoolPlacement.MinPopupScale of a game card, whatever the player's overlay scale.
        var floor = PoolPlacement.PopupFloor(vm.CardPoolOverlayScale);
        if (floor != _floor)
        {
            _floor = floor;
            RenderTransformOrigin = RelativePoint.TopLeft;
            RenderTransform = floor == 1 ? null : new ScaleTransform(floor, floor);
        }

        var (x, y) = PoolPlacement.PlaceOnSurface(anchor, layer.Bounds.Width, vm.CardPoolOverlayScale, Bounds.Width * floor, Bounds.Height * floor);
        Canvas.SetLeft(this, x);
        Canvas.SetTop(this, y);
    }

    /// <summary>The pointer in <paramref name="view"/> coordinates (null outside the overlay) and the mouse buttons.</summary>
    public void UpdatePointer(Visual view, Point? pointer, (bool Left, bool Right) buttons)
    {
        if (DataContext is not OverlayViewModel vm)
        {
            return;
        }

        var local = pointer is { } p && Card.IsEffectivelyVisible ? view.TranslatePoint(p, Card) : null;
        var over = local is { } q && q.X >= 0 && q.Y >= 0 && q.X <= Card.Bounds.Width && q.Y <= Card.Bounds.Height;
        vm.UpdateCardPoolPointer(over, buttons.Left, buttons.Right);
        PreviewRowAt(over ? local : null);
    }

    /// <summary>
    /// The full card of the row under the pointer (rows of this game, related cards, Excavate): the panel takes no mouse,
    /// so the pointer update does what a hover would. The preview leaves with the pointer.
    /// </summary>
    private void PreviewRowAt(Point? inCard)
    {
        var row = inCard is { } at ? RowAt(at) : null;
        if (row == _previewRow)
        {
            return;
        }

        if (_previewRow is not null)
        {
            CardPreviewHost.For(_previewRow)?.HideSoon(_previewRow);
        }

        _previewRow = row;
        if (row is not null)
        {
            CardPreviewHost.For(row)?.Show(row);
        }
    }

    /// <summary>
    /// The row with a preview at a point of <see cref="Card"/>. The rows and their bounds are collected once per layout
    /// pass (<see cref="_rowsDirty"/>), not by walking the panel's tree on every pointer update.
    /// </summary>
    private Control? RowAt(Point inCard)
    {
        if (_rowsDirty)
        {
            _rowsDirty = false;
            _rows.Clear();
            CollectRows(Card);
        }

        foreach (var (row, bounds) in _rows)
        {
            if (bounds.Contains(inCard))
            {
                return row;
            }
        }

        return null;
    }

    private void CollectRows(Visual node)
    {
        if (node is Control { IsEffectivelyVisible: true } control && CardPreview.HasPreview(control) && control.Bounds.Width > 0)
        {
            if (control.TranslatePoint(default, Card) is { } origin)
            {
                _rows.Add((control, new Rect(origin, control.Bounds.Size)));
            }

            return;
        }

        foreach (var child in node.GetVisualChildren())
        {
            CollectRows(child);
        }
    }
}
