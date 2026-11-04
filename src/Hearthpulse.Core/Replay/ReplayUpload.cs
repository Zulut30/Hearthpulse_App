using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Replay;

/// <summary>The site's answer to an upload: the replay's id, its public link and the token that deletes it.</summary>
public sealed record ReplayUploadResult(string Id, string Url, string DeleteToken)
{
    public DateTime UploadedAt { get; init; } = DateTime.UtcNow;

    internal static ReplayUploadResult? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("id", out var id) && id.GetString() is { Length: 10 } idText
                   && root.TryGetProperty("url", out var url) && url.GetString() is { } urlText
                   && Uri.TryCreate(urlText, UriKind.Absolute, out var link) && link.Scheme == Uri.UriSchemeHttps
                   && root.TryGetProperty("deleteToken", out var token) && token.GetString() is { Length: > 0 } tokenText
                ? new ReplayUploadResult(idText, urlText, tokenText)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Web replays (plan 2.2): what the app uploads when the player presses "Upload to the site" on a replay card. The
/// copy is the Share copy (<see cref="ReplayImport.Sanitize"/>, date only, no names) without the post-game part of a
/// Constructed game (<c>N</c>/<c>K</c>: opponent cards the log revealed, which the player may never have seen), gzip in
/// base64. The site checks it again (docs/web-replays.md). Uploads are remembered in <c>replays\uploads.json</c>
/// (<see cref="ReplayUploads"/>), so the card can show the link and delete the replay from the site.
/// </summary>
public static class ReplayUpload
{
    /// <summary>The site keeps an uploaded replay this long (its privacy policy).</summary>
    public const int KeepDays = 180;

    /// <summary>The upload text of a replay, or null when it does not pass the Share filter.</summary>
    public static string? Prepare(string text, CardDb cards)
    {
        if (ReplayImport.Sanitize(text, cards, out _, dateOnly: true) is not { } clean)
        {
            return null;
        }

        clean = WithoutPartner(clean);
        var lines = clean.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var constructed = lines.Length > 1 && lines[1].Contains("\"kind\":\"constructed\"", StringComparison.Ordinal);
        var kept = lines.Where((line, index) => index < 2 || !constructed || !(line.StartsWith("N ", StringComparison.Ordinal) || line.StartsWith("K ", StringComparison.Ordinal)));
        return string.Join('\n', kept);
    }

    /// <summary>
    /// The site reads formats up to 4 (docs/web-replays.md): a version 5 file loses our Duos partner's table (side P,
    /// its tag lines and the steps that name it) and goes as version 4. The player's own copy keeps it.
    /// </summary>
    internal static string WithoutPartner(string clean)
    {
        if (!clean.StartsWith(ReplayFile.Magic5 + "\n", StringComparison.Ordinal) || ReplayFile.Read(clean) is not { } read)
        {
            return clean;
        }

        var (header, body) = read;

        var partner = body.Where(ReplayFile.HasPartner).Select(l => l.Split(' ')[1]).ToHashSet(StringComparer.Ordinal);
        var kept = body.Where(line =>
        {
            var t = line.Split(' ');
            return t[0] switch
            {
                "E" or "X" or "Y" or "Z" => !partner.Contains(t[1]),
                "V" => t[1] == "L" || !partner.Contains(t[1]),
                "M" => !t.Skip(2).Any(partner.Contains),
                _ => true,
            };
        }).ToList();
        return ReplayFile.Write(header with { Events = Math.Max(0, header.Events - (body.Count - kept.Count)) }, kept);
    }

    /// <summary>Gzip, then base64: the body the site takes.</summary>
    public static string Encode(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return Convert.ToBase64String(buffer.ToArray());
    }
}

/// <summary>
/// The replays this app uploaded (<c>replays\uploads.json</c>), by <see cref="KeyOf"/>. Thread-safe; written through a
/// temporary file, so a broken write never loses the delete tokens; failures are logged. Call it off the UI thread.
/// </summary>
public sealed class ReplayUploads(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    /// <summary>
    /// The key of a game: Recent and Saved hold the same game under the same file name (Saved is its starred copy),
    /// so they share it; Imported replays are never uploaded.
    /// </summary>
    public static string KeyOf(ReplayShelf shelf, string fileName) =>
        (shelf == ReplayShelf.Imported ? "imported/" : "games/") + fileName;

    /// <summary>Every upload, read once (for a whole library tab).</summary>
    public IReadOnlyDictionary<string, ReplayUploadResult> All()
    {
        lock (_gate)
        {
            return Load();
        }
    }

    public ReplayUploadResult? Get(string key)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(key);
        }
    }

    public void Set(string key, ReplayUploadResult? upload)
    {
        lock (_gate)
        {
            var all = Load();
            if (upload is null)
            {
                all.Remove(key);
            }
            else
            {
                all[key] = upload;
            }

            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Error("Saving the uploaded replays failed", ex);
                try
                {
                    File.Delete(temp);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    Diag.Info($"Leftover {temp}: {cleanup.Message}");
                }
            }
        }
    }

    private Dictionary<string, ReplayUploadResult> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, ReplayUploadResult>>(File.ReadAllText(path), Json) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Diag.Error("Reading the uploaded replays failed", ex);
            return new(StringComparer.Ordinal);
        }
    }
}
