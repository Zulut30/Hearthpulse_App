using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Hearthpulse.App.Overlay;

namespace Hearthpulse.App.Main.SettingsTabs;

/// <summary>
/// Settings, Overlay: what is drawn over the game, its size, panel layout (its data: <see cref="MainViewModel"/>). What
/// only the main window does (saving and applying settings, opening folders, the overlay) the window keeps: this tab's
/// buttons raise events it handles.
/// </summary>
public partial class OverlaySettings : UserControl
{
    public OverlaySettings()
    {
        InitializeComponent();
        // Shown again (the tab, the page): a panel's rows may have changed on the overlay since, and the theme with it.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                (DataContext as MainViewModel)?.RefreshOverlayLook();
                RefreshPreview();
            }
        };
    }

    /// <summary>The window saves the settings after a density preset (D5).</summary>
    public event Action? DensityChanged;

    /// <summary>A density preset: the size and every panel's rows at once; the size slider and the preview follow.</summary>
    private void OnDensity(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || sender is not RadioButton { Tag: string preset })
        {
            return;
        }

        model.ApplyOverlayDensity(preset);
        // The settings object does not notify: the slider reads it again
        BindingOperations.GetBindingExpression(SizeSlider, RangeBase.ValueProperty)?.UpdateTarget();
        RefreshPreview();
        DensityChanged?.Invoke();
    }

    /// <summary>
    /// The preview's deck panel at the overlay's size, its backgrounds at the panels' opacity: faded copies of the
    /// backdrop brushes in the preview box, as <see cref="OverlayWindow"/> does for the overlay.
    /// </summary>
    private void RefreshPreview()
    {
        // Also raised by the sliders' first binding, before the preview is built
        if (DataContext is not MainViewModel model || OverlayPreviewPanel is null)
        {
            return;
        }

        OverlayPreviewPanel.LayoutTransform = new ScaleTransform(model.PreviewScale, model.PreviewScale);
        var opacity = model.Settings.OverlayOpacity;
        foreach (var key in OverlayViewModel.BackdropBrushes)
        {
            if (opacity >= 1)
            {
                OverlayPreviewBox.Resources.Remove(key);
            }
            else if (Application.Current?.TryFindResource(key) is Brush brush)
            {
                var faded = brush.Clone();
                faded.Opacity = brush.Opacity * opacity;
                faded.Freeze();
                OverlayPreviewBox.Resources[key] = faded;
            }
        }
    }

    /// <summary>The window's <c>OnSliderChanged</c> for this tab's ValueChanged.</summary>
    public event Action<RoutedPropertyChangedEventArgs<double>>? SliderChanged;

    /// <summary>Opening "Advanced" is not a setting: the page's OnSettingChanged must not see it.</summary>
    private void OnDisclosure(object sender, RoutedEventArgs e) => e.Handled = true;

    private void RaiseSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        (DataContext as MainViewModel)?.RefreshOverlayLook();
        RefreshPreview();
        SliderChanged?.Invoke(e);
    }

    /// <summary>The window's <c>OnOpenTrackerTab</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? OpenTrackerTabClicked;

    private void RaiseOpenTrackerTabClicked(object sender, RoutedEventArgs e) => OpenTrackerTabClicked?.Invoke(e);

    /// <summary>The window's <c>OnArrangeOverlay</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? ArrangeOverlayClicked;

    private void RaiseArrangeOverlayClicked(object sender, RoutedEventArgs e) => ArrangeOverlayClicked?.Invoke(e);
}
