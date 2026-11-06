using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.UI.Main.SettingsTabs;

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
        SizeSlider.ValueChanged += (_, _) => OnLookChanged();
        OpacitySlider.ValueChanged += (_, _) => OnLookChanged();
        // Shown again (the tab, the page): a panel's rows may have changed on the overlay since, and the theme with it.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty && IsVisible)
            {
                OnLookChanged();
            }
        };
        DataContextChanged += (_, _) => OnLookChanged();
    }

    /// <summary>The window saves the settings after a density preset (D5).</summary>
    public event Action? DensityChanged;

    /// <summary>A density preset: the size and every panel's rows at once; the size slider and the preview follow.</summary>
    private void OnDensity(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel model || sender is not RadioButton { Tag: string preset })
        {
            return;
        }

        model.ApplyOverlayDensity(preset);
        // The settings object does not notify: the slider takes the new size (and the window saves on its change)
        SizeSlider.Value = model.Settings.OverlayScale;
        OnLookChanged();
        DensityChanged?.Invoke();
    }

    private void OnLookChanged()
    {
        (DataContext as MainViewModel)?.RefreshOverlayLook();
        RefreshPreview();
    }

    /// <summary>
    /// The preview's deck panel at the overlay's size, its backgrounds at the panels' opacity: faded copies of the
    /// backdrop brushes in the preview box, as <see cref="OverlayView"/> does for the overlay.
    /// </summary>
    private void RefreshPreview()
    {
        if (DataContext is not MainViewModel model)
        {
            return;
        }

        OverlayPreviewScale.LayoutTransform = new ScaleTransform(model.PreviewScale, model.PreviewScale);
        var opacity = model.Settings.OverlayOpacity;
        foreach (var key in OverlayViewModel.BackdropBrushes)
        {
            if (opacity < 1 && Application.Current is { } app && app.TryGetResource(key, ActualThemeVariant, out var value)
                && OverlayView.Faded(value as IBrush, opacity) is { } faded)
            {
                OverlayPreviewBox.Resources[key] = faded;
            }
            else
            {
                OverlayPreviewBox.Resources.Remove(key);
            }
        }
    }

    /// <summary>The window's <c>OnOpenTrackerTab</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? OpenTrackerTabClicked;

    private void RaiseOpenTrackerTabClicked(object? sender, RoutedEventArgs e) => OpenTrackerTabClicked?.Invoke(e);

    /// <summary>The window's <c>OnArrangeOverlay</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? ArrangeOverlayClicked;

    private void RaiseArrangeOverlayClicked(object? sender, RoutedEventArgs e) => ArrangeOverlayClicked?.Invoke(e);
}
