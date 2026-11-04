using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.SettingsTabs;

/// <summary>
/// Settings, General: language, theme, app (its data: <see cref="MainViewModel"/>). What only the main window does (saving and
/// applying settings, opening folders, the overlay) the window keeps: this tab's buttons raise events it handles.
/// </summary>
public partial class GeneralSettings : UserControl
{
    public GeneralSettings() => InitializeComponent();
}
