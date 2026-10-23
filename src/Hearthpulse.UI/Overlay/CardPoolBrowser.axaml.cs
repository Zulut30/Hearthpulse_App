using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The pool browser: every card of the pool in a grid, mechanic filters, a draggable left edge. The overlay does not
/// take keyboard focus from the game, so on Windows Esc is read from the keyboard state while the browser is open
/// (elsewhere a key press reaches the view when it has focus). Mouse-wheel scrolling glides to its target.
/// </summary>
public partial class CardPoolBrowser : UserControl
{
    private const int EscapeKey = 0x1B;

    private readonly DispatcherTimer _escape = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _glide = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private bool _escapeDown;
    private double _scrollTarget;

    public CardPoolBrowser()
    {
        InitializeComponent();
        _escape.Tick += (_, _) => PollEscape();
        _glide.Tick += (_, _) => Glide();
        Scroller.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        if (OperatingSystem.IsWindows())
        {
            AttachedToVisualTree += (_, _) => _escape.Start();
            DetachedFromVisualTree += (_, _) => _escape.Stop();
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

    private void PollEscape()
    {
        if (ViewModel is not { CardPoolBrowserOpen: true } vm)
        {
            _escapeDown = true; // a key held while the browser opens does not count
            return;
        }

        var down = (GetAsyncKeyState(EscapeKey) & 0x8000) != 0;
        if (down && !_escapeDown)
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
            ViewModel?.ToggleCardPoolMechanic.Execute(chip.Key);
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
        // The browser sits at the right edge: dragging its left edge left makes it wider.
        if (ViewModel is { } vm)
        {
            vm.CardPoolBrowserWidth -= e.Vector.X;
        }
    }

    private void OnResized(object? sender, VectorEventArgs e) => ViewModel?.CommitCardPoolBrowserWidth();

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
