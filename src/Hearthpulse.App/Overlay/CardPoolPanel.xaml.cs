using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hearthpulse.App.Controls;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a hovered card in hand), often over the board, so it lets
/// clicks through to the game: the pointer and mouse buttons are read while it is shown and handed to
/// <see cref="OverlayViewModel.UpdateCardPoolPointer(bool, bool, bool)"/> (hold the pool, right-click opens the pool
/// browser). With <see cref="Anchored"/> it sits in a Canvas over the whole surface and stands next to its card.
/// It fades in when it appears or its pool changes (<see cref="Reveal"/>).
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private static readonly TimeSpan RevealTime = TimeSpan.FromMilliseconds(120);

    // Input priority: the poll must not starve behind rendering, or a short right-click falls between two ticks.
    private readonly DispatcherTimer _poll = new(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(40) };
    private OverlayViewModel? _subscribed;
    private string _revealKey = string.Empty;
    private FrameworkElement? _previewRow;

    public CardPoolPanel()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                // A right-click from before the panel showed must not count as one on it.
                PoolInput.WasPressed(PoolInput.RightButton);
                _poll.Start();
                // Measured and placed before the first frame, so the panel never shows at its previous spot.
                UpdateLayout();
                Place();
                Reveal();
            }
            else
            {
                _poll.Stop();
                ViewModel?.UpdateCardPoolPointer(false, false, false);
            }
        };
        SizeChanged += (_, _) => Place();
        Loaded += (_, _) =>
        {
            // The game window was resized: the surface (and the card's spot on it) changes, the panel does not.
            if (Anchored && Parent is FrameworkElement layer)
            {
                layer.SizeChanged += (_, _) => Place();
            }
        };
        DataContextChanged += (_, _) => Subscribe();
    }

    /// <summary>The panel is the popup next to its card (in a Canvas over the surface), not the movable panel's content.</summary>
    public bool Anchored { get; set; }

    private OverlayViewModel? ViewModel => DataContext as OverlayViewModel;

    private void Subscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged -= OnViewModelChanged;
        }

        _subscribed = ViewModel;
        if (_subscribed is not null)
        {
            _subscribed.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.CardPoolAnchor):
                Place();
                break;
            case nameof(OverlayViewModel.CardPoolTitle) or nameof(OverlayViewModel.CardPoolPhrase) or nameof(OverlayViewModel.CardPoolSource):
                if (IsVisible && RevealKey() != _revealKey)
                {
                    Reveal();
                }

                break;
        }
    }

    private string RevealKey() => ViewModel is { } vm ? $"{vm.CardPoolSource}|{vm.CardPoolTitle}|{vm.CardPoolPhrase}" : string.Empty;

    /// <summary>
    /// A short fade (ease-out, opacity only) when the panel appears or shows another pool; nothing when Windows'
    /// animation setting is off. The position is set before, so the panel never slides.
    /// </summary>
    private void Reveal()
    {
        _revealKey = RevealKey();
        if (!SystemParameters.ClientAreaAnimation)
        {
            Card.BeginAnimation(OpacityProperty, null);
            return;
        }

        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, RevealTime) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>Stands next to the card: <see cref="PoolPlacement"/> in surface units, from the panel's measured size.</summary>
    private void Place()
    {
        if (!Anchored || !IsVisible || ViewModel is not { CardPoolAnchor: { } anchor } vm || Parent is not FrameworkElement { ActualWidth: > 0 } layer
            || ActualWidth <= 0)
        {
            return;
        }

        var (x, y) = PoolPlacement.PlaceOnSurface(anchor, layer.ActualWidth, vm.CardPoolOverlayScale, ActualWidth, ActualHeight);
        Canvas.SetLeft(this, x);
        Canvas.SetTop(this, y);
    }

    private void Poll()
    {
        if (ViewModel is not { } vm || PresentationSource.FromVisual(Card) is null || PoolInput.Cursor() is not { } cursor)
        {
            return;
        }

        var point = Card.PointFromScreen(new Point(cursor.X, cursor.Y));
        var over = point.X >= 0 && point.Y >= 0 && point.X <= Card.ActualWidth && point.Y <= Card.ActualHeight;
        vm.UpdateCardPoolPointer(over, PoolInput.IsDown(PoolInput.LeftButton), PoolInput.WasPressed(PoolInput.RightButton));
        PreviewRowAt(over ? new Point(cursor.X, cursor.Y) : null);
    }

    /// <summary>
    /// The full card of the row under the pointer (rows of this game, related cards, Excavate): the panel takes no mouse,
    /// so the poll does what a hover would. The preview leaves with the pointer.
    /// </summary>
    private void PreviewRowAt(Point? screen)
    {
        var row = screen is { } at ? RowAt(Card, at) : null;
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

    private static FrameworkElement? RowAt(DependencyObject node, Point screen)
    {
        if (node is FrameworkElement { IsVisible: true } element && CardPreview.HasPreview(element) && element.ActualWidth > 0)
        {
            var local = element.PointFromScreen(screen);
            return local.X >= 0 && local.Y >= 0 && local.X <= element.ActualWidth && local.Y <= element.ActualHeight ? element : null;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (RowAt(VisualTreeHelper.GetChild(node, i), screen) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
