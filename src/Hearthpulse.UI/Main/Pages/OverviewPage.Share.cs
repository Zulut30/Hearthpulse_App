using Avalonia.Interactivity;
using Hearthpulse.UI.Main.Share;

namespace Hearthpulse.UI.Main.Pages;

/// <summary>"Session recap" on the recent games card (TASK-2026-11-07-growth-app): the share window of the latest session.</summary>
public partial class OverviewPage
{
    private void OnSessionRecap(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel model)
        {
            ShareWindow.Open(this, model.SessionRecapShare());
        }
    }
}
