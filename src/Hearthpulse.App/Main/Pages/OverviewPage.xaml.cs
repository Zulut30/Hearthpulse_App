using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Pages;

/// <summary>
/// The Home page of the main window (its data: <see cref="MainViewModel"/>). Its buttons ask the window for what only
/// the window does: the log import, arranging the overlay, opening History.
/// </summary>
public partial class OverviewPage : UserControl
{
    public OverviewPage() => InitializeComponent();

    /// <summary>"Import" on the getting-started card.</summary>
    public event Action? ImportRequested;

    /// <summary>"Arrange the overlay" on the getting-started card.</summary>
    public event Action? ArrangeRequested;

    /// <summary>"View all" on the recent games card.</summary>
    public event Action? HistoryRequested;

    private void OnImport(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();

    private void OnArrange(object sender, RoutedEventArgs e) => ArrangeRequested?.Invoke();

    private void OnViewHistory(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke();
}
