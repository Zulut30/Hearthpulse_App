using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a play, a hovered card), often over the board, so it lets
/// clicks through to the game: whether the mouse is over it, and a right-click on it (opens or closes the pool browser),
/// are read from the mouse state while it is shown. While the mouse is over it the pool stays up even when what
/// triggered it is gone.
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _rightDown = true;

    public CardPoolPanel()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _rightDown = true; // a button held while the panel appears does not count
                _poll.Start();
            }
            else
            {
                _poll.Stop();
                if (ViewModel is { CardPoolHeld: true } vm)
                {
                    vm.CardPoolHeld = false;
                }
            }
        };
    }

    private OverlayViewModel? ViewModel => DataContext as OverlayViewModel;

    private void Poll()
    {
        if (ViewModel is not { } vm || PresentationSource.FromVisual(Card) is null || PoolInput.Cursor() is not { } cursor)
        {
            return;
        }

        var point = Card.PointFromScreen(new Point(cursor.X, cursor.Y));
        var over = point.X >= 0 && point.Y >= 0 && point.X <= Card.ActualWidth && point.Y <= Card.ActualHeight;
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
}
