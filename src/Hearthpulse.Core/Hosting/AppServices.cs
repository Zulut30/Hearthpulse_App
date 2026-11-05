using System.Net;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Hosting;

/// <summary>
/// The services both apps build at start the same way: the shared HTTP client and API, the start-up prefetch of the
/// game and the cards, the history, the client memory reader, the live session with its data sources, the site
/// account with its profile upload, and the daily free-use gate. The hosts keep what is theirs: windows, tray,
/// dispatcher and what runs when.
/// </summary>
public sealed class AppServices
{
    private int _cardPoolRequested;

    /// <param name="settings">The app's settings (the same instance the main window edits).</param>
    /// <param name="version">The app's version, for the HTTP user agent.</param>
    /// <param name="cards">The card data the host has loaded so far (empty until the cards are in).</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The client is kept in Http and lives as long as the app.")]
    public AppServices(AppSettings settings, string version, Func<CardDb> cards)
        : this(settings, CreateHttp(version), cards, AppData.FromAppPaths(), AppPaths.Database, UsageGate.DefaultPath)
    {
    }

    /// <summary>
    /// With a client, a data folder, a history file and a usage record of the test's own (no network, not the shared
    /// data folder's).
    /// </summary>
    internal AppServices(AppSettings settings, HttpClient http, Func<CardDb> cards, AppData data, string historyPath, string usagePath)
    {
        Settings = settings;
        Http = http;
        Api = new HearthpulseApi(http);
        // The game and the cards are looked up while the host builds its windows, not after.
        Prefetch = StartupPrefetch.Start(settings, http);

        Data = data;
        History = new HistoryStore(historyPath) { Replays = Data.Replays };
        Memory = new HearthstoneMemory();
        Session = new GameSession(History, Memory);
        MulliganGuide = new MulliganGuideSource(new HearthpulseData(Api)) { Rank = settings.MulliganRank };
        Session.SetMulliganGuide(MulliganGuide);
        Session.SetCardPoolWiki(new WikiPoolSource(new HearthpulseData(Api)));
        Session.SetDiscoverPicks(new DiscoverChoiceSource(new HearthpulseData(Api)));
        Session.SetBgLeaderboards(new BgLeaderboardSource(Api));

        var accountClient = new AccountClient(http);
        // The account center's journal of the contact with the site (Settings → Account), in the data folder.
        Account = new AccountService(accountClient, activity: new AccountActivity(Path.Combine(data.Root, "account-activity.json")));
        Uploader = ProfileUploader.For(accountClient, Account, () => settings.UploadGamesToProfile, History);
        Uploader.BaseHero = id => cards().BaseHeroId(id);
        Account.Changed += Uploader.Kick;
        // Daily free use of the stats pool's numbers (plan 3.1); the host makes the session publish again on a decision.
        Usage = new UsageGate(accountClient, Account, usagePath);
        // "Check now" in the account center re-reads the limits as well as /me.
        Account.CheckRequested += () => Usage.RefreshLimitsAsync(force: true).Forget("Usage limits");
        Session.Advisor.Usage = Usage;
        Session.GameFinished += _ => Usage.GameFinished();
    }

    public AppSettings Settings { get; }

    public HttpClient Http { get; }

    public HearthpulseApi Api { get; }

    public StartupPrefetch Prefetch { get; }

    /// <summary>The data folder and its replay stores.</summary>
    public AppData Data { get; }

    public HistoryStore History { get; }

    public HearthstoneMemory Memory { get; }

    public GameSession Session { get; }

    /// <summary>The overlay's mulligan guide; its rank follows <see cref="AppSettings.MulliganRank"/>.</summary>
    public MulliganGuideSource MulliganGuide { get; }

    public AccountService Account { get; }

    public ProfileUploader Uploader { get; }

    /// <summary>Daily free use of the stats pool's numbers in the overlay (plan 3.1).</summary>
    public UsageGate Usage { get; }

    /// <summary>
    /// The app's one HTTP client: compressed answers, connections renewed every 10 minutes (DNS changes), a 40-second
    /// limit, and the app's name and version as its user agent.
    /// </summary>
    public static HttpClient CreateHttp(string version) => CreateHttp(version, Isolation.On);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The handler is owned by the HttpClient (disposeHandler: true), which lives as long as the app.")]
    internal static HttpClient CreateHttp(string version, bool offline)
    {
        HttpMessageHandler handler = offline ? new OfflineHandler() : new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(40),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthpulse/" + version);
        return http;
    }

    /// <summary>
    /// Loads the card catalog the overlay's card pool panel cuts pools from: once, and only while "Show card pool" is on
    /// (called at start-up and on settings changes). Nothing loaded (offline, no cache) lets the next call try again.
    /// </summary>
    public async Task LoadCardPoolAsync()
    {
        if (!Settings.ShowCardPool || Interlocked.Exchange(ref _cardPoolRequested, 1) == 1)
        {
            return;
        }

        try
        {
            var catalog = await CardPoolCatalog.LoadAsync(Api, Http).ConfigureAwait(false);
            Session.SetCardPoolCatalog(catalog);
            Diag.Info($"Card pool: {catalog.Count} cards from {catalog.Source}");
            if (catalog.Count == 0)
            {
                Volatile.Write(ref _cardPoolRequested, 0);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _cardPoolRequested, 0);
            Diag.Error("Card pool catalog unavailable", ex);
        }
    }

    /// <summary>Fails every request (<see cref="Isolation"/>).</summary>
    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("No network in an isolated run (HEARTHPULSE_ISOLATED)"));
    }
}
