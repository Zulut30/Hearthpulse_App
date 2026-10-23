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
    // The last known paid access is kept too, so a start without network shows it as it was. ExpiresAt is the sign-in's
    // absolute end (refreshing does not extend it).
    private sealed record Saved(string RefreshToken, string Id, string DisplayName, bool HasAccess = false, string[]? Sources = null,
        string[]? Sections = null, bool Stale = true, DateTime? CheckedAt = null, DateTime? ExpiresAt = null);

    public (string RefreshToken, AccountUser User, DateTime? ExpiresAt)? Load()
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
            return saved is { RefreshToken.Length: > 0 }
                ? (saved.RefreshToken, new AccountUser(saved.Id, saved.DisplayName,
                    new Entitlements(saved.HasAccess, saved.Sources ?? [], saved.Sections ?? [], saved.Stale, saved.CheckedAt)), saved.ExpiresAt)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicFailure)
        {
            Diag.Info($"account: saved session unreadable ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// Writes the session atomically (temp file, then rename): the site revokes the whole sign-in when an already rotated
    /// refresh token comes back, so a half-written file must never replace the last good one.
    /// </summary>
    public bool Save(string refreshToken, AccountUser user, DateTime? expiresAt)
    {
        var temp = path + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Saved(refreshToken, user.Id, user.DisplayName, user.Entitlements.HasAccess,
                [.. user.Entitlements.Sources], [.. user.Entitlements.Sections], user.Entitlements.Stale, user.Entitlements.CheckedAt, expiresAt));
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
            return false;
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("account: could not delete the saved session", ex);
        }
    }

    internal sealed class CryptographicFailure(string message) : Exception(message);

    [SupportedOSPlatform("windows")]
    private static class Dpapi
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
