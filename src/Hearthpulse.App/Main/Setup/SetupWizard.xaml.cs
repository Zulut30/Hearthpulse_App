using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.Setup;

/// <summary>
/// The setup wizard over the main window's page (its data: <see cref="MainViewModel"/>, <c>MainViewModel.Setup.cs</c>).
/// Arranging the overlay is asked of the window, as on Home.
/// </summary>
public partial class SetupWizard : UserControl
{
    public SetupWizard() => InitializeComponent();

    /// <summary>"Arrange panels" on the overlay step.</summary>
    public event Action? ArrangeRequested;

    private void OnArrange(object sender, RoutedEventArgs e) => ArrangeRequested?.Invoke();
}
