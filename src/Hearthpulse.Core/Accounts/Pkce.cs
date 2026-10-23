using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// Proof Key for Code Exchange (RFC 7636) and the random <c>state</c>/<c>nonce</c> values of one sign-in. Only the
/// S256 method is used: the provider requires PKCE and "plain" would leak the verifier in the browser URL.
/// </summary>
internal static class Pkce
{
    /// <summary>A fresh code verifier: 32 random bytes, base64url without padding (43 characters, RFC 7636 4.1).</summary>
    public static string NewVerifier() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 code challenge: base64url(SHA-256(ASCII(verifier))).</summary>
    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>An unguessable value for <c>state</c> or <c>nonce</c>.</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
}
