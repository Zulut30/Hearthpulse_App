using System.Net;
using System.Reflection;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Tests;

/// <summary>
/// <see cref="AppServices"/>, what both hosts built the same way at start (Q2d): the shared HTTP client and how the
/// services are wired to each other. No network: a handler that answers nothing; a history file of the test's own.
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
        await With(new AppSettings { ShowCardPool = false }, async (services, handler) =>
        {
            await services.LoadCardPoolAsync();
            await services.LoadCardPoolAsync();

            Assert.Equal(0, handler.CatalogRequests);
        });
    }

    [Fact]
    public Task The_usage_gate_judges_the_advisor_and_hears_when_a_game_ends() =>
        With(new AppSettings(), (services, _) =>
        {
            Assert.Same(services.Usage, services.Session.Advisor.Usage);
            var handlers = Handlers(services.Session, nameof(GameSession.GameFinished));
            Assert.Contains(handlers, h => h.Target == services && h.Method.Name == "OnGameFinished");
            return Task.CompletedTask;
        });

    [Fact]
    public Task A_change_of_the_account_wakes_the_profile_upload() =>
        With(new AppSettings(), (services, _) =>
        {
            var handlers = Handlers(services.Account, nameof(AccountService.Changed));
            Assert.Contains(handlers, h => h.Target == services.Uploader && h.Method.Name == nameof(ProfileUploader.Kick));
            return Task.CompletedTask;
        });

    [Fact]
    public Task The_mulligan_guide_starts_at_the_rank_in_the_settings() =>
        With(new AppSettings { MulliganRank = "diamond" }, (services, _) =>
        {
            Assert.Equal("diamond", services.MulliganGuide.Rank);
            return Task.CompletedTask;
        });

    [Fact]
    public Task The_upload_names_base_heroes_from_the_cards_loaded_by_then()
    {
        var asked = 0;
        return With(new AppSettings(), (services, _) =>
        {
            var hero = services.Uploader.BaseHero!("TB_BaconShop_HERO_01");

            Assert.Equal(1, asked);
            Assert.Equal(CardDb.Empty.BaseHeroId("TB_BaconShop_HERO_01"), hero);
            return Task.CompletedTask;
        }, () =>
        {
            asked++;
            return CardDb.Empty;
        });
    }

    /// <summary>Builds the services on a client that answers nothing and a history of the test's own, runs the test, cleans up.</summary>
    internal static async Task With(AppSettings settings, Func<AppServices, Offline, Task> test, Func<CardDb>? cards = null, Offline? handler = null)
    {
        handler ??= new Offline();
        var db = TestDataFolder.Scratch("hp-services") + ".db";
        using var http = new HttpClient(handler);
        var services = new AppServices(settings, http, cards ?? (() => CardDb.Empty), db);
        try
        {
            await test(services, handler);
        }
        finally
        {
            services.Memory.Dispose();
            TestDataFolder.TryDelete(db);
        }
    }

    /// <summary>The handlers of a field-like event, read from its backing field.</summary>
    private static Delegate[] Handlers(object source, string eventName) =>
        (source.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(source) as Delegate)?.GetInvocationList() ?? [];

    /// <summary>
    /// No network: every request fails, except the HearthstoneJSON card list when <see cref="CardList"/> is set. Requests
    /// for the card pool's first source (the API's constructed cards) are counted.
    /// </summary>
    internal sealed class Offline : HttpMessageHandler
    {
        private int _catalog;

        public string? CardList { get; set; }

        public int CatalogRequests => Volatile.Read(ref _catalog);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("constructed-cards", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _catalog);
            }

            return Task.FromResult(CardList is { } list && path.EndsWith("cards.collectible.json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list) }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}

/// <summary>
/// <see cref="AppServices.LoadCardPoolAsync"/>: asked again after nothing could be loaded, once after a catalog came. Shares
/// the card pool cache files with <see cref="CardPoolFallbackTests"/>, so it runs in their collection and starts from none.
/// </summary>
[Collection(nameof(CardPoolCacheCollection))]
public sealed class AppServicesCardPoolTests
{
    private const string Collectible = """
        [{"id":"CS2_087","dbfId":1,"name":"Blessing of Might","set":"CORE","type":"SPELL","cost":1,"collectible":true,"cardClass":"PALADIN"}]
        """;

    public AppServicesCardPoolTests()
    {
        Directory.CreateDirectory(Path.Combine(AppPaths.Cache, "api"));
        File.Delete(Path.Combine(AppPaths.Cache, "api", "card-pool.json"));
        File.Delete(Path.Combine(AppPaths.Cache, "cards.enUS.collectible.json"));
    }

    [Fact]
    public Task Nothing_loaded_lets_the_next_call_ask_again() =>
        AppServicesTests.With(new AppSettings { ShowCardPool = true }, async (services, handler) =>
        {
            await services.LoadCardPoolAsync();
            await services.LoadCardPoolAsync();

            Assert.Equal(2, handler.CatalogRequests);
        });

    [Fact]
    public Task A_loaded_catalog_is_not_asked_for_again() =>
        AppServicesTests.With(new AppSettings { ShowCardPool = true }, async (services, handler) =>
        {
            await services.LoadCardPoolAsync();
            await services.LoadCardPoolAsync();

            Assert.Equal(1, handler.CatalogRequests);
        }, handler: new AppServicesTests.Offline { CardList = Collectible });
}
