using System.ComponentModel;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// Streamer mode (<c>docs/streaming.md</c>, review M1): Settings → Account says "Signed in" instead of the site account's
/// name while the mode is on, and the name comes back when it is off. The site is offline here: the saved session is
/// enough.
/// </summary>
public sealed class StreamerAccountTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"hp-streamer-account-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // The temp folder takes what is left.
        }
    }

    /// <summary>A site that cannot be reached: the account keeps its saved session.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Streamer_mode_shows_signed_in_instead_of_the_account_name()
    {
        Loc.Instance.SetLanguage(Loc.English);
        Directory.CreateDirectory(_dir);
        var store = new TokenStore(Path.Combine(_dir, "account.dat"));
        var client = new AccountClient(new HttpClient(new Offline()), "https://site.test/api/v1/");
        Assert.True(store.Save("ref-1", new AccountUser("u-1", "StreamerName", Entitlements.None), DateTime.UtcNow.AddDays(30), client.Site));
        var account = new AccountService(client, store);
        await account.RestoreAsync();
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), TestDataFolder.App);
        vm.AttachAccount(account);
        Assert.Equal("StreamerName", vm.AccountName);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.StreamerMode = true;
        Assert.Contains(nameof(MainViewModel.AccountName), raised);
        Assert.Equal(Loc.T("Stream.Mode.AccountHidden"), vm.AccountName);
        Assert.DoesNotContain("StreamerName", vm.AccountName, StringComparison.Ordinal);

        vm.StreamerMode = false;
        Assert.Equal("StreamerName", vm.AccountName);
    }
}
