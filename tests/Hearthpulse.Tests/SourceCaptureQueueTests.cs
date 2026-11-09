using System.Text;
using System.Text.Json;
using Hearthpulse.Core.SourceCapture;

namespace Hearthpulse.Tests;

public sealed class SourceCaptureQueueTests
{
    [Fact]
    public async Task Settings_gate_requires_new_notice_and_endpoint_and_opt_out_cannot_auto_resume()
    {
        using var scratch = new SourceScratch(); await using var queue = await SourceCaptureQueue.OpenAsync(scratch.Path);
        var settings = new Hearthpulse.Core.AppSettings { GameStatsNoticeShown = true, PoolEndpoint = SourceScratch.Endpoint };
        await queue.ConfigureAsync(settings); Assert.False(queue.Capturing); Assert.Null(queue.Generation);
        settings.SourceCaptureNoticeVersion = SourceCaptureClient.NoticeVersion; await queue.ConfigureAsync(settings); Assert.True(queue.Capturing);
        var generation = queue.Generation; settings.PoolEndpoint = null; await queue.ConfigureAsync(settings); Assert.False(queue.Capturing); Assert.Equal(generation, queue.Generation);
        settings.PoolEndpoint = SourceScratch.Endpoint; await queue.ConfigureAsync(settings); Assert.True(queue.Capturing); Assert.Equal(generation, queue.Generation);
        settings.GameStatsOff = true; await queue.ConfigureAsync(settings); Assert.False(queue.Capturing); Assert.Null(queue.Generation); Assert.Equal(1, queue.State.PendingDeletions);
        settings.GameStatsOff = false; await queue.ConfigureAsync(settings); Assert.False(queue.Capturing);
        await queue.ConfigureAsync(settings, explicitEnable: true); Assert.True(queue.Capturing); Assert.NotEqual(generation, queue.Generation);
    }

