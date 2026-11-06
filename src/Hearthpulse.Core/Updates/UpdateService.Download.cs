using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Updates;

public static partial class UpdateService
{
    /// <summary>Where a downloaded update waits until the app closes. Shared with the next run, so it is downloaded once.</summary>
    public static string StagedPath(UpdateInfo info) => Path.Combine(AppPaths.Root, "updates", info.AssetName);

    /// <summary>Downloads the release file; returns the staged path, or null when it could not be fetched.</summary>
    public static async Task<string?> DownloadAsync(HttpClient http, UpdateInfo info, CancellationToken cancellationToken = default)
    {
        // The setup runs silently, so a file that cannot be checked against the published one is never downloaded.
        if (info.Sha256 is null)
        {
            Diag.Info(UnverifiedMessage(info));
            // A copy an older build downloaded without a check would only take space (about 100 MB).
            TryDelete(StagedPath(info));
            return null;
        }

        try
        {
            var staged = StagedPath(info);
            var existing = new FileInfo(staged);
            if (existing.Exists && existing.Length > 0)
            {
                if (await MatchesDigestAsync(staged, info, cancellationToken).ConfigureAwait(false))
                {
                    return staged;
                }

                File.Delete(staged);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            DeleteOldDownloads(staged);
            var part = staged + ".part";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromMinutes(30));
            using (var response = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                await using var sourceScope = source.ConfigureAwait(false);
                var file = File.Create(part);
                await using var fileScope = file.ConfigureAwait(false);
                await source.CopyToAsync(file, cts.Token).ConfigureAwait(false);
            }

            // The setup runs silently, so a file that is not exactly the published one is never kept.
            if (!await MatchesDigestAsync(part, info, cancellationToken).ConfigureAwait(false))
            {
                Diag.Info($"Update {info.Version} failed its checksum and was discarded.");
                File.Delete(part);
                return null;
            }

            File.Move(part, staged, overwrite: true);
            ClearDownloadMark(staged);
            Diag.Info($"Update {info.Version} downloaded ({info.AssetName}).");
            return staged;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            Diag.Error("Update download failed", ex);
            return null;
        }
    }

    /// <summary>True only when the file has the SHA-256 the release publishes; false when the release publishes none.</summary>
    private static async Task<bool> MatchesDigestAsync(string path, UpdateInfo info, CancellationToken cancellationToken)
    {
        if (info.Sha256 is null)
        {
            return false;
        }

        var stream = File.OpenRead(path);
        await using var streamScope = stream.ConfigureAwait(false);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash) == info.Sha256;
    }

    /// <summary>Earlier downloads are a hundred megabytes each and are never needed again.</summary>
    private static void DeleteOldDownloads(string keep)
    {
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(keep)!))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _ = ex;
                }
            }
        }
    }
}
