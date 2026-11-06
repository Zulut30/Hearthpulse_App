using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Twitch;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// Settings → Stream → Twitch extension (TASK-2026-11-07-twitch-extension, AC4): the switch and the delay reach the
    /// settings and tell the host; with the switch on the status line follows the sender, and "Link Twitch" shows while
    /// the account has no Twitch linked.
    /// </summary>
    [Fact]
    public void Twitch_card_switch_delay_status_and_link_button() => Headless.Run(TwitchCard);

    private static void TwitchCard()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var app = Application.Current!;
        var theme = Theme();
        var window = new Window { Width = 1260, Height = 840 };
        var settings = new AppSettings();
        var model = new MainViewModel(new HistoryStore(Path.Combine(Path.GetTempPath(), $"hp-twitch-{Guid.NewGuid():N}.db")), settings, TestDataFolder.App);
        using var http = new HttpClient(new NotLinkedSite());
        using var publisher = new TwitchPublisher(new TwitchClient(http, new Uri("https://site.test/api/v1/")), _ => Task.FromResult<string?>("token"), settings, () => CardDb.Empty);
        try
        {
            model.AttachTwitch(publisher);
            var view = new MainView { DataContext = model };
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            var told = 0;
            model.StreamSettingsChanged += () => told++;
            view.ShowSettingsTab("Stream");

            var toggle = Named<CheckBox>(view, "TwitchSetting")!;
            var status = Named<TextBlock>(view, "TwitchStatusLine")!;
            var link = Named<Button>(view, "TwitchLinkButton")!;
            var delay = Named<Slider>(view, "TwitchDelaySetting")!;
            Assert.False(toggle.IsChecked);
            Assert.False(status.IsEffectivelyVisible);
            Assert.False(link.IsEffectivelyVisible);

            delay.Value = 45;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(45, settings.TwitchDelaySeconds);
            Assert.Equal(Loc.F("Twitch.DelayValue", 45), model.TwitchDelayText);
            Assert.Equal(1, told);

            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(settings.TwitchExtension);
            Assert.Equal(2, told);
            publisher.Update(LiveSnapshot.Empty);
            for (var i = 0; i < 200 && publisher.Link.State != TwitchLinkState.NotLinked; i++)
            {
                Thread.Sleep(5);
            }

            Dispatcher.UIThread.RunJobs();
            Assert.True(status.IsEffectivelyVisible);
            Assert.Equal(Loc.T("Twitch.Status.NotLinked"), status.Text);
            Assert.True(link.IsEffectivelyVisible);
            Assert.Equal(new Uri("https://site.test/account/twitch"), publisher.Link.LinkUrl);

            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(status.IsEffectivelyVisible);
            Assert.False(link.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [Theory]
    [InlineData("OFFLINE", "Twitch.Error.Offline")]
    [InlineData("NOT_AVAILABLE", "Twitch.Error.NotAvailable")]
    [InlineData("TWITCH_UNAVAILABLE", "Twitch.Error.TwitchDown")]
    [InlineData("SERVER_ERROR", "Twitch.Error.Server")]
    public void Twitch_errors_have_words(string code, string key)
    {
        Loc.Instance.SetLanguage(Loc.English);
        Assert.Equal(Loc.T(key), MainViewModel.TwitchErrorReason(code));
        Assert.Equal(Loc.F("Twitch.Error.Refused", "INVALID_STATE"), MainViewModel.TwitchErrorReason("INVALID_STATE"));
    }

    private sealed class NotLinkedSite : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"configured":true,"linked":false,"linkUrl":"https://site.test/account/twitch"}""", Encoding.UTF8, "application/json"),
            });
    }
}
