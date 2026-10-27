using System.Net;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;

namespace Hearthpulse.Tests;

/// <summary>The HearthstoneJSON fallback of the card pool, used while the Hearthpulse API is down.</summary>
public sealed class CardPoolFallbackTests
{
    private const string Collectible = """
        [{"id":"CS2_087","dbfId":1,"name":"Blessing of Might","set":"CORE","type":"SPELL","cost":1,"collectible":true,"cardClass":"PALADIN"}]
        """;

    [Fact]
    public async Task A_broken_answer_is_not_kept_as_the_cached_card_list()
    {
        // The only test of the fallback: it owns these cache files.
        File.Delete(Path.Combine(AppPaths.Cache, "cards.enUS.collectible.json"));
        Directory.CreateDirectory(Path.Combine(AppPaths.Cache, "api"));
        File.Delete(Path.Combine(AppPaths.Cache, "api", "card-pool.json"));
        var api = new HearthpulseApi(new HttpClient(new Answer(HttpStatusCode.ServiceUnavailable, "{}")), "https://api.test/");

        // A captive portal answers 200 with a page instead of the card list.
        var broken = await CardPoolCatalog.LoadAsync(api, new HttpClient(new Answer(HttpStatusCode.OK, "<html>sign in</html>")));
        var fixedNow = await CardPoolCatalog.LoadAsync(api, new HttpClient(new Answer(HttpStatusCode.OK, Collectible)));

        Assert.Equal(0, broken.Count);
        Assert.Equal(1, fixedNow.Count);
    }

    private sealed class Answer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
