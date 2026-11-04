using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.SettingsTabs;

/// <summary>
/// Settings, Account: the hearthpulse.net sign-in (its data: <see cref="MainViewModel"/>). What only the main window does (saving and
/// applying settings, opening folders, the overlay) the window keeps: this tab's buttons raise events it handles.
/// </summary>
public partial class AccountSettings : UserControl
{
    public AccountSettings() => InitializeComponent();

    /// <summary>The window's <c>OnCopyAccountCode</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? CopyAccountCodeClicked;

    private void RaiseCopyAccountCodeClicked(object? sender, RoutedEventArgs e) => CopyAccountCodeClicked?.Invoke(e);
}
