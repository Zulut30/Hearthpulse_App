using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.SettingsTabs;

/// <summary>
/// Settings, Account: the hearthpulse.net sign-in (its data: <see cref="MainViewModel"/>). What only the main window does (saving and
/// applying settings, opening folders, the overlay) the window keeps: this tab's buttons raise events it handles.
/// </summary>
public partial class AccountSettings : UserControl
{
    public AccountSettings() => InitializeComponent();
}
