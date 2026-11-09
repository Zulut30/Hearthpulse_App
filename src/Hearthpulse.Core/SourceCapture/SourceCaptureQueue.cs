using System.Globalization;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Hearthpulse.Core.SourceCapture;

/// <summary>Bounded background disk spool. One owner per directory; HTTP never runs under the disk gate.</summary>
public sealed class SourceCaptureQueue : IAsyncDisposable
{
    public const int TargetChunkBytes = 1024 * 1024;
    public const int MaxChunkBytes = 4 * 1024 * 1024;
    public const int MaxLineBytes = 64 * 1024;
    public const int MaxChunks = 64;
    private readonly string _root;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _disk = new(1);
    private readonly SemaphoreSlim _sender = new(1);
    private readonly Channel<Command> _commands;
    private readonly Task _pump;
    private readonly Lock _gate = new();
    private StoreState _store = new(null, null, null, false, 0, []);
    private string? _acceptGeneration;
    private bool _disposed;
    private long _dropped;
    private string _status = "disabled";
    private DateTimeOffset _retryAt;
    private readonly ConcurrentDictionary<string, SourceCaptureManifest> _manifests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    private SourceCaptureQueue(string root, long maxBytes, int capacity, TimeProvider time)
    {
        _root = Path.GetFullPath(root); _maxBytes = maxBytes; _time = time;
        _commands = Channel.CreateBounded<Command>(new BoundedChannelOptions(capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Loads durable generation/revocations and seals crash-interrupted sources as incomplete, off the UI thread.</summary>
    public static async Task<SourceCaptureQueue> OpenAsync(string directory, long maxBytes = 2L * 1024 * 1024 * 1024, int pendingRecords = 4096, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pendingRecords, 1);
        var queue = new SourceCaptureQueue(directory, maxBytes, pendingRecords, timeProvider ?? TimeProvider.System);
        try { await Task.Run(queue.Load).ConfigureAwait(false); return queue; }
        catch { await queue.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Safe aggregate state. Dropped remains durable across restarts.</summary>
    public SourceCaptureState State
    {
        get { lock (_gate) return new(_status, _dropped, _manifests.Count, _store.Revocations.Count); }
    }
    public string? Generation { get { lock (_gate) return _store.Generation; } }
    /// <summary>Settings were applied and this queue currently admits new source; use to gate GameSession.</summary>
    public bool Capturing { get { lock (_gate) return _acceptGeneration is not null; } }

    /// <summary>Both hosts use this overload: opt-out revokes; a missing notice or endpoint only pauses admission/delivery.</summary>
    public Task ConfigureAsync(AppSettings settings, bool explicitEnable = false) => ConfigureAsync(
        Hearthpulse.Core.GameStats.GameStatsClient.InstallId(settings),
        settings.SourceCaptureNoticeVersion >= SourceCaptureClient.NoticeVersion ? Hearthpulse.Core.Pool.PoolClient.Endpoint(settings) : null,
        sharing: !settings.GameStatsOff, explicitEnable);

    /// <summary>
    /// Persist lifecycle settings before attaching a recorder. A revoked generation is never resumed; only explicitEnable creates its successor.
    /// Call with sharing=false for opt-out; missing notice/endpoint pauses capture without granting consent.
    /// </summary>
    public async Task ConfigureAsync(string iid, string? endpoint, bool sharing, bool explicitEnable = false)
    {
        if (!SourceCaptureTokens.Hex(iid, 32)) throw new ArgumentException("Invalid source install key.", nameof(iid));
        var available = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
        lock (_gate) _acceptGeneration = null; // Immediately stop parser admission; no UI thread I/O.
        await FlushAsync().ConfigureAwait(false);
        await Task.Run(async () =>
        {
            await _disk.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!sharing)
                {
                    if (_store.Generation is { } generation && _store.Endpoint is { } oldEndpoint && _store.Iid is { } oldIid)
                    {
                        var revoked = _store.Revocations.ToList();
                        revoked.Add(new(oldIid, generation, oldEndpoint));
                        _store = _store with { Generation = null, Enabled = false, Revocations = revoked };
                        SaveStore(); // Tombstone first, source removal second. Never regenerated by startup.
                    }
                    // Include quarantined/corrupt sources omitted from the sendable manifest inventory.
                    foreach (var directory in Directory.GetDirectories(_root))
                    {
                        var id = Path.GetFileName(directory);
                        if (SourceCaptureTokens.Hex(id, 32)) DeleteCapture(id);
                    }
                    SetStatus("disabled");
                }
                else if (available)
                {
                    if (_store.Generation is null && (_store.Iid is null || explicitEnable))
                        _store = _store with { Iid = iid, Generation = Guid.NewGuid().ToString("N"), Endpoint = endpoint!.TrimEnd('/'), Enabled = true };
                    // Identity/endpoint changes must be handled as opt-out then explicit enable; never move old source to another endpoint.
                    if (_store.Iid == iid && _store.Endpoint == endpoint!.TrimEnd('/') && _store.Generation is not null)
                        _store = _store with { Enabled = true };
                    else _store = _store with { Enabled = false };
                    SaveStore();
                    lock (_gate) _acceptGeneration = _store.Enabled ? _store.Generation : null;
                    SetStatus(_store.Enabled ? "ready" : "disabled");
                }
                else
                {
                    _store = _store with { Enabled = false }; SaveStore(); SetStatus("unavailable");
                }
            }
            finally { _disk.Release(); }
        }).ConfigureAwait(false);
    }

    /// <summary>Non-blocking parser callback. Overflow drops a whole source, never emits a falsely complete source.</summary>
    public bool TryAppend(SourceCaptureFrame frame)
    {
        lock (_gate)
        {
            if (_disposed || _acceptGeneration is null) return false;
            if (_commands.Writer.TryWrite(new(frame, _acceptGeneration, null))) return true;
            _failed.Add(frame.CaptureId); _dropped++; _status = "dropped_local";
            return false;
        }
    }

    /// <summary>Waits for already-admitted disk work. Does not send anything.</summary>
    public async Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(new(null, null, done)).ConfigureAwait(false);
        await done.Task.ConfigureAwait(false);
    }

    private async Task PumpAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await _disk.WaitAsync().ConfigureAwait(false);
            try
            {
                if (command.Frame is { } frame && command.Generation == _store.Generation)
                {
                    bool failed;
                    lock (_gate) failed = _failed.Contains(frame.CaptureId);
                    if (failed) DeleteCapture(frame.CaptureId);
                    else Append(frame);
                }
                if (command.Done is not null || _store.Dropped != Interlocked.Read(ref _dropped)) SaveStore();
                command.Done?.TrySetResult();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                if (command.Frame is { } frame) { lock (_gate) _failed.Add(frame.CaptureId); }
                Interlocked.Increment(ref _dropped); SetStatus("disk_error");
                command.Done?.TrySetException(new IOException("Source spool is unavailable."));
            }
            finally { _disk.Release(); }
        }
    }

    private void Append(SourceCaptureFrame frame)
    {
        if (!SourceCaptureTokens.Hex(frame.CaptureId, 32)) throw new InvalidDataException("Invalid capture key.");
        var bytes = Encoding.UTF8.GetBytes(frame.Record.ToJson() + "\n");
        if (bytes.Length > MaxLineBytes || DirectoryBytes() + bytes.Length > _maxBytes)
        {
            Drop(frame.CaptureId, "dropped_local"); return;
        }
        if (!_manifests.TryGetValue(frame.CaptureId, out var manifest))
        {
            if (frame.Record.Kind != "start" || frame.Record.Seq != 0) { Drop(frame.CaptureId, "invalid"); return; }
            Directory.CreateDirectory(CaptureDir(frame.CaptureId));
            manifest = new(_store.Iid!, _store.Generation!, frame.CaptureId, frame.CaptureKind, frame.Mode, frame.Day, "incomplete", 0, false, []);
            _manifests[frame.CaptureId] = manifest;
            SaveManifest(manifest);
        }
        if (manifest.Finalized) { Drop(frame.CaptureId, "invalid"); return; }
        var files = ChunkFiles(frame.CaptureId);
        var index = files.Length == 0 ? 0 : int.Parse(Path.GetFileNameWithoutExtension(files[^1]), CultureInfo.InvariantCulture);
        var path = ChunkPath(frame.CaptureId, index);
        if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > TargetChunkBytes) path = ChunkPath(frame.CaptureId, ++index);
        // Leave room for recovery gap/end in the final chunk.
        if (index >= MaxChunks || index == MaxChunks - 1 && new FileInfo(path).Exists && new FileInfo(path).Length + bytes.Length > MaxChunkBytes - MaxLineBytes)
        { Drop(frame.CaptureId, "dropped_local"); return; }
        using (var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(flushToDisk: true); }
        if (frame.Record.Kind == "end") FinalizeCapture(manifest);
        SetStatus("queued");
    }

    private void Drop(string id, string status)
    {
        lock (_gate) _failed.Add(id);
        Interlocked.Increment(ref _dropped); SetStatus(status); DeleteCapture(id);
    }

    private void Load()
    {
        Directory.CreateDirectory(_root);
        if (File.Exists(StorePath)) _store = Read<StoreState>(StorePath);
        _dropped = _store.Dropped;
        // Startup never accepts new captures until settings/new notice have been checked by the host.
        foreach (var directory in Directory.GetDirectories(_root))
        {
            var id = Path.GetFileName(directory);
            if (!SourceCaptureTokens.Hex(id, 32)) continue;
            try
            {
                var manifest = Read<SourceCaptureManifest>(Path.Combine(directory, "manifest.json"));
                if (manifest.Generation != _store.Generation || manifest.Iid != _store.Iid || manifest.CaptureId != id)
                { Directory.Delete(directory, recursive: true); continue; }
                _manifests[id] = manifest;
                if (!manifest.Finalized) Recover(manifest);
                else ValidateFiles(manifest);
                if (_time.GetUtcNow() - new DateTimeOffset(Directory.GetCreationTimeUtc(directory)) > TimeSpan.FromDays(14)) Drop(id, "expired_local");
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            { Interlocked.Increment(ref _dropped); SetStatus("corrupt_local"); _failed.Add(id); _manifests.TryRemove(id, out _); }
        }
        SaveStore();
    }

    private void Recover(SourceCaptureManifest manifest)
    {
        var files = ChunkFiles(manifest.CaptureId);
        if (files.Length == 0) { Drop(manifest.CaptureId, "dropped_local"); return; }
        var last = files[^1];
        var bytes = ReadChunk(last);
        var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        using (var trim = new FileStream(last, FileMode.Open, FileAccess.Write)) { trim.SetLength(end); trim.Flush(true); }
        string? lastLine = null;
        foreach (var path in files)
        {
            if (new FileInfo(path).Length > MaxChunkBytes) throw new InvalidDataException("Source chunk limit.");
            foreach (var line in File.ReadLines(path))
                if (line.Length > 0) lastLine = line;
        }
        if (lastLine is null) { Drop(manifest.CaptureId, "dropped_local"); return; }
        using var tail = JsonDocument.Parse(lastLine);
        if (tail.RootElement.GetProperty("kind").GetString() != "end")
        {
            var seq = tail.RootElement.GetProperty("seq").GetInt32() + 1;
            var suffix = new SourceCaptureRecord(seq, "gap", 0, "none", 0, null, 0, new { reason = "shutdown", count = 1 }).ToJson() + "\n"
                + new SourceCaptureRecord(seq + 1, "end", 0, "none", 0, null, 0, new { status = "incomplete", result = (string?)null, placement = (int?)null, reason = "shutdown" }).ToJson() + "\n";
            using var append = new FileStream(last, FileMode.Append, FileAccess.Write); append.Write(Encoding.UTF8.GetBytes(suffix)); append.Flush(true);
        }
        FinalizeCapture(manifest);
    }

    private void FinalizeCapture(SourceCaptureManifest manifest)
    {
        var chunks = new List<SourceCaptureChunk>(); var seq = 0; var gaps = 0; var status = "incomplete";
        foreach (var path in ChunkFiles(manifest.CaptureId))
        {
            var bytes = ReadChunk(path);
            if (bytes.Length is 0 or > MaxChunkBytes || chunks.Count >= MaxChunks) throw new InvalidDataException("Source chunk limit.");
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (Encoding.UTF8.GetByteCount(line) + 1 > MaxLineBytes) throw new InvalidDataException("Source line limit.");
                using var json = JsonDocument.Parse(line); var root = json.RootElement;
                if (root.GetProperty("seq").GetInt32() != seq++) throw new InvalidDataException("Source order.");
                if (seq == 1 && root.GetProperty("kind").GetString() != "start") throw new InvalidDataException("Source start missing.");
                if (root.GetProperty("kind").GetString() == "gap") gaps += root.GetProperty("data").GetProperty("count").GetInt32();
                if (root.GetProperty("kind").GetString() == "end") status = root.GetProperty("data").GetProperty("status").GetString()!;
            }
            chunks.Add(new(chunks.Count, Hash(bytes), bytes.Length));
        }
        var updated = manifest with { Chunks = chunks, Gaps = gaps, CaptureStatus = gaps == 0 && status == "complete" ? "complete" : "incomplete", Finalized = true };
        SaveManifest(updated); _manifests[manifest.CaptureId] = updated;
    }

    private void ValidateFiles(SourceCaptureManifest manifest)
    {
        if (manifest.Chunks.Count is 0 or > MaxChunks) throw new InvalidDataException("Invalid chunk count.");
        foreach (var chunk in manifest.Chunks.Where(c => !c.Acknowledged))
        {
            var bytes = ReadChunk(ChunkPath(manifest.CaptureId, chunk.Index));
            if (bytes.Length != chunk.UncompressedBytes || Hash(bytes) != chunk.Sha256) throw new InvalidDataException("Source hash mismatch.");
        }
        // Receipt was atomically persisted before unlink; finish a crash-interrupted unlink.
        foreach (var chunk in manifest.Chunks.Where(c => c.Acknowledged)) File.Delete(ChunkPath(manifest.CaptureId, chunk.Index));
    }

    /// <summary>One background delivery pass. Retains failures, honors Retry-After; caller may await from its timer loop.</summary>
    public Task SendPendingAsync(SourceCaptureClient client, CancellationToken cancellationToken = default) => Task.Run(() => SendCoreAsync(client, cancellationToken), cancellationToken);

    private async Task SendCoreAsync(SourceCaptureClient client, CancellationToken token)
    {
        await _sender.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_time.GetUtcNow() < _retryAt) return;
            foreach (var revocation in _store.Revocations.ToArray())
            {
                var answer = await client.ForgetAsync(revocation, token).ConfigureAwait(false);
                if (!answer.Accepted) { Backoff(answer); return; }
                await _disk.WaitAsync(token).ConfigureAwait(false);
                try { _store = _store with { Revocations = _store.Revocations.Where(r => r != revocation).ToList() }; SaveStore(); }
                finally { _disk.Release(); }
            }
            SourceCaptureManifest[] captures;
            await _disk.WaitAsync(token).ConfigureAwait(false);
            try
            {
                PruneExpired();
                captures = _manifests.Values.Where(m => m.Finalized).ToArray();
            }
            finally { _disk.Release(); }
            foreach (var capture in captures)
            {
                var endpoint = _store.Endpoint!; // Bind destination to this generation, never read a replacement endpoint after HTTP starts.
                foreach (var chunk in capture.Chunks.Where(c => !c.Acknowledged))
                {
                    byte[] bytes;
                    await _disk.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!CanSend(capture)) return;
                        if (new FileInfo(ChunkPath(capture.CaptureId, chunk.Index)).Length != chunk.UncompressedBytes) { SetStatus("corrupt_local"); return; }
                        bytes = await File.ReadAllBytesAsync(ChunkPath(capture.CaptureId, chunk.Index), token).ConfigureAwait(false);
                        if (bytes.Length != chunk.UncompressedBytes || Hash(bytes) != chunk.Sha256) { SetStatus("corrupt_local"); return; }
                    }
                    finally { _disk.Release(); }
                    var answer = await client.ChunkAsync(endpoint, capture, chunk, bytes, token).ConfigureAwait(false);
                    if (!answer.Accepted) { Backoff(answer); return; }
                    await _disk.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        if (!CanSend(capture)) return;
                        var current = _manifests[capture.CaptureId];
                        var updated = current with { Chunks = current.Chunks.Select(c => c.Index == chunk.Index ? c with { Acknowledged = true } : c).ToList() };
                        SaveManifest(updated); _manifests[capture.CaptureId] = updated;
                        File.Delete(ChunkPath(capture.CaptureId, chunk.Index));
                    }
                    finally { _disk.Release(); }
                }
                if (!CanSend(capture)) return;
                var complete = await client.CompleteAsync(endpoint, capture, token).ConfigureAwait(false);
                if (!complete.Accepted) { Backoff(complete); return; }
                await _disk.WaitAsync(token).ConfigureAwait(false);
                try { if (CanSend(capture)) DeleteCapture(capture.CaptureId); }
                finally { _disk.Release(); }
            }
            SetStatus(_store.Enabled ? "idle" : "disabled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { SetStatus("disk_error"); }
        finally { _sender.Release(); }
    }

    private bool CanSend(SourceCaptureManifest capture)
    {
        lock (_gate) return _acceptGeneration == capture.Generation && _store.Enabled && _store.Generation == capture.Generation
            && !_failed.Contains(capture.CaptureId) && _manifests.ContainsKey(capture.CaptureId);
    }
    private void PruneExpired()
    {
        foreach (var directory in Directory.GetDirectories(_root))
        {
            var id = Path.GetFileName(directory);
            if (SourceCaptureTokens.Hex(id, 32) && _time.GetUtcNow() - new DateTimeOffset(Directory.GetCreationTimeUtc(directory)) > TimeSpan.FromDays(14))
                Drop(id, "expired_local");
        }
        if (_store.Dropped != Interlocked.Read(ref _dropped)) SaveStore();
    }
    private void Backoff(SourceCaptureAnswer answer) { _retryAt = _time.GetUtcNow() + answer.RetryAfter; SetStatus(answer.Status); }
    private void SetStatus(string status) { lock (_gate) _status = status; }
    private string StorePath => Path.Combine(_root, "state.json");
    private string CaptureDir(string id) => Path.Combine(_root, id);
    private string ChunkPath(string id, int index) => Path.Combine(CaptureDir(id), index.ToString("D4", CultureInfo.InvariantCulture) + ".jsonl");
    private string[] ChunkFiles(string id) => Directory.GetFiles(CaptureDir(id), "*.jsonl").Order(StringComparer.Ordinal).ToArray();
    private long DirectoryBytes() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] ReadChunk(string path)
    {
        if (new FileInfo(path).Length > MaxChunkBytes) throw new InvalidDataException("Source chunk limit.");
        return File.ReadAllBytes(path);
    }
    private void SaveStore() { _store = _store with { Dropped = Interlocked.Read(ref _dropped) }; Atomic(StorePath, _store); }
    private void SaveManifest(SourceCaptureManifest manifest) => Atomic(Path.Combine(CaptureDir(manifest.CaptureId), "manifest.json"), manifest);
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), SourceCaptureRecord.Json) ?? throw new InvalidDataException("Invalid spool metadata.");
    private static void Atomic<T>(string path, T value)
    {
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(file, value, SourceCaptureRecord.Json); file.Flush(true); }
        File.Move(path + ".tmp", path, overwrite: true);
    }
    private void DeleteCapture(string id)
    {
        // IDs are strictly validated at admission/load, and paths remain under the caller-supplied spool root.
        if (!SourceCaptureTokens.Hex(id, 32)) throw new InvalidDataException("Invalid capture key.");
        var directory = CaptureDir(id);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        _manifests.TryRemove(id, out _);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _acceptGeneration = null; }
        _commands.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        await _sender.WaitAsync().ConfigureAwait(false);
        _sender.Release();
        // No implicit HTTP at shutdown. Non-finalized files are recovered with explicit shutdown gaps on next open.
        _disk.Dispose(); _sender.Dispose();
    }

    private sealed record Command(SourceCaptureFrame? Frame, string? Generation, TaskCompletionSource? Done);
    private sealed record StoreState(string? Iid, string? Generation, string? Endpoint, bool Enabled, long Dropped, List<SourceCaptureRevocation> Revocations);
}
