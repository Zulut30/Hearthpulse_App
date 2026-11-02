using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Stats;

public partial class ArenaStatsView : UserControl
{
    public ArenaStatsView() => InitializeComponent();

    /// <summary>An opened rewards editor takes the focus in its first field, its text selected.</summary>
    private void FocusOnLoad(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }
}
