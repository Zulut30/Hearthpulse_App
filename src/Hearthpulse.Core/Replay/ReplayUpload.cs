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
/// base64. The site checks it again (docs/web-replays.md). Uploads are remembered by file name in
/// <c>replays\uploads.json</c>, so the card can show the link and delete the replay from the site.
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

        var lines = clean.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var constructed = lines.Length > 1 && lines[1].Contains("\"kind\":\"constructed\"", StringComparison.Ordinal);
        var kept = lines.Where((line, index) => index < 2 || !constructed || !(line.StartsWith("N ", StringComparison.Ordinal) || line.StartsWith("K ", StringComparison.Ordinal)));
        return string.Join('\n', kept);
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

/// <summary>The replays this app uploaded, by file name (<c>replays\uploads.json</c>). Thread-safe; failures are logged.</summary>
public sealed class ReplayUploads(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Lock _gate = new();

    public static ReplayUploads Default { get; } = new(Path.Combine(AppPaths.Replays, "uploads.json"));

    public ReplayUploadResult? Get(string fileName)
    {
        lock (_gate)
        {
            return Load().GetValueOrDefault(fileName);
        }
    }

    public void Set(string fileName, ReplayUploadResult? upload)
    {
        lock (_gate)
        {
            var all = Load();
            if (upload is null)
            {
                all.Remove(fileName);
            }
            else
            {
                all[fileName] = upload;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(all, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Diag.Error("Saving the uploaded replays failed", ex);
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
