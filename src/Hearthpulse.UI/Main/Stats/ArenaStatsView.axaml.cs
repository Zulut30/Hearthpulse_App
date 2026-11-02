using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.Stats;

public partial class ArenaStatsView : UserControl
{
    public ArenaStatsView() => InitializeComponent();

    /// <summary>An opened rewards editor takes the focus in its first field, its text selected; once, not on a rebuild.</summary>
    private void FocusOnLoad(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: ArenaRewardsEdit edit } box && edit.TakeFocus())
        {
            box.Focus();
            box.SelectAll();
        }
    }

    /// <summary>The chip of the run whose editor just closed gets the focus back.</summary>
    private void ChipLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArenaRunRow row } chip && DataContext is MainViewModel vm && vm.TakeArenaRewardsFocus(row.Key))
        {
            chip.Focus();
        }
    }
}
