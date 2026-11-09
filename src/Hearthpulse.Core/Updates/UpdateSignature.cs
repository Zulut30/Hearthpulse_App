using System.Text;

namespace Hearthpulse.Core.Updates;

/// <summary>
/// The project's signature on a release: the release workflow signs SHA256SUMS.txt (one line per release file) with
/// the project's Ed25519 key and uploads the signature as SHA256SUMS.txt.sig (base64 of the 64 signature bytes). A
/// build that carries the public key (update-signing-key.pub, embedded) installs a file only when the signature checks
/// out with one of its keys, the file's line in SHA256SUMS.txt is there and equals GitHub's digest; a build without a key
/// (no PEM block in the file) goes by GitHub's digest alone, as builds before the signature did.
/// </summary>
internal static class UpdateSignature
{
    public const string ChecksumsName = "SHA256SUMS.txt";

    public const string SignatureName = "SHA256SUMS.txt.sig";

    /// <summary>The DER prefix of an Ed25519 SubjectPublicKeyInfo (RFC 8410): 12 bytes, then the 32 key bytes.</summary>
    private static readonly byte[] SpkiPrefix = Convert.FromHexString("302a300506032b6570032100");

    private const int MaxFileBytes = 256 * 1024;

    private static int _noKeyLogged;

    /// <summary>The keys this build trusts, read from the embedded update-signing-key.pub; empty when it carries none.</summary>
    public static IReadOnlyList<byte[]> EmbeddedKeys { get; } = ReadEmbeddedKeys();

    /// <summary>
    /// The Ed25519 keys in a key file: every "-----BEGIN PUBLIC KEY-----" block that holds an Ed25519 SPKI; "#" lines
    /// and anything else are ignored. A block that is not an Ed25519 key is skipped (and logged), never trusted.
    /// </summary>
    public static IReadOnlyList<byte[]> ParseKeys(string? text)
    {
        var keys = new List<byte[]>();
        if (string.IsNullOrEmpty(text))
        {
            return keys;
        }

        const string begin = "-----BEGIN PUBLIC KEY-----";
        const string end = "-----END PUBLIC KEY-----";
        var at = 0;
        while ((at = text.IndexOf(begin, at, StringComparison.Ordinal)) >= 0)
        {
            var start = at + begin.Length;
            var stop = text.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0)
            {
                break;
            }

            at = stop + end.Length;
            var base64 = new string([.. text[start..stop].Where(c => !char.IsWhiteSpace(c))]);
            try
            {
                var der = Convert.FromBase64String(base64);
                if (der.Length == SpkiPrefix.Length + Ed25519.PublicKeySize && der.AsSpan(0, SpkiPrefix.Length).SequenceEqual(SpkiPrefix))
                {
                    keys.Add(der[SpkiPrefix.Length..]);
                    continue;
                }
            }
            catch (FormatException)
            {
                // Logged below like any other block that is not an Ed25519 key.
            }

            Diag.Info("Update signing key file: a block is not an Ed25519 public key and is ignored.");
        }

        return keys;
    }

    /// <summary>The lines of SHA256SUMS.txt as name → lower-case hex ("hash  name", or "hash *name" from a binary-mode sha256sum).</summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 66 || line[64] != ' ' || (line[65] != ' ' && line[65] != '*'))
            {
                continue;
            }

            var hash = line[..64];
            if (hash.All(Uri.IsHexDigit))
            {
                sums[line[66..]] = hash.ToLowerInvariant();
            }
        }

        return sums;
    }

    /// <summary>True when the base64 signature text is a valid signature of <paramref name="sums"/> by one of the keys.</summary>
    public static bool VerifySums(ReadOnlySpan<byte> sums, string? signatureText, IReadOnlyList<byte[]> keys)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String((signatureText ?? string.Empty).Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var key in keys)
        {
            if (Ed25519.Verify(key, sums, signature))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks the release's signature for <paramref name="info"/> before anything is downloaded. Returns the update to
    /// download (the delta dropped when its own line does not match), or null with the exact reason in the log. Without
    /// keys (a build that carries none) the update comes back unchanged and the log says once that it was not checked.
    /// </summary>
    public static async Task<UpdateInfo?> VerifyAsync(HttpClient http, UpdateInfo info, IReadOnlyList<byte[]> keys, CancellationToken cancellationToken = default)
    {
        if (keys.Count == 0)
        {
            if (Interlocked.Exchange(ref _noKeyLogged, 1) == 0)
            {
                Diag.Info("Update: release signature not checked: this build carries no update key.");
            }

            return info;
        }

        if (info.ChecksumsUrl is null || info.SignatureUrl is null)
        {
            return Refuse(info, $"the release has no {(info.ChecksumsUrl is null ? ChecksumsName : SignatureName)}");
        }

        byte[]? sums;
        byte[]? signature;
        try
        {
            sums = await FetchAsync(http, info.ChecksumsUrl, cancellationToken).ConfigureAwait(false);
            signature = sums is null ? null : await FetchAsync(http, info.SignatureUrl, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return Refuse(info, $"{ChecksumsName} or its signature could not be downloaded ({ex.Message})");
        }

        if (sums is null || signature is null)
        {
            return Refuse(info, $"{(sums is null ? ChecksumsName : SignatureName)} could not be downloaded");
        }

        if (!VerifySums(sums, Encoding.ASCII.GetString(signature), keys))
        {
            return Refuse(info, $"{SignatureName} does not match {ChecksumsName} for any key this build trusts");
        }

        var lines = ParseSums(Encoding.UTF8.GetString(sums));
        if (!lines.TryGetValue(info.AssetName, out var hash))
        {
            return Refuse(info, $"{info.AssetName} is not listed in the signed {ChecksumsName}");
        }

        if (!string.Equals(hash, info.Sha256, StringComparison.Ordinal))
        {
            return Refuse(info, $"the signed SHA-256 of {info.AssetName} is not GitHub's digest");
        }

        var verified = info;
        if (info.Delta is { } delta && (!lines.TryGetValue(delta.Name, out var deltaHash) || !string.Equals(deltaHash, delta.Sha256, StringComparison.Ordinal)))
        {
            Diag.Info($"Update {info.Version}: the delta {delta.Name} has no matching line in the signed {ChecksumsName}; the full setup is used.");
            verified = info with { Delta = null };
        }

        Diag.Info($"Update {info.Version}: release signature verified.");
        return verified;
    }

    private static UpdateInfo? Refuse(UpdateInfo info, string reason)
    {
        Diag.Info($"Update {info.Version} not installed: {reason}.");
        return null;
    }

    /// <summary>A small release file (the sums, the signature); null for an answer other than 200 or a file too large to be one.</summary>
    private static async Task<byte[]?> FetchAsync(HttpClient http, string url, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxFileBytes)
        {
            return null;
        }

        // Read with a cap: a reply without a length (chunked) is never taken in whole.
        var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        await using var streamScope = stream.ConfigureAwait(false);
        using var kept = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (kept.Length + read > MaxFileBytes)
            {
                return null;
            }

            await kept.WriteAsync(buffer.AsMemory(0, read), cts.Token).ConfigureAwait(false);
        }

        return kept.ToArray();
    }

    private static IReadOnlyList<byte[]> ReadEmbeddedKeys()
    {
        using var stream = typeof(UpdateSignature).Assembly.GetManifestResourceStream("Hearthpulse.UpdateSigningKey.pub");
        if (stream is null)
        {
            return [];
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return ParseKeys(reader.ReadToEnd());
    }
}
