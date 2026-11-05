using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Hearthpulse.Core;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// R06 (owner, 2026-11-05): the opacity setting fades the panels' backgrounds only. The overlay keeps its surface
    /// opaque, and the panels take its faded copies of the backdrop brushes; the theme's own brushes stay as they were.
    /// </summary>
    [Fact]
    public void Avalonia_overlay_opacity_fades_backgrounds_and_keeps_text_opaque() => Headless.Run(() =>
    {
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 1920, Height = 1080 };
        window.Resources["UiFont"] = FontFamily.Default;
        try
        {
            var view = new OverlayView { DataContext = new OverlayViewModel(), Settings = new AppSettings { OverlayOpacity = 0.4 } };
            window.Content = view;
            window.Show();
            view.Fit(1920, 1080);

            Assert.Equal(1, view.FindControl<Control>("Surface")!.Opacity);
            foreach (var key in OverlayViewModel.BackdropBrushes)
            {
                Assert.True(app.TryGetResource(key, ThemeVariant.Default, out var original), key);
                var faded = Assert.IsAssignableFrom<IBrush>(view.Resources[key]);
                Assert.Equal(((IBrush)original!).Opacity * 0.4, faded.Opacity, 3);
                Assert.Equal(1, ((IBrush)original).Opacity);
            }

            // The panels draw with the overlay's copy, not the theme's.
            var solid = view.Resources["OverlayPanelSolid"];
            Assert.Contains(view.GetLogicalDescendants().OfType<Border>(), b => ReferenceEquals(b.Background, solid));
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
        }
    });
}
