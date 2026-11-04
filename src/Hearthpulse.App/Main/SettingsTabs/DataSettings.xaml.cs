using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.SettingsTabs;

/// <summary>
/// Settings, Data: history, the data folder, where the numbers come from (its data: <see cref="MainViewModel"/>). What
/// only the main window does (saving and applying settings, opening folders, the overlay) the window keeps: this tab's
/// buttons raise events it handles.
/// </summary>
public partial class DataSettings : UserControl
{
    public DataSettings() => InitializeComponent();

    /// <summary>The window's <c>OnReimport</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? ReimportClicked;

    private void RaiseReimportClicked(object sender, RoutedEventArgs e) => ReimportClicked?.Invoke(e);

    /// <summary>The window's <c>OnOpenData</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? OpenDataClicked;

    private void RaiseOpenDataClicked(object sender, RoutedEventArgs e) => OpenDataClicked?.Invoke(e);
}
