using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Hearthpulse.App.Main.SettingsTabs;

/// <summary>
/// Settings, Appearance (its data: <see cref="MainViewModel"/>, MainViewModel.Appearance.cs). The tile under the pointer
/// or the keyboard focus fills the preview; leaving the gallery shows the theme in use again.
/// </summary>
public partial class AppearanceSettings : UserControl
{
    public AppearanceSettings() => InitializeComponent();

    /// <summary>The window's <c>OnSliderChanged</c> for this tab's sliders (saved like the overlay's).</summary>
    public event Action<RoutedPropertyChangedEventArgs<double>>? SliderChanged;

    private MainViewModel? Model => DataContext as MainViewModel;

    private void OnTileEnter(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThemeChoice tile })
        {
            Model?.PreviewThemeCommand.Execute(tile.Key);
        }
    }

    private void OnGalleryLeave(object sender, RoutedEventArgs e)
    {
        if (e is KeyboardFocusChangedEventArgs { NewFocus: FrameworkElement { DataContext: ThemeChoice } })
        {
            // Focus moved to the next tile: its GotKeyboardFocus shows it.
            return;
        }

        Model?.PreviewThemeCommand.Execute(null);
    }

    /// <summary>Opening "Advanced" is not a setting: the page's OnSettingChanged must not see it.</summary>
    private void OnDisclosure(object sender, RoutedEventArgs e) => e.Handled = true;

    private void OnSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SliderChanged?.Invoke(e);
}
