using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The pool browser: every card of the pool in a grid, mechanic filters, a draggable right edge. The overlay does not
/// take keyboard focus from the game, so on Windows Esc is read from the keyboard state while the browser is open and
/// Hearthstone or this app is in front (elsewhere a key press reaches the view when it has focus). Mouse-wheel
/// scrolling glides to its target when the Windows animation setting is on.
/// </summary>
public partial class CardPoolBrowser : UserControl
{
    private readonly DispatcherTimer _escape = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _glide = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _escapeDown;
    private OverlayViewModel? _watched;
    private double _scrollTarget;

    public CardPoolBrowser()
    {
        InitializeComponent();
        _escape.Tick += (_, _) => PollEscape();
        _glide.Tick += (_, _) => Glide();
        // A hidden scroller never reaches the glide's target: the 16 ms timer would run on and keep this browser alive.
        DetachedFromVisualTree += (_, _) => _glide.Stop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && !IsVisible)
            {
                _glide.Stop();
            }
        };
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        if (OperatingSystem.IsWindows())
        {
            // Esc is polled only while the browser is open.
            DataContextChanged += (_, _) =>
            {
                if (_watched is not null)
                {
                    _watched.PropertyChanged -= OnViewModelChanged;
                }

                _watched = DataContext as OverlayViewModel;
                if (_watched is not null)
                {
                    _watched.PropertyChanged += OnViewModelChanged;
                }
            };
        }

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                ViewModel?.CloseCardPoolBrowser.Execute(null);
                e.Handled = true;
            }
        };
    }

    private OverlayViewModel? ViewModel => DataContext as OverlayViewModel;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OverlayViewModel.CardPoolBrowserOpen) || _watched is null)
        {
            return;
        }

        if (_watched.CardPoolBrowserOpen)
        {
            _escapeDown = true; // a key held while the browser opens does not count
            _escape.Start();
        }
        else
        {
            _escape.Stop();
        }
    }

    private void PollEscape()
    {
        if (ViewModel is not { CardPoolBrowserOpen: true } vm)
        {
            return;
        }

        var down = PoolInput.IsDown(PoolInput.Escape);
        if (down && !_escapeDown && PoolInput.GameOrAppInFront())
        {
            vm.CloseCardPoolBrowser.Execute(null);
        }

        _escapeDown = down;
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.CardPoolHeld = true;
        }
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.CardPoolHeld = false;
        }
    }

    private void OnChipReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && sender is Control { DataContext: PoolChipModel chip })
        {
            (chip.Section ? ViewModel?.ToggleCardPoolSection : ViewModel?.ToggleCardPoolMechanic)?.Execute(chip.Key);
            e.Handled = true;
        }
    }

    private void OnFiltersReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            ViewModel?.ToggleCardPoolFilters.Execute(null);
            e.Handled = true;
        }
    }

    private void OnResize(object? sender, VectorEventArgs e)
    {
        // The browser sits at the left edge: dragging its right edge right makes it wider.
        if (ViewModel is { } vm)
        {
            vm.CardPoolBrowserWidth += e.Vector.X;
        }
    }

    private void OnResized(object? sender, VectorEventArgs e) => ViewModel?.CommitCardPoolBrowserWidth();

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Same rule as the WPF app: animate only when the system's animation setting is on.
        if (!PoolInput.AnimationsOn())
        {
            return;
        }

        e.Handled = true;
        var max = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        if (!_glide.IsEnabled)
        {
            _scrollTarget = Scroller.Offset.Y;
        }

        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta.Y * 108, 0, max);
        _glide.Start();
    }

    private void Glide()
    {
        var y = Scroller.Offset.Y;
        if (Math.Abs(_scrollTarget - y) < 0.75)
        {
            Scroller.Offset = new Vector(Scroller.Offset.X, _scrollTarget);
            _glide.Stop();
            return;
        }

        Scroller.Offset = new Vector(Scroller.Offset.X, y + (_scrollTarget - y) * 0.22);
    }
}
