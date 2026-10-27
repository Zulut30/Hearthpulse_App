using System.Net;
using Hearthpulse.Core;

namespace Hearthpulse.Tests;

/// <summary>The hosts load the cards while their windows are built; they must get the cards their settings ask for.</summary>
public sealed class StartupPrefetchTests
{
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private static void Cache(string locale, string name)
    {
        Directory.CreateDirectory(AppPaths.Cache);
        File.WriteAllText(Path.Combine(AppPaths.Cache, $"cards.{locale}.json"), $$"""[{"id":"TST_001","dbfId":1,"name":"{{name}}"}]""");
    }

    [Fact]
    public async Task The_prefetched_cards_are_used_when_the_locale_still_matches()
    {
        Cache("tsAA", "Card A");
        Cache("tsBB", "Card B");
        using var http = new HttpClient(new Offline());
        var prefetch = StartupPrefetch.Start(new AppSettings { Locale = "tsAA" }, http);

        var first = await prefetch.CardsAsync("tsAA");
        Assert.Equal("tsAA", first.Locale);
        Assert.Equal("Card A", first.Get("TST_001")!.Name);
        Assert.Same(first, await prefetch.CardsAsync("tsAA"));

        // The settings asked for another locale by the time the host needed the cards: those are loaded instead.
        var other = await prefetch.CardsAsync("tsBB");
        Assert.Equal("tsBB", other.Locale);
        Assert.Equal("Card B", other.Get("TST_001")!.Name);
    }

    [Fact]
    public async Task The_game_is_looked_up_with_the_configured_folder()
    {
        var install = Path.Combine(TestDataFolder.Path, "prefetch-install");
        Directory.CreateDirectory(Path.Combine(install, "Logs"));
        File.WriteAllText(Path.Combine(install, "Hearthstone.exe"), string.Empty);
        using var http = new HttpClient(new Offline());
        var prefetch = StartupPrefetch.Start(new AppSettings { Locale = "tsAA", HearthstonePath = install }, http);

        var (found, client) = await prefetch.Game;
        Assert.Equal(Hearthpulse.Core.Hearthstone.HearthstoneInstall.FindInstallDirectory(install), found);
        Assert.Equal(Hearthpulse.Core.Hearthstone.HearthstoneInstall.DetectLocale(found), client);
    }
}
