using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.LogicalTree;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;
using Microsoft.Extensions.Time.Testing;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>A site that cannot be reached: the restored account stays signed in, offline.</summary>
    private sealed class UnreachableSite : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    /// <summary>
    /// The main window's account indicator (TASK-2026-11-05-account-center): hidden while signed out; with a restored
    /// account the site cannot reach it shows "Offline" with the state line as its tip and accessible name, and a click
    /// opens Settings, Account, where the center shows the same line.
    /// </summary>
    [Fact]
    public async Task Account_indicator_shows_the_state_and_opens_the_account_center()
    {
        var dir = TestDataFolder.Scratch("hp-indicator");
        Directory.CreateDirectory(dir);
        var store = new TokenStore(Path.Combine(dir, "account.dat"));
        var client = new AccountClient(new HttpClient(new UnreachableSite()), "https://site.test/api/v1/");
        Assert.True(store.Save("ref-1", new AccountUser("u-1", "Anna", Entitlements.None), DateTime.UtcNow.AddDays(20), client.Site));
        // The retry while offline runs on a clock that never moves here.
        var account = new AccountService(client, store, activity: new AccountActivity(null)) { Time = new FakeTimeProvider(DateTimeOffset.UtcNow) };
        await account.RestoreAsync();
        Headless.Run(() => IndicatorChecks(account, dir));
    }

    private static void IndicatorChecks(AccountService account, string dir)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var theme = Theme();
        var window = new Window { Width = 1260, Height = 840 };
        var settings = new AppSettings();
        var model = new MainViewModel(HistoryStore.InMemory(), settings, TestDataFolder.App);
        try
        {
            var view = new MainView { DataContext = model };
            window.Content = view;
            Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("AccountLinkButton")!;
            Assert.False(button.IsVisible);

            model.AttachAccount(account);
            Dispatcher.UIThread.RunJobs();

            Assert.True(button.IsVisible);
            Assert.Equal(model.AccountLinkTip, ToolTip.GetTip(button));
            Assert.Equal(model.AccountLinkTip, Avalonia.Automation.AutomationProperties.GetName(button));
            Assert.StartsWith("hearthpulse.net: Can't reach the site — retrying at ", model.AccountLinkTip, StringComparison.Ordinal);
            Assert.Contains(button.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == "Offline");

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Account", settings.SettingsTab);
            Assert.True(view.FindControl<RadioButton>("SettingsTabAccount")!.IsChecked);
            Dispatcher.UIThread.RunJobs();
            var center = view.GetLogicalDescendants().OfType<Hearthpulse.UI.Main.SettingsTabs.AccountSettings>().Single();
            Assert.Contains(center.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == model.AccountLinkText && t.Text.StartsWith("Can't reach the site — retrying at ", StringComparison.Ordinal));
            Assert.Contains(center.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("Account.Center.Connection"));
        }
        finally
        {
            window.Close();
            Application.Current!.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
            TestDataFolder.TryDelete(dir);
        }
    }
}
