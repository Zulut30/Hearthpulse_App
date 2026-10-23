using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// The card pool panel. It appears on its own (Discover, a play, a hovered card), often over the board, so it lets
/// clicks through to the game: the pointer and mouse buttons are read while it is shown and handed to
/// <see cref="OverlayViewModel.UpdateCardPoolPointer"/> (hold the pool, right-click opens the pool browser).
/// </summary>
public partial class CardPoolPanel : UserControl
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public CardPoolPanel()
    {
        InitializeComponent();
        _poll.Tick += (_, _) => Poll();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _poll.Start();
            }
            else
            {
                _poll.Stop();
                ViewModel?.UpdateCardPoolPointer(false, false, false);
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
        vm.UpdateCardPoolPointer(over, PoolInput.IsDown(PoolInput.LeftButton), PoolInput.IsDown(PoolInput.RightButton));
    }
}
