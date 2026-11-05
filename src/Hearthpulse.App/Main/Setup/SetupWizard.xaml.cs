using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Hearthpulse.App.Main.Setup;

/// <summary>
/// The setup wizard over the main window's page (its data: <see cref="MainViewModel"/>, <c>MainViewModel.Setup.cs</c>).
/// Arranging the overlay is asked of the window, as on Home.
/// </summary>
public partial class SetupWizard : UserControl
{
    public SetupWizard()
    {
        InitializeComponent();
        // Shown: the keyboard starts in the wizard (on Skip, its first control), not on the page under it.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SkipButton.Focus());
            }
        };
    }

    /// <summary>"Arrange panels" on the overlay step.</summary>
    public event Action? ArrangeRequested;

    private void OnArrange(object sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.SetupArranged();
        ArrangeRequested?.Invoke();
    }
}
