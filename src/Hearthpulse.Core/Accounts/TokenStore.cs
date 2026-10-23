using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// Keeps the account's refresh token (rotated on every refresh, so rewritten each time), profile and last known paid
/// access between starts. On Windows the file is encrypted with DPAPI for the current Windows user, so a copied file is
/// useless elsewhere; on macOS and Linux it is readable by the owner only.
/// </summary>
public sealed class TokenStore(string path)
{
    // File format: 2 = device flow on the HeartPulse site. Older files (the email/password draft, the OIDC draft) have
    // no Format and are dropped unread, so their tokens never go to a service that did not issue them.
    private const int CurrentFormat = 2;

    // The last known paid access is kept too, so a start without network shows it as it was. Site is the origin that
    // issued the token. ExpiresAt is the sign-in's absolute end (refreshing does not extend it).
    private sealed record Saved(string RefreshToken, string Id, string DisplayName, bool HasAccess = false, string[]? Sources = null,
        string[]? Sections = null, bool Stale = true, DateTime? CheckedAt = null, DateTime? ExpiresAt = null, int Format = 0,
        string? Site = null);

    private string TempPath => path + ".tmp";

    /// <summary>
    /// The session saved for <paramref name="site"/> (an origin such as https://hearthpulse.net), or null. A file from
    /// another site or an older format is deleted. The paid access comes back marked stale: it is only what the site
    /// said last time.
    /// </summary>
    public (string RefreshToken, AccountUser User, DateTime? ExpiresAt)? Load(string site)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
            {
                bytes = Dpapi.Unprotect(bytes);
            }

            var saved = JsonSerializer.Deserialize<Saved>(bytes);
            if (saved is not { RefreshToken.Length: > 0, Format: CurrentFormat } || !string.Equals(saved.Site, site, StringComparison.OrdinalIgnoreCase))
            {
                Diag.Info("account: saved session is for another site or format, dropped");
                Delete();
                return null;
            }

            return (saved.RefreshToken, new AccountUser(saved.Id, saved.DisplayName,
                new Entitlements(saved.HasAccess, saved.Sources ?? [], saved.Sections ?? [], Stale: true, saved.CheckedAt)), saved.ExpiresAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicFailure)
        {
            Diag.Info($"account: saved session unreadable ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// Writes the session atomically (temp file, then rename): the site revokes the whole sign-in when an already rotated
    /// refresh token comes back, so a half-written file must never replace the last good one. False when it could not be
    /// written; the previous file is then left as it was.
    /// </summary>
    public bool Save(string refreshToken, AccountUser user, DateTime? expiresAt, string site)
    {
        var temp = TempPath;
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(refreshToken, user.Id, user.DisplayName, user.Entitlements.HasAccess,
                [.. user.Entitlements.Sources], [.. user.Entitlements.Sections], user.Entitlements.Stale, user.Entitlements.CheckedAt, expiresAt,
                CurrentFormat, site));
            if (OperatingSystem.IsWindows())
            {
                bytes = Dpapi.Protect(bytes);
            }
            else
            {
                using (File.Create(temp, 1, FileOptions.None))
                {
                }

                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicFailure)
        {
            Diag.Error("account: could not save the session", ex);
            TryDelete(temp);
            return false;
        }
    }

    public void Delete()
    {
        TryDelete(path);
        TryDelete(TempPath);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("account: could not delete the saved session", ex);
        }
    }

    internal sealed class CryptographicFailure(string message) : Exception(message);

    [SupportedOSPlatform("windows")]
    internal static class Dpapi
    {
        // An app-specific entropy keeps other programs of the same user from decrypting the file by accident.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Hearthpulse.Account.v1");

        public static byte[] Protect(byte[] data) => Run(data, protect: true);

        public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

        private static unsafe byte[] Run(byte[] data, bool protect)
        {
            fixed (byte* input = data)
            fixed (byte* entropy = Entropy)
            {
                var inBlob = new Blob { Size = data.Length, Data = (nint)input };
                var entropyBlob = new Blob { Size = Entropy.Length, Data = (nint)entropy };
                var ok = protect
                    ? CryptProtectData(ref inBlob, null, ref entropyBlob, 0, 0, UiForbidden, out var outBlob)
                    : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, UiForbidden, out outBlob);
                if (!ok)
                {
                    throw new CryptographicFailure($"DPAPI failed ({Marshal.GetLastPInvokeError()})");
                }

                try
                {
                    var result = new byte[outBlob.Size];
                    Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                    return result;
                }
                finally
                {
                    LocalFree(outBlob.Data);
                }
            }
        }

        private const int UiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public nint Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob dataIn, string? description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CryptUnprotectData(ref Blob dataIn, nint description, ref Blob entropy, nint reserved, nint prompt, int flags, out Blob dataOut);

        [DllImport("kernel32.dll")]
        private static extern nint LocalFree(nint memory);
    }
}
