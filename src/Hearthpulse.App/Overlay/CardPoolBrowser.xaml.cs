using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// The pool browser: every card of the pool in a grid, mechanic filters, a draggable right edge. The overlay never takes
/// keyboard focus (it would steal it from the game), so Esc is read from the keyboard state while the browser is open
/// and Hearthstone or this app is in front. Mouse-wheel scrolling glides to its target when Windows animations are on.
/// </summary>
public partial class CardPoolBrowser : UserControl
{
    private readonly DispatcherTimer _escape = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _escapeDown;
    private ScrollViewer? _scroller;
    private double _scrollTarget;
    private bool _gliding;

    public CardPoolBrowser()
    {
        InitializeComponent();
        _escape.Tick += (_, _) => PollEscape();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _escapeDown = true; // a key held while opening does not count
                _escape.Start();
            }
            else
            {
                _escape.Stop();
            }
        };
    }

    private OverlayViewModel? ViewModel => DataContext as OverlayViewModel;

    private void PollEscape()
    {
        var down = PoolInput.IsDown(PoolInput.Escape);
        if (down && !_escapeDown && ViewModel is { CardPoolBrowserOpen: true } vm && PoolInput.GameOrAppInFront())
        {
            vm.CloseCardPoolBrowser.Execute(null);
        }

        _escapeDown = down;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.CardPoolHeld = true;
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.CardPoolHeld = false;
        }
    }

    private void OnChipClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PoolChipModel chip })
        {
            ViewModel?.ToggleCardPoolMechanic.Execute(chip.Key);
            e.Handled = true;
        }
    }

    private void OnFiltersClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.ToggleCardPoolFilters.Execute(null);
        e.Handled = true;
    }

    private void OnResize(object sender, DragDeltaEventArgs e)
    {
        // The browser sits at the left edge: dragging its right edge right makes it wider.
        if (ViewModel is { } vm)
        {
            vm.CardPoolBrowserWidth += e.HorizontalChange;
        }
    }

    private void OnResized(object sender, DragCompletedEventArgs e) => ViewModel?.CommitCardPoolBrowserWidth();

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        e.Handled = true;
        if (!_gliding || !ReferenceEquals(scroller, _scroller))
        {
            _scroller = scroller;
            _scrollTarget = scroller.VerticalOffset;
        }

        _scrollTarget = Math.Clamp(_scrollTarget - e.Delta * 0.9, 0, scroller.ScrollableHeight);
        if (!_gliding)
        {
            _gliding = true;
            CompositionTarget.Rendering += Glide;
        }
    }

    private void Glide(object? sender, EventArgs e)
    {
        if (_scroller is not { } scroller)
        {
            StopGlide();
            return;
        }

        var offset = scroller.VerticalOffset;
        var step = (_scrollTarget - offset) * 0.22;
        if (Math.Abs(_scrollTarget - offset) < 0.75)
        {
            scroller.ScrollToVerticalOffset(_scrollTarget);
            StopGlide();
            return;
        }

        scroller.ScrollToVerticalOffset(offset + step);
    }

    private void StopGlide()
    {
        _gliding = false;
        CompositionTarget.Rendering -= Glide;
    }
}
