using System.Net.Http.Headers;
using System.Text.Json;
using Hearthpulse.Core.Accounts;

namespace Hearthpulse.Core.Twitch;

/// <summary>A site answer: the HTTP status (0 when the site was not reached), its error code, and its <c>Retry-After</c>.</summary>
public sealed record TwitchAnswer(int Status, string? Code, TimeSpan? RetryAfter, string Body)
{
    /// <summary>No answer: the network failed or timed out.</summary>
    public static TwitchAnswer Offline { get; } = new(0, "OFFLINE", null, string.Empty);
}

/// <summary>The account's Twitch link as <c>GET twitch/status</c> gives it.</summary>
public sealed record TwitchStatusInfo(bool Configured, bool Linked, string? Login, string? DisplayName, Uri? LinkUrl);

/// <summary>
/// The site's Twitch routes for the app (TASK-2026-11-07-twitch-extension, "Контракты"): <c>POST twitch/state</c> with
/// the deck, <c>GET twitch/status</c> with the link. Both carry the account's Bearer token; the base is the account's
/// API (<c>HEARTHPULSE_SITE_API</c> or hearthpulse.net). Never throws for a network failure: that is
/// <see cref="TwitchAnswer.Offline"/>.
/// </summary>
public sealed class TwitchClient(HttpClient http, Uri baseUri)
{
    /// <summary>The site's page that links a Twitch account, when the status gave none.</summary>
    public Uri DefaultLinkUrl { get; } = new(baseUri.GetLeftPart(UriPartial.Authority) + "/account/twitch");

    /// <summary>Sends one state body (<see cref="TwitchState.ToJson"/>).</summary>
    public async Task<TwitchAnswer> PostStateAsync(string accessToken, string json, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "twitch/state"))
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the link; the info is null unless the site answered 200 with a readable body.</summary>
    public async Task<(TwitchAnswer Answer, TwitchStatusInfo? Info)> GetStatusAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, "twitch/status"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var answer = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (answer, answer.Status == 200 ? ParseStatus(answer.Body) : null);
    }

    internal static TwitchStatusInfo? ParseStatus(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var link = AccountClient.Text(root, "linkUrl") is { Length: > 0 } text
                && Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri
                : null;
            return new TwitchStatusInfo(Flag(root, "configured"), Flag(root, "linked"),
                AccountClient.Text(root, "login") is { Length: > 0 } login ? login : null,
                AccountClient.Text(root, "displayName") is { Length: > 0 } name ? name : null,
                link);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private async Task<TwitchAnswer> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return new TwitchAnswer(status, status >= 400 ? AccountClient.ErrorCodeOf(body) : null, RetryAfter(response.Headers.RetryAfter), body);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            Diag.Info("twitch: the site was not reached: " + ex.Message);
            return TwitchAnswer.Offline;
        }
    }

    /// <summary>The wait the site asked for: seconds, or a date (from now); null when absent.</summary>
    internal static TimeSpan? RetryAfter(RetryConditionHeaderValue? header, DateTimeOffset? now = null)
    {
        if (header?.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - (now ?? DateTimeOffset.UtcNow);
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }
}
