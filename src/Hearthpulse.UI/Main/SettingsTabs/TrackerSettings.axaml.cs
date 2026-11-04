using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Hearthpulse.UI.Main.SettingsTabs;

/// <summary>
/// Settings, Tracker: the Constructed deck tracker, counters and mulligan (its data: <see cref="MainViewModel"/>). What
/// only the main window does (saving and applying settings, opening folders, the overlay) the window keeps: this tab's
/// buttons raise events it handles.
/// </summary>
public partial class TrackerSettings : UserControl
{
    /// <summary>Whether the overlay switch (the Overlay tab's) is on: the "overlay is off" banner shows while it is not.</summary>
    public static readonly StyledProperty<bool> OverlayOnProperty = AvaloniaProperty.Register<TrackerSettings, bool>(nameof(OverlayOn), true);

    public TrackerSettings() => InitializeComponent();

    /// <summary>The view binds it to the Overlay tab's switch.</summary>
    public bool OverlayOn
    {
        get => GetValue(OverlayOnProperty);
        set => SetValue(OverlayOnProperty, value);
    }

    /// <summary>The window's <c>OnOpenOverlayTab</c> for this tab's Click.</summary>
    public event Action<RoutedEventArgs>? OpenOverlayTabClicked;

    private void RaiseOpenOverlayTabClicked(object? sender, RoutedEventArgs e) => OpenOverlayTabClicked?.Invoke(e);
}
