using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Hearthpulse.Core.SourceCapture;
using Microsoft.Extensions.Time.Testing;

namespace Hearthpulse.Tests;

public sealed class SourceCaptureClientTests
{
    [Fact]
    public async Task Remote_status_returns_only_technical_counts_and_rejects_a_different_capture()
    {
        var calls = 0;
        using var handler = new Fake(request =>
        {
            Assert.EndsWith("/v1/captures/status", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(HttpStatusCode.OK, new { ok = true, status = "parsed", captureId = ++calls == 1 ? SourceScratch.CaptureId : new string('3', 32), parserVersion = "source-v1.0", records = 42, unsupported = 2, gaps = 1, privateData = "do-not-copy" }));
        }); using var http = new HttpClient(handler); var client = new SourceCaptureClient(http);
        Assert.Equal(new SourceCaptureRemoteStatus("parsed", "source-v1.0", 42, 2, 1), await client.GetStatusAsync(SourceScratch.Endpoint, SourceScratch.Iid, new string('4', 32), SourceScratch.CaptureId));
        Assert.Null(await client.GetStatusAsync(SourceScratch.Endpoint, SourceScratch.Iid, new string('4', 32), SourceScratch.CaptureId));
    }

    [Fact]
    public async Task Multiple_chunks_precede_manifest_and_gzip_hash_size_index_match_exact_source_bytes()
    {
        using var scratch = new SourceScratch(); await using var queue = await scratch.Open();
        queue.TryAppend(SourceScratch.Frame(0, "start"));
        var options = Enumerable.Range(0, 64).Select(i => "OPTION_" + i + "_" + new string('X', 60)).ToArray();
        for (var i = 1; i <= 300; i++) queue.TryAppend(SourceScratch.Frame(i, "choice", new { choiceId = i, sourceCard = (string?)null, options, picked = Array.Empty<string>(), resolved = false }));
        queue.TryAppend(SourceScratch.Frame(301, "end")); await queue.FlushAsync();
        var routes = new List<string>(); var hashes = new List<string>(); var records = new List<JsonElement>();
        using var handler = new Fake(async request =>
        {
            routes.Add(request.RequestUri!.AbsolutePath); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = body.RootElement;
            if (request.RequestUri.AbsolutePath.EndsWith("chunks"))
            {
                Assert.Equal(hashes.Count, root.GetProperty("index").GetInt32());
                using var packed = new MemoryStream(Convert.FromBase64String(root.GetProperty("data").GetString()!)); using var gzip = new GZipStream(packed, CompressionMode.Decompress); using var plain = new MemoryStream(); await gzip.CopyToAsync(plain);
                var bytes = plain.ToArray(); Assert.InRange(bytes.Length, 1, SourceCaptureQueue.MaxChunkBytes); Assert.Equal(bytes.Length, root.GetProperty("uncompressedBytes").GetInt32()); Assert.Equal((byte)'\n', bytes[^1]);
                hashes.Add(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))); Assert.Equal(hashes.Last(), root.GetProperty("sha256").GetString());
                foreach (var line in System.Text.Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)) { using var record = JsonDocument.Parse(line); records.Add(record.RootElement.Clone()); }
                return ChunkReceipt(root);
            }
            Assert.Equal(new[] { 0, 1 }, root.GetProperty("chunks").EnumerateArray().Select(c => c.GetProperty("index").GetInt32()));
            Assert.Equal(hashes, root.GetProperty("chunks").EnumerateArray().Select(c => c.GetProperty("sha256").GetString()));
            return Reply(HttpStatusCode.Accepted, new { ok = true, status = "stored", captureId = SourceScratch.CaptureId });
        }); using var http = new HttpClient(handler); await queue.SendPendingAsync(new(http));
        Assert.Equal(new[] { "/v1/captures/chunks", "/v1/captures/chunks", "/v1/captures/complete" }, routes);
        Assert.Equal(Enumerable.Range(0, 302), records.Select(r => r.GetProperty("seq").GetInt32())); Assert.Equal(0, queue.State.PendingCaptures);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(409)]
    [InlineData(410)]
    [InlineData(413)]
    public async Task Permanent_http_rejection_retains_source_and_exposes_rejected_state(int status)
    {
        using var scratch = new SourceScratch(); await using var queue = await scratch.Open(); SourceScratch.Complete(queue); await queue.FlushAsync();
        using var handler = new Fake(_ => Task.FromResult(Reply((HttpStatusCode)status, new { ok = false }))); using var http = new HttpClient(handler);
        await queue.SendPendingAsync(new(http)); Assert.Equal("rejected", queue.State.Status); Assert.Single(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        using var manifest = scratch.Manifest(); Assert.False(manifest.RootElement.GetProperty("chunks")[0].GetProperty("acknowledged").GetBoolean());
    }

    [Fact]
    public async Task Wrong_hash_and_ok_false_receipts_never_delete_source()
    {
        using var scratch = new SourceScratch(); var time = new FakeTimeProvider(); await using var queue = await scratch.Open(time: time); SourceScratch.Complete(queue); await queue.FlushAsync();
        var calls = 0;
        using var handler = new Fake(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); calls++;
            return Reply(HttpStatusCode.Created, new { ok = calls != 2, stored = true, index = 0, sha256 = calls == 1 ? new string('0', 64) : body.RootElement.GetProperty("sha256").GetString() });
        }); using var http = new HttpClient(handler); var client = new SourceCaptureClient(http);
        await queue.SendPendingAsync(client); Assert.Single(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        time.Advance(TimeSpan.FromSeconds(31)); await queue.SendPendingAsync(client); Assert.Single(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        Assert.Equal(2, calls); Assert.Equal("invalid_receipt", queue.State.Status);
    }

    [Fact]
    public async Task Network_loss_and_wrong_receipt_keep_exact_source_until_verified_retry_then_manifest()
    {
        using var scratch = new SourceScratch(); var time = new FakeTimeProvider();
        await using var queue = await scratch.Open(time: time); SourceScratch.Complete(queue); await queue.FlushAsync();
        var original = File.ReadAllBytes(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories).Single());
        var attempts = 0; var routes = new List<string>(); var bodies = new List<byte[]>();
        using var handler = new Fake(async request =>
        {
            routes.Add(request.RequestUri!.AbsolutePath); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); var root = body.RootElement;
            if (request.RequestUri.AbsolutePath.EndsWith("chunks"))
            {
                using var packed = new MemoryStream(Convert.FromBase64String(root.GetProperty("data").GetString()!)); using var gzip = new GZipStream(packed, CompressionMode.Decompress); using var plain = new MemoryStream(); await gzip.CopyToAsync(plain); bodies.Add(plain.ToArray());
                if (++attempts == 1) throw new HttpRequestException("lost response after commit");
                if (attempts == 2) return Reply(HttpStatusCode.OK, new { ok = true, stored = true, index = 17, sha256 = root.GetProperty("sha256").GetString() });
                return ChunkReceipt(root);
            }
            return Reply(HttpStatusCode.Accepted, new { ok = true, status = "stored", captureId = root.GetProperty("captureId").GetString() });
        });
        using var http = new HttpClient(handler); var client = new SourceCaptureClient(http);
        await queue.SendPendingAsync(client); Assert.Equal(1, queue.State.PendingCaptures); Assert.Single(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        time.Advance(TimeSpan.FromSeconds(31)); await queue.SendPendingAsync(client); Assert.Single(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        time.Advance(TimeSpan.FromSeconds(31)); await queue.SendPendingAsync(client);
        Assert.Equal(new[] { "/v1/captures/chunks", "/v1/captures/chunks", "/v1/captures/chunks", "/v1/captures/complete" }, routes);
        Assert.All(bodies, b => Assert.Equal(original, b)); Assert.Equal(0, queue.State.PendingCaptures);
    }

    [Fact]
    public async Task Acknowledged_chunk_survives_restart_as_receipt_and_manifest_retries_without_duplicate_chunk()
    {
        using var scratch = new SourceScratch(); var routes = new List<string>(); var complete = false;
        using var handler = new Fake(async request =>
        {
            routes.Add(request.RequestUri!.AbsolutePath); using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return request.RequestUri.AbsolutePath.EndsWith("chunks") ? ChunkReceipt(json.RootElement)
                : complete ? Reply(HttpStatusCode.Accepted, new { ok = true, status = "stored", captureId = SourceScratch.CaptureId }) : Reply(HttpStatusCode.ServiceUnavailable, new { ok = false });
        });
        using var http = new HttpClient(handler); var client = new SourceCaptureClient(http);
        await using (var queue = await scratch.Open())
        {
            SourceScratch.Complete(queue); await queue.FlushAsync(); await queue.SendPendingAsync(client);
            Assert.Empty(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories)); Assert.Equal(1, queue.State.PendingCaptures);
        }
        complete = true;
        await using var reopened = await SourceCaptureQueue.OpenAsync(scratch.Path); await reopened.ConfigureAsync(SourceScratch.Iid, SourceScratch.Endpoint, true); await reopened.SendPendingAsync(client);
        Assert.Equal(new[] { "/v1/captures/chunks", "/v1/captures/complete", "/v1/captures/complete" }, routes); Assert.Equal(0, reopened.State.PendingCaptures);
    }

    [Fact]
    public async Task Forget_202_is_pending_across_restart_until_200_deleted_and_uploads_stay_off()
    {
        using var scratch = new SourceScratch(); var deleted = false; var calls = 0;
        using var handler = new Fake(request =>
        {
            Assert.EndsWith("/v1/captures/forget", request.RequestUri!.AbsolutePath); calls++;
            return Task.FromResult(Reply(deleted ? HttpStatusCode.OK : HttpStatusCode.Accepted, new { ok = true, status = deleted ? "deleted" : "deleting" }));
        }); using var http = new HttpClient(handler); var client = new SourceCaptureClient(http);
        await using (var queue = await scratch.Open())
        {
            SourceScratch.Complete(queue); await queue.FlushAsync(); await queue.ConfigureAsync(SourceScratch.Iid, SourceScratch.Endpoint, false);
            await queue.SendPendingAsync(client); Assert.Equal(1, queue.State.PendingDeletions); Assert.Equal("deleting", queue.State.Status);
        }
        deleted = true;
        await using var restarted = await SourceCaptureQueue.OpenAsync(scratch.Path); await restarted.SendPendingAsync(client);
        Assert.Equal(0, restarted.State.PendingDeletions); Assert.False(restarted.TryAppend(SourceScratch.Frame(0, "start"))); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Late_upload_receipt_after_opt_out_cannot_complete_or_recreate_source()
    {
        using var scratch = new SourceScratch(); await using var queue = await scratch.Open(); SourceScratch.Complete(queue); await queue.FlushAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var routes = new List<string>();
        using var handler = new Fake(async request =>
        {
            routes.Add(request.RequestUri!.AbsolutePath); using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (request.RequestUri.AbsolutePath.EndsWith("chunks")) { entered.SetResult(); await release.Task; return ChunkReceipt(body.RootElement); }
            return Reply(HttpStatusCode.OK, new { ok = true, status = "deleted" });
        }); using var http = new HttpClient(handler); var client = new SourceCaptureClient(http);
        var sending = queue.SendPendingAsync(client); await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await queue.ConfigureAsync(SourceScratch.Iid, SourceScratch.Endpoint, false); release.SetResult(); await sending;
        Assert.Equal(0, queue.State.PendingCaptures); Assert.Equal(1, queue.State.PendingDeletions);
        Assert.Equal(new[] { "/v1/captures/chunks" }, routes);
        await queue.SendPendingAsync(client); Assert.Equal(0, queue.State.PendingDeletions);
    }

    [Fact]
    public async Task Retry_after_prevents_early_retries_and_keeps_unacknowledged_source()
    {
        using var scratch = new SourceScratch(); var time = new FakeTimeProvider(); await using var queue = await scratch.Open(time: time); SourceScratch.Complete(queue); await queue.FlushAsync();
        var calls = 0;
        using var handler = new Fake(_ => { calls++; var response = Reply(HttpStatusCode.ServiceUnavailable, new { ok = false }); response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120)); return Task.FromResult(response); });
        using var http = new HttpClient(handler); var client = new SourceCaptureClient(http);
        await queue.SendPendingAsync(client); time.Advance(TimeSpan.FromSeconds(119)); await queue.SendPendingAsync(client); Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1)); await queue.SendPendingAsync(client); Assert.Equal(2, calls); Assert.Single(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
    }

    private static HttpResponseMessage ChunkReceipt(JsonElement body) => Reply(HttpStatusCode.Created, new { ok = true, stored = true, duplicate = false, index = body.GetProperty("index").GetInt32(), sha256 = body.GetProperty("sha256").GetString() });
    private static HttpResponseMessage Reply(HttpStatusCode status, object body) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body)) };
    private sealed class Fake(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
}
