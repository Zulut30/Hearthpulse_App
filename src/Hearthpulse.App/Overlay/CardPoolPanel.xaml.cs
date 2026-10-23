using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hearthpulse.App.Overlay;

/// <summary>
/// The card pool panel. Right-click opens or closes the pool browser (every card); while the mouse is over the panel the pool stays
/// up even when what triggered it (a hovered card, a finished Discover) is gone.
/// </summary>
public partial class CardPoolPanel : UserControl
{
    public CardPoolPanel()
    {
        InitializeComponent();
    }

    private OverlayViewModel? ViewModel => DataContext as OverlayViewModel;

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

    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        ViewModel?.OpenCardPoolBrowser.Execute(null);
        e.Handled = true;
    }
}
