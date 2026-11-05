using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.Setup;

/// <summary>
/// The setup wizard over the main view's page (its data: <see cref="MainViewModel"/>, <c>MainViewModel.Setup.cs</c>).
/// Arranging the overlay is asked of the view, as on Home.
/// </summary>
public partial class SetupWizard : UserControl
{
    public SetupWizard() => InitializeComponent();

    /// <summary>"Arrange panels" on the overlay step.</summary>
    public event Action? ArrangeRequested;

    private void OnArrange(object? sender, RoutedEventArgs e) => ArrangeRequested?.Invoke();
}
