using System.Globalization;
using System.Net;
#if HEARTHPULSE_WPF
using System.Net.Http;
#endif
using System.Text;
using Hearthpulse.Core.Accounts;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// <c>--render-ui</c> only (render group <c>account</c>): a made-up signed-in player "Render" so the account center and the
/// connection indicator can be checked in frames without a sign-in. The site is answered here in memory: no network, no
/// real token, nothing written to the data folder. Called only by the renderers (DevRender.Brawl.cs,
/// OffscreenRenderer.Brawl.cs), and it refuses to run in a process started without <c>--render-ui</c>.
/// </summary>
public sealed partial class MainViewModel
{
    private (int Count, DateTime? Last)? _accountRenderReplays;

    /// <summary>
    /// Replaces the account with the sample: <paramref name="state"/> (<c>HEARTHPULSE_RENDER_ACCOUNT</c>) is
    /// <c>connected</c> (the default), <c>offline</c> or <c>ended</c>. Games sent 42 (the last at 14:02), 3 replays on the
    /// site, 2 of 5 free games used today, an Arena + Standard subscription.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The sample's client and its in-memory site live as long as the sample account, i.e. the render process.")]
    internal async Task ShowRenderAccountAsync(string? state)
    {
        if (!Environment.GetCommandLineArgs().Contains("--render-ui", StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The sample account exists for --render-ui only.");
        }

        var link = state?.Trim().ToLowerInvariant() switch
        {
            "offline" => AccountLink.Offline,
            "ended" => AccountLink.SessionEnded,
            _ => AccountLink.Connected,
        };

        // The journal as a day of play left it: 42 games, the last at 14:02 local time; today's limited games.
        var localDay = DateTime.Now.Date;
        DateTime? at = localDay.AddHours(14).AddMinutes(2).ToUniversalTime();
        var activity = new AccountActivity(null) { UtcNow = () => at ?? DateTime.UtcNow };
        activity.CountGames(42, 0, 0);
        // The services answered a minute ago (before the restore below, which then decides the state).
        at = DateTime.UtcNow.AddMinutes(-1);
        activity.Succeeded(SiteService.Games);
        activity.Succeeded(SiteService.Replays);
        activity.Succeeded(SiteService.Limits);
        at = null;
        var utcDay = DateTime.UtcNow.Date;
        activity.SetUsage(new UsageToday(utcDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), true, Entitlements.Battlegrounds, 2, 5,
            DateTime.SpecifyKind(utcDay.AddDays(1), DateTimeKind.Utc)));
        _accountRenderReplays = (3, localDay.AddDays(-2).AddHours(20).ToUniversalTime());

        // A throwaway token file outside the data folder, gone once the session is restored from it.
        var tokens = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hearthpulse-render-account-{Guid.NewGuid():N}.dat");
        var client = new AccountClient(new HttpClient(new RenderSite(link), disposeHandler: true), "https://hearthpulse.net/api/v1/");
        var store = new TokenStore(tokens);
        store.Save("render-refresh", new AccountUser("render", "Render", Entitlements.None), DateTime.UtcNow.AddDays(21), client.Site);
        var account = new AccountService(client, store, _ => true, activity);
        try
        {
            await account.RestoreAsync().ConfigureAwait(true);
        }
        finally
        {
            store.Delete();
        }

        AttachAccount(account);
        await RefreshAccountCountsAsync().ConfigureAwait(true);
    }

    /// <summary>hearthpulse.net for the sample: the refresh and <c>/me</c> as the state needs them; nothing leaves the process.</summary>
    private sealed class RenderSite(AccountLink state) : HttpMessageHandler
    {
        private const string Me = """
            {"user": {"id": "render", "name": "Render", "profileUrl": "https://hearthpulse.net/profile/"},
             "subscription": {"hasAccess": true, "source": "boosty", "stale": false,
                              "entitlements": {"arena": true, "standard": true}}}
            """;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (state == AccountLink.Offline)
            {
                throw new HttpRequestException("render sample: no connection");
            }

            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return Task.FromResult(path switch
            {
                _ when path.EndsWith("/oauth/token", StringComparison.Ordinal) && state == AccountLink.SessionEnded => Json(400, """{"error": "invalid_grant"}"""),
                _ when path.EndsWith("/oauth/token", StringComparison.Ordinal) => Json(200,
                    """{"access_token": "render-access", "refresh_token": "render-refresh", "token_type": "Bearer", "expires_in": 900}"""),
                _ when path.EndsWith("/me", StringComparison.Ordinal) => Json(200, Me),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(int status, string body) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
