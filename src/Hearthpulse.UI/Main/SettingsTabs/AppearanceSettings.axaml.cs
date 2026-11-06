using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.SettingsTabs;

/// <summary>
/// Settings, Appearance (its data: <see cref="MainViewModel"/>, MainViewModel.Appearance.cs). The tile under the pointer
/// or the keyboard focus fills the preview; leaving the gallery shows the theme in use again.
/// </summary>
public partial class AppearanceSettings : UserControl
{
    public AppearanceSettings() => InitializeComponent();

    private MainViewModel? Model => DataContext as MainViewModel;

    private void OnTileEnter(object? sender, PointerEventArgs e) => ShowPreview(sender);

    private void OnTileFocus(object? sender, FocusChangedEventArgs e) => ShowPreview(sender);

    private void OnTileBlur(object? sender, RoutedEventArgs e) => Model?.PreviewThemeCommand.Execute(null);

    private void OnGalleryLeave(object? sender, PointerEventArgs e) => Model?.PreviewThemeCommand.Execute(null);

    private void ShowPreview(object? sender)
    {
        if (sender is Control { DataContext: ThemeChoice tile })
        {
            Model?.PreviewThemeCommand.Execute(tile.Key);
        }
    }
}
