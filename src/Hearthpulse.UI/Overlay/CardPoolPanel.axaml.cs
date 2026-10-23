using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a play, a hovered card), often over the board, so on Windows
/// it lets clicks through to the game: whether the mouse is over it, and a right-click on it (opens or closes the pool
/// browser), are read from the mouse state while it is shown. Elsewhere it takes the pointer as usual. While the mouse
/// is over it the pool stays up even when what triggered it is gone.
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _rightDown = true;

    public CardPoolPanel()
    {
        InitializeComponent();
        if (OperatingSystem.IsWindows())
        {
            _poll.Tick += (_, _) => Poll();
            AttachedToVisualTree += (_, _) => _poll.Start();
            DetachedFromVisualTree += (_, _) => _poll.Stop();
        }
        else
        {
            OverlayHit.SetInteractive(Card, true);
        }
    }

    private OverlayViewModel? ViewModel => DataContext as OverlayViewModel;

    private void Poll()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        if (!vm.ShowCardPoolPanel || PoolInput.Cursor() is not { } cursor)
        {
            _rightDown = true; // a button held while the panel appears does not count
            if (vm.CardPoolHeld)
            {
                vm.CardPoolHeld = false;
            }

            return;
        }

        var point = Card.PointToClient(new PixelPoint(cursor.X, cursor.Y));
        var over = point.X >= 0 && point.Y >= 0 && point.X <= Card.Bounds.Width && point.Y <= Card.Bounds.Height;
        if (vm.CardPoolHeld != over)
        {
            vm.CardPoolHeld = over;
        }

        var right = PoolInput.IsDown(PoolInput.RightButton);
        if (right && !_rightDown && over)
        {
            vm.OpenCardPoolBrowser.Execute(null);
        }

        _rightDown = right;
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

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            ViewModel?.OpenCardPoolBrowser.Execute(null);
            e.Handled = true;
        }
    }
}
