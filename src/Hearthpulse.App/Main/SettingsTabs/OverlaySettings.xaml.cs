using System.Windows;
using System.Windows.Controls;

namespace Hearthpulse.App.Main.SettingsTabs;

/// <summary>
/// Settings, Overlay: what is drawn over the game, its size, panel layout (its data: <see cref="MainViewModel"/>). What
/// only the main window does (saving and applying settings, opening folders, the overlay) the window keeps: this tab's
/// buttons raise events it handles.
/// </summary>
public partial class OverlaySettings : UserControl
{
    public OverlaySettings() => InitializeComponent();

    /// <summary>The window's <c>OnSliderChanged</c> for this tab's ValueChanged.</summary>
    public event Action<RoutedPropertyChangedEventArgs<double>>? SliderChanged;

    private void RaiseSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => SliderChanged?.Invoke(e);

    /// <summary>The window's <c>OnOpenTrackerTab</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? OpenTrackerTabClicked;

    private void RaiseOpenTrackerTabClicked(object sender, RoutedEventArgs e) => OpenTrackerTabClicked?.Invoke(e);

    /// <summary>The window's <c>OnArrangeOverlay</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? ArrangeOverlayClicked;

    private void RaiseArrangeOverlayClicked(object sender, RoutedEventArgs e) => ArrangeOverlayClicked?.Invoke(e);
}
