using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a hovered card in hand), often over the board, so it lets
/// clicks through to the game on every desktop, like the other click-through panels: the overlay host reads the pointer
/// and mouse buttons through its platform layer and passes them in (<see cref="OverlayView.UpdateCardPoolPointer"/>).
/// With <see cref="Anchored"/> it sits in a Canvas over the whole surface and stands next to its card.
/// It fades in when it appears or its pool changes (<see cref="Reveal"/>).
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private static readonly TimeSpan RevealTime = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherTimer _reveal = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _revealClock = new();
    private OverlayViewModel? _subscribed;
    private bool _shown;
    private string _revealKey = string.Empty;

    public CardPoolPanel()
    {
        InitializeComponent();
        _reveal.Tick += (_, _) => StepReveal();
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
            case nameof(OverlayViewModel.CardPoolTitle) or nameof(OverlayViewModel.CardPoolPhrase) or nameof(OverlayViewModel.CardPoolSource):
                if (_shown && RevealKey() != _revealKey)
                {
                    Reveal();
                }

                break;
        }
    }

    private string RevealKey() => DataContext is OverlayViewModel vm ? $"{vm.CardPoolSource}|{vm.CardPoolTitle}|{vm.CardPoolPhrase}" : string.Empty;

    /// <summary>
    /// A short fade (ease-out, opacity only) when the panel appears or shows another pool; only with the Windows
    /// animation setting on, like the pool browser. The position is set before, so the panel never slides.
    /// </summary>
    private void Reveal()
    {
        _revealKey = RevealKey();
        if (!Motion || !PoolInput.AnimationsOn())
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
    }
}
