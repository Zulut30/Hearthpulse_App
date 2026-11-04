using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.SettingsTabs;

/// <summary>
/// Settings, General: language, theme, app (its data: <see cref="MainViewModel"/>). What only the main window does (saving and
/// applying settings, opening folders, the overlay) the window keeps: this tab's buttons raise events it handles.
/// </summary>
public partial class GeneralSettings : UserControl
{
    public GeneralSettings() => InitializeComponent();

    /// <summary>The window's <c>OnSettingChanged</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? SettingClicked;

    private void RaiseSettingClicked(object sender, RoutedEventArgs e) => SettingClicked?.Invoke(e);
}
