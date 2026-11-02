using System.Net;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Hosting;

namespace Hearthpulse.Tests;

/// <summary>
/// <see cref="AppServices"/>, what both hosts built the same way at start: the shared HTTP client, and the card pool
/// catalog loaded only while its panel is on. No network: a handler that counts requests and answers nothing.
/// </summary>
public sealed class AppServicesTests
{
    [Fact]
    public void The_shared_client_asks_for_compressed_answers_names_the_app_and_gives_up_after_40_seconds()
    {
        using var http = AppServices.CreateHttp("1.2.3");

        Assert.Equal(TimeSpan.FromSeconds(40), http.Timeout);
        Assert.Equal("Hearthpulse/1.2.3", http.DefaultRequestHeaders.UserAgent.ToString());
    }

    [Fact]
    public async Task With_the_card_pool_off_the_catalog_is_never_asked_for()
    {
        var handler = new Offline();
        using var http = new HttpClient(handler);
        var db = TestDataFolder.Scratch("hp-services") + ".db";
        var services = new AppServices(new AppSettings { ShowCardPool = false }, http, () => CardDb.Empty, db);
        var before = handler.CardPoolRequests;

        await services.LoadCardPoolAsync();
        await services.LoadCardPoolAsync();

        Assert.Equal(before, handler.CardPoolRequests);
        services.Memory.Dispose();
        TestDataFolder.TryDelete(db);
    }

    /// <summary>No network: every request fails, card pool requests are counted.</summary>
    private sealed class Offline : HttpMessageHandler
    {
        private int _cardPool;

        public int CardPoolRequests => Volatile.Read(ref _cardPool);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("pool", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _cardPool);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}
