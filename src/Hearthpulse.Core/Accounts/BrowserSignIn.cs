using System.ComponentModel;
using System.Diagnostics;

namespace Hearthpulse.Core.Accounts;

/// <summary>A finished browser sign-in: the verified profile and the tokens that came with it.</summary>
public sealed record SignInResult(AccountUser User, OidcTokens Tokens);

/// <summary>
/// One sign-in through the system browser (RFC 8252): listen on a loopback port, open the provider's authorization
/// page with PKCE S256, <c>state</c> and <c>nonce</c>, wait for the redirect, check it, exchange the code and read the
/// profile. The browser tab is answered only after all of that, so it says "signed in" only when it is true.
/// </summary>
public sealed class BrowserSignIn(OidcClient oidc, Func<Uri, bool>? openBrowser = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>How long the player has to finish in the browser.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    public async Task<SignInResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = await oidc.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewToken();
        var nonce = Pkce.NewToken();
        using var listener = new LoopbackListener();
        var redirectUri = listener.RedirectUri;
        if (!(openBrowser ?? SystemBrowser.Open)(OidcClient.AuthorizationUrl(endpoints, redirectUri, Pkce.Challenge(verifier), state, nonce)))
        {
            throw new AccountException("BROWSER_FAILED");
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(Timeout);
        SignInResult? result = null;
        Exception? failure = null;
        try
        {
            await listener.WaitAsync(async (query, token) =>
            {
                try
                {
                    var code = ParseCallback(query, state, oidc.Issuer);
                    var tokens = await oidc.ExchangeCodeAsync(code, redirectUri, verifier, token).ConfigureAwait(false);
                    result = new SignInResult(await oidc.ProfileAsync(tokens, nonce, token).ConfigureAwait(false), tokens);
                    return CallbackPage.Success;
                }
                catch (AccountException ex)
                {
                    failure = ex;
                    return CallbackPage.Failure;
                }
            }, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountException("TIMEOUT");
        }

        return result ?? throw failure!;
    }

    /// <summary>
    /// The authorization code from the redirect's query. <c>state</c> is checked first, error answers included: a
    /// redirect that is not for this attempt is ignored whatever it says. An <c>iss</c> parameter (RFC 9207), when the
    /// provider sends one, must name our issuer.
    /// </summary>
    internal static string ParseCallback(string query, string expectedState, string issuer)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            values.TryAdd(Decode(parts[0]), parts.Length > 1 ? Decode(parts[1]) : string.Empty);
        }

        if (!values.TryGetValue("state", out var state) || state != expectedState)
        {
            throw new AccountException("STATE_MISMATCH");
        }

        if (values.TryGetValue("iss", out var iss) && iss.TrimEnd('/') != issuer)
        {
            throw new AccountException("ISSUER_MISMATCH");
        }

        if (values.TryGetValue("error", out var error))
        {
            throw new AccountException(error.ToUpperInvariant(), values.GetValueOrDefault("error_description"));
        }

        return values.TryGetValue("code", out var code) && code.Length > 0 ? code : throw new AccountException("BAD_RESPONSE", "redirect without code");
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}

/// <summary>Opens a URL in the player's default browser.</summary>
public static class SystemBrowser
{
    public static bool Open(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        try
        {
            ProcessStartInfo start;
            if (OperatingSystem.IsWindows())
            {
                start = new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true };
            }
            else
            {
                start = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
                start.ArgumentList.Add(url.AbsoluteUri);
            }

            using var process = Process.Start(start);
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Diag.Error("account: could not open the browser", ex);
            return false;
        }
    }
}
