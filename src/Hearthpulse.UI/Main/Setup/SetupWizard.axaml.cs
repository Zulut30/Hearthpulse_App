using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Hearthpulse.UI.Main.Setup;

/// <summary>
/// The setup wizard over the main view's page (its data: <see cref="MainViewModel"/>, <c>MainViewModel.Setup.cs</c>).
/// Arranging the overlay is asked of the view, as on Home.
/// </summary>
public partial class SetupWizard : UserControl
{
    public SetupWizard()
    {
        InitializeComponent();
        // Shown: the keyboard starts in the wizard (on Skip, its first control), not on the page under it.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && e.NewValue is true)
            {
                Dispatcher.UIThread.Post(() => SkipButton.Focus(NavigationMethod.Tab), DispatcherPriority.Input);
            }
        };
    }

    /// <summary>"Arrange panels" on the overlay step.</summary>
    public event Action? ArrangeRequested;

    private void OnArrange(object? sender, RoutedEventArgs e)
    {
        (DataContext as MainViewModel)?.SetupArranged();
        ArrangeRequested?.Invoke();
    }
}