    [Fact]
    public async Task Interrupted_source_and_torn_tail_recover_with_contiguous_shutdown_gap_and_incomplete_manifest()
    {
        using var scratch = new SourceScratch();
        await using (var queue = await scratch.Open())
        {
            Assert.True(queue.TryAppend(SourceScratch.Frame(0, "start"))); Assert.True(queue.TryAppend(SourceScratch.Frame(1, "tag")));
            await queue.FlushAsync();
        }
        var source = Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories).Single();
        File.AppendAllText(source, "{\"torn_private_data");
        await using var recovered = await SourceCaptureQueue.OpenAsync(scratch.Path);
        var lines = File.ReadAllLines(source).Select(s => JsonDocument.Parse(s)).ToArray();
        try
        {
            Assert.Equal(new[] { "start", "tag", "gap", "end" }, lines.Select(d => d.RootElement.GetProperty("kind").GetString()));
            Assert.Equal(new[] { 0, 1, 2, 3 }, lines.Select(d => d.RootElement.GetProperty("seq").GetInt32()));
            Assert.Equal("shutdown", lines[2].RootElement.GetProperty("data").GetProperty("reason").GetString());
            Assert.Equal("incomplete", lines[3].RootElement.GetProperty("data").GetProperty("status").GetString());
            Assert.DoesNotContain("private", File.ReadAllText(source));
            using var manifest = scratch.Manifest(); Assert.True(manifest.RootElement.GetProperty("finalized").GetBoolean()); Assert.Equal(1, manifest.RootElement.GetProperty("gaps").GetInt32());
        }
        finally { foreach (var line in lines) line.Dispose(); }
    }

    [Fact]
    public async Task Finalized_chunks_have_exact_utf8_lf_hash_and_restart_keeps_the_same_generation()
    {
        using var scratch = new SourceScratch(); string? generation;
        await using (var queue = await scratch.Open()) { generation = queue.Generation; SourceScratch.Complete(queue); await queue.FlushAsync(); }
        await using var reopened = await SourceCaptureQueue.OpenAsync(scratch.Path);
        Assert.Equal(generation, reopened.Generation); Assert.Equal(1, reopened.State.PendingCaptures);
        var source = File.ReadAllBytes(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories).Single());
        Assert.Equal((byte)'\n', source[^1]); Assert.False(source.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        using var manifest = scratch.Manifest(); var chunk = manifest.RootElement.GetProperty("chunks")[0];
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(source)), chunk.GetProperty("sha256").GetString());
        Assert.Equal(source.Length, chunk.GetProperty("uncompressedBytes").GetInt32());
        Assert.Equal("complete", manifest.RootElement.GetProperty("captureStatus").GetString());
    }

    [Fact]
    public async Task Spool_budget_and_oversized_line_drop_explicitly_and_cannot_complete()
    {
        using var scratch = new SourceScratch();
        await using var queue = await scratch.Open(maxBytes: 2000);
        queue.TryAppend(SourceScratch.Frame(0, "start"));
        queue.TryAppend(SourceScratch.Frame(1, "tag", new { tag = "ATK", value = new string('x', 70_000) }));
        queue.TryAppend(SourceScratch.Frame(2, "end")); await queue.FlushAsync();
        Assert.True(queue.State.Dropped > 0); Assert.Equal(0, queue.State.PendingCaptures); Assert.Empty(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Failed_disk_write_is_reported_and_never_publishes_a_complete_capture()
    {
        using var scratch = new SourceScratch(); await using var queue = await scratch.Open();
        File.WriteAllText(System.IO.Path.Combine(scratch.Path, SourceScratch.CaptureId), "obstruction");
        queue.TryAppend(SourceScratch.Frame(0, "start")); await queue.FlushAsync();
        Assert.True(queue.State.Dropped > 0); Assert.Equal(0, queue.State.PendingCaptures);
        Assert.Empty(Directory.GetFiles(scratch.Path, "manifest.json", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Forget_survives_restart_and_automatic_startup_cannot_create_a_new_generation()
    {
        using var scratch = new SourceScratch(); string? old;
        await using (var queue = await scratch.Open())
        {
            old = queue.Generation; SourceScratch.Complete(queue); await queue.FlushAsync();
            await queue.ConfigureAsync(SourceScratch.Iid, SourceScratch.Endpoint, sharing: false);
            Assert.Empty(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        }
        await using var restarted = await SourceCaptureQueue.OpenAsync(scratch.Path);
        Assert.Equal(1, restarted.State.PendingDeletions); Assert.Null(restarted.Generation);
        await restarted.ConfigureAsync(SourceScratch.Iid, SourceScratch.Endpoint, sharing: true);
        Assert.Null(restarted.Generation); Assert.False(restarted.TryAppend(SourceScratch.Frame(0, "start")));
        await restarted.ConfigureAsync(SourceScratch.Iid, SourceScratch.Endpoint, sharing: true, explicitEnable: true);
        Assert.NotNull(restarted.Generation); Assert.NotEqual(old, restarted.Generation); Assert.Equal(1, restarted.State.PendingDeletions);
    }

    [Fact]
    public async Task Modified_source_is_quarantined_instead_of_declared_complete_after_restart()
    {
        using var scratch = new SourceScratch();
        await using (var queue = await scratch.Open()) { SourceScratch.Complete(queue); await queue.FlushAsync(); }
        File.AppendAllText(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories).Single(), "tampered\n");
        await using var reopened = await SourceCaptureQueue.OpenAsync(scratch.Path);
        Assert.Equal(0, reopened.State.PendingCaptures); Assert.True(reopened.State.Dropped > 0);
        await reopened.ConfigureAsync(SourceScratch.Iid, SourceScratch.Endpoint, false);
        Assert.Empty(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Expired_local_source_is_dropped_before_any_network_upload_and_loss_count_is_durable()
    {
        using var scratch = new SourceScratch(); var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using (var queue = await scratch.Open(time: time))
        {
            SourceScratch.Complete(queue); await queue.FlushAsync();
            time.SetUtcNow(new DateTimeOffset(Directory.GetCreationTimeUtc(System.IO.Path.Combine(scratch.Path, SourceScratch.CaptureId))) + TimeSpan.FromDays(15));
            using var http = new HttpClient(new NeverSend()); await queue.SendPendingAsync(new(http));
            Assert.Equal(0, queue.State.PendingCaptures); Assert.Equal(1, queue.State.Dropped);
            Assert.Empty(Directory.GetFiles(scratch.Path, "*.jsonl", SearchOption.AllDirectories));
        }
        await using var reopened = await SourceCaptureQueue.OpenAsync(scratch.Path); Assert.Equal(1, reopened.State.Dropped);
    }

    private sealed class NeverSend : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new NotSupportedException("Expired source must not reach HTTP.");
    }
}

internal sealed class SourceScratch : IDisposable
{
    internal const string Iid = "11111111111111111111111111111111";
    internal const string CaptureId = "22222222222222222222222222222222";
    internal const string Endpoint = "https://capture.test";
    internal string Path { get; } = TestDataFolder.Scratch("hp-source");
    internal async Task<SourceCaptureQueue> Open(long maxBytes = 10 * 1024 * 1024, TimeProvider? time = null)
    {
        var queue = await SourceCaptureQueue.OpenAsync(Path, maxBytes, pendingRecords: 20000, timeProvider: time);
        await queue.ConfigureAsync(Iid, Endpoint, sharing: true); return queue;
    }
    internal JsonDocument Manifest() => JsonDocument.Parse(File.ReadAllBytes(Directory.GetFiles(Path, "manifest.json", SearchOption.AllDirectories).Single()));
    internal static SourceCaptureFrame Frame(int seq, string kind, object? data = null) => new(CaptureId, "game", "ranked", 20766,
        new(seq, kind, 0, "none", 0, null, 0, data ?? kind switch
        {
            "start" => new { captureKind = "game", mode = "ranked", day = 20766, build = (int?)null },
            "end" => new { status = "complete", result = "W", placement = (int?)null, reason = "normal" },
            _ => new { tag = "TURN", value = 1 },
        }));
    internal static void Complete(SourceCaptureQueue queue) { queue.TryAppend(Frame(0, "start")); queue.TryAppend(Frame(1, "tag")); queue.TryAppend(Frame(2, "end")); }
    public void Dispose() => TestDataFolder.TryDelete(Path);
}
