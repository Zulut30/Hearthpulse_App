using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.SettingsTabs;

/// <summary>
/// Settings, Stream: streamer mode and the OBS windows (its data: <see cref="MainViewModel"/>). What only the main window does (saving and
/// applying settings, opening folders, the overlay) the window keeps: this tab's buttons raise events it handles.
/// </summary>
public partial class StreamSettings : UserControl
{
    public StreamSettings() => InitializeComponent();
}
