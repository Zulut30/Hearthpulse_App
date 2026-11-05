using System.Windows;
using System.Windows.Media;
using Hearthpulse.App.Overlay;
using Hearthpulse.Core;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// R06 in WPF: the opacity setting fades the backdrop brushes the overlay's panels take as dynamic resources, never the
/// surface with the text; fully opaque, the theme's own brushes show. The Avalonia twin is in LocBindingTests.
/// </summary>
public sealed class OverlayOpacityTests
{
    [Fact]
    public void Wpf_overlay_opacity_fades_the_backdrops_and_keeps_the_surface_opaque() => WpfThread.RunFresh(Check);

    private static void Check()
    {
        var app = Application.Current!;
        var window = new OverlayWindow(new OverlayViewModel(), new AppSettings { OverlayOpacity = 0.4 });
        var root = (FrameworkElement)window.Content;

        window.FadeBackdrops(0.4);
        Assert.Equal(1, window.Surface.Opacity);
        foreach (var key in OverlayViewModel.BackdropBrushes)
        {
            var original = (Brush)app.FindResource(key);
            var faded = Assert.IsAssignableFrom<Brush>(window.Resources[key]);
            Assert.Equal(original.Opacity * 0.4, faded.Opacity, 3);
            // What the panels resolve under the window is the faded copy.
            Assert.Same(faded, root.FindResource(key));
        }

        window.FadeBackdrops(1);
        foreach (var key in OverlayViewModel.BackdropBrushes)
        {
            Assert.False(window.Resources.Contains(key), key);
            Assert.Same(app.FindResource(key), root.FindResource(key));
        }

        window.Close();
    }
}
