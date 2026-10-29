using System.Windows;
using System.Windows.Controls;
using Hearthpulse.Core;
using Hearthpulse.Core.Tracking;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The second usability pass (TASK-2026-10-29-ux-round2) in the WPF window laid out off screen with the real theme and
/// fonts, and in both hosts' view models.
/// </summary>
public sealed partial class UxPolishContractTests
{
    [Fact]
    public void Setup_banner_wraps_a_long_warning_inside_its_card() => Sta(() =>
    {
        var model = new WpfMain(History(), new AppSettings());
        // The narrowest window and every log missing: the warning is far longer than one line.
        var (window, root) = Open(model, "Home", width: 1040);
        model.Apply(LiveSnapshot.Empty with
        {
            LogConfigMissing = ["Power", "LoadingScreen", "Decks", "Arena", "Achievements", "FullScreenFX", "Zone", "Asset", "Net", "Bob", "Gameplay", "Store"],
        });
        Settle(root);

        var banner = Named<Border>(window, "SetupBanner");
        var text = Named<TextBlock>(window, "SetupWarningText");
        Assert.Equal(Visibility.Visible, banner.Visibility);
        Assert.Equal(model.SetupWarning, text.Text);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        // Inside the card's padding, right of the glyph: measured against its column, not at full length.
        var inner = banner.ActualWidth - banner.Padding.Left - banner.Padding.Right - banner.BorderThickness.Left - banner.BorderThickness.Right;
        Assert.True(text.ActualWidth <= inner + 0.5, $"text {text.ActualWidth} in {inner}");
        Assert.True(text.TranslatePoint(new Point(text.ActualWidth, 0), banner).X <= banner.ActualWidth - banner.Padding.Right + 0.5);
        Assert.True(text.ActualHeight >= 1.8 * text.FontSize, $"one line only: {text.ActualHeight}");
        window.Close();
    });
}
