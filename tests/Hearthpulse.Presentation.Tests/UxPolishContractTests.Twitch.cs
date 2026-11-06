using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

public sealed partial class UxPolishContractTests
{
    /// <summary>
    /// Settings → Stream → Twitch extension in WPF (TASK-2026-11-07-twitch-extension): the same controls as the Avalonia
    /// card (LocBindingTests.Twitch), bound to the shared model's properties; the switch and delay reach the settings.
    /// </summary>
    [Fact]
    public void Twitch_card_binds_the_shared_model_in_wpf() => Sta(() =>
    {
        var settings = new AppSettings();
        var model = new WpfMain(History(), settings, TestApp.Data);
        var (window, _) = Open(model, "Settings", "Stream");

        Assert.Equal(nameof(WpfMain.TwitchExtension), BindingOperations.GetBinding(Named<CheckBox>(window, "TwitchSetting"), ToggleButton.IsCheckedProperty)?.Path?.Path);
        Assert.Equal(nameof(WpfMain.TwitchDelay), BindingOperations.GetBinding(Named<Slider>(window, "TwitchDelaySetting"), RangeBase.ValueProperty)?.Path?.Path);
        Assert.Equal(nameof(WpfMain.TwitchStatusText), BindingPath(Named<TextBlock>(window, "TwitchStatusLine")));
        Assert.Equal(nameof(WpfMain.LinkTwitch), BindingOperations.GetBinding(Named<Button>(window, "TwitchLinkButton"), ButtonBase.CommandProperty)?.Path?.Path);

        model.TwitchExtension = true;
        model.TwitchDelay = 90.4;
        Assert.True(settings.TwitchExtension);
        Assert.Equal(90, settings.TwitchDelaySeconds);
        Assert.Equal(Loc.F("Twitch.DelayValue", 90), model.TwitchDelayText);
        window.Close();
    });

    [Fact]
    public void Twitch_texts_are_the_same_in_both_hosts()
    {
        Loc.Instance.SetLanguage(Loc.English);
        foreach (var code in new[] { "OFFLINE", "NOT_AVAILABLE", "RATE_LIMITED", "TWITCH_UNAVAILABLE", "SERVER_ERROR", "INVALID_STATE" })
        {
            Assert.Equal(WpfMain.TwitchErrorReason(code), AvaMain.TwitchErrorReason(code));
        }

        // No sender attached (renders, tests): the card has no status line and no link button.
        var wpf = new WpfMain(History(), new AppSettings { TwitchExtension = true }, TestApp.Data);
        Assert.False(wpf.TwitchStatusShown);
        Assert.False(wpf.TwitchLinkShown);
    }
}
