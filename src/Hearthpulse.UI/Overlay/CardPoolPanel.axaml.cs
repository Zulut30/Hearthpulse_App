using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a play, a hovered card), often over the board, so it lets
/// clicks through to the game on every desktop, like the other click-through panels: the overlay host reads the pointer
/// and mouse buttons through its platform layer and passes them in (<see cref="OverlayView.UpdateCardPoolPointer"/>);
/// chart tabs and columns react to hover (<see cref="OverlayViewModel.HoverCardPoolChart"/>).
/// With <see cref="Anchored"/> it sits in a Canvas over the whole surface and stands next to its card.
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private OverlayViewModel? _subscribed;
    private bool _pointerWasOver;

    public CardPoolPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Place();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                Place();
            }
        };
    }

    /// <summary>The panel is the popup next to its card (in a Canvas over the surface), not the movable panel's content.</summary>
    public bool Anchored { get; set; }

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
        if (Anchored && Parent is Control layer)
        {
            layer.SizeChanged += (_, _) => Place();
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.CardPoolAnchor))
        {
            Place();
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

        var (x, y) = PoolPlacement.PlaceOnSurface(anchor, layer.Bounds.Width, vm.CardPoolOverlayScale, Bounds.Width, Bounds.Height);
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

        // The chart reacts to hover: the element under the pointer belongs to a tab or a column through its DataContext.
        string? stat = null;
        int? bar = null;
        for (var node = over ? Card.InputHitTest(local!.Value) as Visual : null; node is not null && node != Card; node = node.GetVisualParent())
        {
            if (node is Control { DataContext: PoolChartTab tab })
            {
                stat = tab.Stat;
                break;
            }

            if (node is Control { DataContext: PoolChartBar column })
            {
                bar = column.Index;
                break;
            }
        }

        // Off the panel only the moment the pointer leaves it clears the hovered column.
        if (over || _pointerWasOver)
        {
            vm.HoverCardPoolChart(stat, bar);
        }

        _pointerWasOver = over;
    }
}
