using System.Text.Json;

namespace Hearthpulse.Core.Updates;

/// <summary>
/// Where the updater keeps its small state, the clock it goes by and the keys it trusts: one value so tests pass a temp
/// folder, a fixed clock and their own keys. The folder is &lt;data&gt;/update-state, not &lt;data&gt;/updates (a full
/// download clears every other file there).
/// </summary>
internal sealed record UpdateContext(string StateDirectory, Func<DateTimeOffset> Now, IReadOnlyList<byte[]> Keys)
{
    public static UpdateContext Default() =>
        new(Path.Combine(AppPaths.Root, "update-state"), () => DateTimeOffset.UtcNow, UpdateSignature.EmbeddedKeys);
}

/// <summary>
/// The last 200 answer of releases/latest with its ETag (update-state/latest-release.json), so the next check can ask
/// "changed since?" and a 304 reads the release from here. A file that cannot be read is deleted, and the check then
/// asks without If-None-Match.
/// </summary>
internal static class ReleaseCache
{
    public const string FileName = "latest-release.json";

    public sealed record Entry(string ETag, string Body);

    public static Entry? Read(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("etag", out var etag) && etag.ValueKind == JsonValueKind.String && etag.GetString() is { Length: > 0 } tag
                && doc.RootElement.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String && body.GetString() is { Length: > 0 } text)
            {
                return new Entry(tag, text);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Diag.Info($"Update check: the saved release answer could not be read ({ex.Message}); asking afresh.");
        }

        TryDelete(path);
        return null;
    }

    /// <summary>Keeps the answer with its ETag; an answer without an ETag cannot be asked about again, so nothing is kept.</summary>
    public static void Write(string stateDirectory, string? etag, string body)
    {
        var path = Path.Combine(stateDirectory, FileName);
        if (string.IsNullOrEmpty(etag))
        {
            TryDelete(path);
            return;
        }

        try
        {
            Directory.CreateDirectory(stateDirectory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<string, string> { ["etag"] = etag, ["body"] = body }));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Info($"Update check: the release answer could not be saved ({ex.Message}).");
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Info($"Update state: {Path.GetFileName(path)} could not be deleted ({ex.Message}).");
        }
    }
}
