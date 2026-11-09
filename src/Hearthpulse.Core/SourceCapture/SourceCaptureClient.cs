using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hearthpulse.Core.SourceCapture;

/// <summary>Strict receipt validation. No status code alone acknowledges local source or completed deletion.</summary>
public sealed class SourceCaptureClient(HttpClient http)
{
    public const int NoticeVersion = 1;

    /// <summary>The existing opt-out plus a freshly shown source notice and configured endpoint authorize source.</summary>
    public static bool Sharing(AppSettings settings) => !settings.GameStatsOff && settings.SourceCaptureNoticeVersion >= NoticeVersion
        && Hearthpulse.Core.Pool.PoolClient.Available(settings);

    /// <summary>Reads technical processing status only; source contents and arbitrary server fields never escape.</summary>
    public async Task<SourceCaptureRemoteStatus?> GetStatusAsync(string endpoint, string iid, string generation, string captureId, CancellationToken cancellationToken = default)
    {
        if (!SourceCaptureTokens.Hex(iid, 32) || !SourceCaptureTokens.Hex(generation, 32) || !SourceCaptureTokens.Hex(captureId, 32))
            throw new ArgumentException("Invalid capture identity.", nameof(captureId));
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Invalid capture endpoint.", nameof(endpoint));
        SourceCaptureRemoteStatus? result = null;
        var answer = await PostAsync(endpoint, "status", new { v = 1, iid, generation, captureId }, root =>
        {
            if (!root.TryGetProperty("captureId", out var id) || id.GetString() != captureId
                || !root.TryGetProperty("status", out var status) || status.GetString() is not ("stored" or "processing" or "parsed" or "partial" or "failed" or "deleted")
                || !Count(root, "records", out var records) || !Count(root, "unsupported", out var unsupported) || !Count(root, "gaps", out var gaps)
                || !root.TryGetProperty("parserVersion", out var parser)) return false;
            var version = parser.ValueKind == JsonValueKind.Null ? null : parser.GetString();
            if (version is not null && (version.Length is 0 or > 80 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))) return false;
            result = new(status.GetString()!, version, records, unsupported, gaps);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        return answer.Accepted ? result : null;
    }

    private static bool Count(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var number) && number.TryGetInt32(out value) && value >= 0;
    }

    /// <summary>Posts exact persisted bytes, compressed off the caller thread by the spool sender.</summary>
    internal async Task<SourceCaptureAnswer> ChunkAsync(string endpoint, SourceCaptureManifest manifest, SourceCaptureChunk chunk, byte[] bytes, CancellationToken token)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) await gzip.WriteAsync(bytes, token).ConfigureAwait(false);
        return await PostAsync(endpoint, "chunks", new
        {
            v = 1, manifest.Iid, manifest.Generation, manifest.CaptureId, chunk.Index, chunk.Sha256,
            encoding = "gzip", chunk.UncompressedBytes, data = Convert.ToBase64String(compressed.ToArray()),
        }, root => True(root, "stored") && root.TryGetProperty("index", out var index) && index.TryGetInt32(out var value) && value == chunk.Index
            && root.TryGetProperty("sha256", out var hash) && hash.GetString() == chunk.Sha256, token).ConfigureAwait(false);
    }

    internal Task<SourceCaptureAnswer> CompleteAsync(string endpoint, SourceCaptureManifest manifest, CancellationToken token) =>
        PostAsync(endpoint, "complete", new
        {
            v = 1, manifest.Iid, manifest.Generation, manifest.CaptureId, manifest.CaptureKind, manifest.Mode, manifest.Day,
            manifest.SourceVersion, manifest.CaptureStatus, chunks = manifest.Chunks.Select(c => new { c.Index, c.Sha256, c.UncompressedBytes }), manifest.Gaps,
        }, root => root.TryGetProperty("status", out var status) && status.GetString() == "stored"
            && root.TryGetProperty("captureId", out var id) && id.GetString() == manifest.CaptureId, token);

    internal Task<SourceCaptureAnswer> ForgetAsync(SourceCaptureRevocation revocation, CancellationToken token) =>
        PostAsync(revocation.Endpoint, "forget", new { v = 1, revocation.Iid, revocation.Generation },
            root => root.TryGetProperty("status", out var status) && status.GetString() == "deleted", token, deletion: true);

    private async Task<SourceCaptureAnswer> PostAsync(string endpoint, string route, object body, Func<JsonElement, bool> receipt, CancellationToken token, bool deletion = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/v1/captures/" + route);
            request.Content = JsonContent.Create(body, options: SourceCaptureRecord.Json);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new(false, (int)response.StatusCode is 400 or 409 or 410 or 413 ? "rejected" : "offline", Retry(response));
            // A hostile/misconfigured endpoint cannot force an unbounded response allocation.
            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var block = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(block, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > 64 * 1024) return new(false, "invalid_receipt", TimeSpan.Zero);
                await buffer.WriteAsync(block.AsMemory(0, read), token).ConfigureAwait(false);
            }
            using var json = JsonDocument.Parse(buffer.ToArray());
            var root = json.RootElement;
            var accepted = True(root, "ok") && receipt(root) && (!deletion || (int)response.StatusCode == 200);
            return new(accepted, accepted ? "stored" : deletion ? "deleting" : "invalid_receipt", Retry(response));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            return new(false, "offline", TimeSpan.FromSeconds(30));
        }
    }

    private static bool True(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static TimeSpan Retry(HttpResponseMessage response) => response.Headers.RetryAfter?.Delta
        ?? (response.Headers.RetryAfter?.Date is { } date ? TimeSpan.FromSeconds(Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 1, 3600)) : TimeSpan.FromSeconds(30));
}

internal sealed record SourceCaptureAnswer(bool Accepted, string Status, TimeSpan RetryAfter);
/// <summary>Aggregate processing status without source records or installation identifiers.</summary>
public sealed record SourceCaptureRemoteStatus(string Status, string? ParserVersion, int Records, int Unsupported, int Gaps);
internal sealed record SourceCaptureRevocation(string Iid, string Generation, string Endpoint);
internal sealed record SourceCaptureChunk(int Index, string Sha256, int UncompressedBytes, bool Acknowledged = false);
/// <summary>A spool manifest. SourceVersion is absent from manifests written before v2 and then reads as 1.</summary>
internal sealed record SourceCaptureManifest(string Iid, string Generation, string CaptureId, string CaptureKind, string Mode, int Day,
    string CaptureStatus, int Gaps, bool Finalized, List<SourceCaptureChunk> Chunks, int SourceVersion = 1);
