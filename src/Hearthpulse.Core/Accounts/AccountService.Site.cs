using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Accounts;

public sealed partial class AccountService
{
    /// <summary>
    /// Uploads a replay the player chose to put on the site (plan 2.2, <see cref="Replay.ReplayUpload"/>): the link and
    /// the delete token, or an error code (SIGNED_OUT, OFFLINE, QUOTA_EXCEEDED, TOO_LARGE, RATE_LIMITED, REFUSED,
    /// SERVER_ERROR). <paramref name="gameId"/> is the game's profile event id (<see cref="ProfileEvents.ProfileGameId"/>).
    /// </summary>
    public async Task<(Replay.ReplayUploadResult? Result, string? Error)> UploadReplayAsync(string gzipBase64, string? gameId = null, CancellationToken cancellationToken = default)
    {
        if (await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) is not { } token)
        {
            return (null, "SIGNED_OUT");
        }

        try
        {
            var (status, body) = await client.PostReplayAsync(token, gzipBase64, gameId, cancellationToken).ConfigureAwait(false);
            if (status == 201 && Replay.ReplayUploadResult.Parse(body) is { } result)
            {
                Activity.Succeeded(SiteService.Replays);
                return (result, null);
            }

            Diag.Info($"account: replay upload answered {status}");
            var code = status switch
            {
                401 or 403 => "SIGNED_OUT",
                404 => "NOT_AVAILABLE",
                409 => "QUOTA_EXCEEDED",
                413 => "TOO_LARGE",
                429 => "RATE_LIMITED",
                >= 400 and < 500 => "REFUSED",
                _ => "SERVER_ERROR",
            };

            // Any other 4xx is the site saying no (REFUSED to the card); the journal keeps the code it named.
            Activity.Failed(SiteService.Replays, code == "REFUSED" ? AccountClient.ErrorCodeOf(body) ?? code : code);
            return (null, code);
        }
        catch (AccountException ex)
        {
            Activity.Failed(SiteService.Replays, ex.Code);
            return (null, ex.Code);
        }
    }

    /// <summary>
    /// Claims one game of a limited feature on the site (plan 3.1, <see cref="UsageGate"/>): the site's decision, or null
    /// when signed out, offline or the answer is not one (the gate then counts the game on this machine).
    /// </summary>
    public async Task<UsageClaim?> ClaimUsageAsync(string feature, string gameId, CancellationToken cancellationToken = default)
    {
        if (await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false) is not { } token)
        {
            return null;
        }

        try
        {
            var (status, body) = await client.PostUsageClaimAsync(token, feature, gameId, cancellationToken).ConfigureAwait(false);
            if (status == 200 && UsageClaim.Parse(body) is { } claim)
            {
                Activity.Succeeded(SiteService.Limits);
                return claim;
            }

            Diag.Info($"account: usage claim answered {status}");
            Activity.Failed(SiteService.Limits, StatusCode(status));
            return null;
        }
        catch (AccountException ex)
        {
            Activity.Failed(SiteService.Limits, ex.Code);
            return null;
        }
    }

    /// <summary>
    /// Deletes an uploaded replay by its delete token: null when deleted; <c>NOT_FOUND</c> when the site has no such
    /// replay (deleted on the account page, expired); <c>OFFLINE</c>, <c>RATE_LIMITED</c>, <c>REFUSED</c> (another 4xx) or <c>SERVER_ERROR</c> when
    /// it is still there.
    /// </summary>
    public async Task<string?> DeleteReplayAsync(string id, string deleteToken, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await client.DeleteReplayAsync(id, deleteToken, cancellationToken).ConfigureAwait(false);
            if (status is not (204 or 404))
            {
                Diag.Info($"account: replay delete answered {status}");
                Activity.Failed(SiteService.Replays, StatusCode(status));
            }
            else if (status == 204)
            {
                // A 404 is no proof either way: the replay is gone, or the site has no replays yet.
                Activity.Succeeded(SiteService.Replays);
            }

            return status switch
            {
                204 => null,
                404 => "NOT_FOUND",
                429 => "RATE_LIMITED",
                >= 400 and < 500 => "REFUSED",
                _ => "SERVER_ERROR",
            };
        }
        catch (AccountException ex)
        {
            Activity.Failed(SiteService.Replays, ex.Code);
            return ex.Code == "OFFLINE" ? "OFFLINE" : "SERVER_ERROR";
        }
    }
}
