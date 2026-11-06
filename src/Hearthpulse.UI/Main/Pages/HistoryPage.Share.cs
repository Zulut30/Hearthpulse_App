using Avalonia.Interactivity;
using Hearthpulse.UI.Main.Share;

namespace Hearthpulse.UI.Main.Pages;

/// <summary>"YouTube timestamps" in the History header (proposal C2).</summary>
public partial class HistoryPage
{
    private void OnTimestamps(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            TimestampsWindow.Open(this, model.Timestamps());
        }
    }
}
