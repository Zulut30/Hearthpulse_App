using System.Windows;
using Hearthpulse.App.Main.Share;

namespace Hearthpulse.App.Main.Pages;

/// <summary>"YouTube timestamps" in the History header (proposal C2).</summary>
public partial class HistoryPage
{
    private void OnTimestamps(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            TimestampsWindow.Open(Window.GetWindow(this), model.Timestamps());
        }
    }
}
