using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.SettingsTabs;

/// <summary>Settings → Stream: the "Session" window's switch and reset (its data: <see cref="MainViewModel"/>), in a file of its own.</summary>
public partial class StreamSessionSettings : UserControl
{
    public StreamSessionSettings() => InitializeComponent();

    private void OnReset(object? sender, RoutedEventArgs e) => (DataContext as MainViewModel)?.ResetStreamSession();
}
