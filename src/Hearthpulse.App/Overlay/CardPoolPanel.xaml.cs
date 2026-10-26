using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a hovered card in hand), often over the board, so it lets
/// clicks through to the game: the pointer and mouse buttons are read while it is shown and handed to
/// <see cref="OverlayViewModel.UpdateCardPoolPointer"/> (hold the pool, right-click opens the pool browser) and
/// <see cref="OverlayViewModel.HoverCardPoolChart"/> (chart tabs and columns react to hover).
/// With <see cref="Anchored"/> it sits in a Canvas over the whole surface and stands next to its card.
/// It fades in and rises a few units when it appears or its pool changes (<see cref="Reveal"/>).
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private static readonly TimeSpan RevealTime = TimeSpan.FromMilliseconds(170);

    /// <summary>How far below its spot the panel starts when it fades in, in overlay units.</summary>
    private const double RevealRise = 6;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private OverlayViewModel? _subscribed;
    private string _chartKey = string.Empty;
    private string _revealKey = string.Empty;
    private bool _pointerWasOver;

    public CardPoolPanel()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
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
                ViewModel?.HoverCardPoolChart(null, null);
                _pointerWasOver = false;
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
            case nameof(OverlayViewModel.CardPoolBars):
                GrowBars();
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
    /// A short fade and rise (ease-out) when the panel appears or shows another pool; nothing when Windows' animation
    /// setting is off. The position is set before, so only the panel itself moves the few units.
    /// </summary>
    private void Reveal()
    {
        _revealKey = RevealKey();
        if (!SystemParameters.ClientAreaAnimation)
        {
            Card.BeginAnimation(OpacityProperty, null);
            CardShift.BeginAnimation(TranslateTransform.YProperty, null);
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, RevealTime) { EasingFunction = ease });
        CardShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(RevealRise, 0, RevealTime) { EasingFunction = ease });
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

    /// <summary>A new pool or stat grows its bars from the axis; hovering a column does not. Only with Windows' animations on.</summary>
    private void GrowBars()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        var key = $"{vm.CardPoolTitle}|{vm.CardPoolPhrase}|{vm.CardPoolStat}";
        if (key == _chartKey)
        {
            return;
        }

        _chartKey = key;
        if (!IsVisible || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        var grow = new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        BarsScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void Poll()
    {
        if (ViewModel is not { } vm || PresentationSource.FromVisual(Card) is null || PoolInput.Cursor() is not { } cursor)
        {
            return;
        }

        var point = Card.PointFromScreen(new Point(cursor.X, cursor.Y));
        var over = point.X >= 0 && point.Y >= 0 && point.X <= Card.ActualWidth && point.Y <= Card.ActualHeight;
        vm.UpdateCardPoolPointer(over, PoolInput.IsDown(PoolInput.LeftButton), PoolInput.IsDown(PoolInput.RightButton));

        // The chart reacts to hover: the element under the pointer belongs to a tab or a column through its DataContext.
        string? stat = null;
        int? bar = null;
        for (var node = over ? Card.InputHitTest(point) as DependencyObject : null; node is not null && node != Card; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: PoolChartTab tab })
            {
                stat = tab.Stat;
                break;
            }

            if (node is FrameworkElement { DataContext: PoolChartBar column })
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
