using Avalonia.Controls;
using Avalonia.Input;

namespace Hearthpulse.UI.Overlay;

/// <summary>
/// The card pool panel. Right-click opens or closes the full list; while the pointer is over the panel the pool
/// stays up even when what triggered it (a hovered card, a finished Discover) is gone.
/// </summary>
public partial class CardPoolPanel : UserControl
{
    public CardPoolPanel()
    {
        InitializeComponent();
    }

    private OverlayViewModel? ViewModel => DataContext as OverlayViewModel;

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
            ViewModel?.ToggleCardPoolList.Execute(null);
            e.Handled = true;
        }
    }

    private void OnChipReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || ViewModel is not { } vm
            || sender is not Control { DataContext: PoolChipModel chip, Tag: string kind })
        {
            return;
        }

        (kind == "sort" ? vm.SetCardPoolSort : vm.SetCardPoolFilter).Execute(chip.Key);
        e.Handled = true;
    }
}
