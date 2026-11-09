using Hearthpulse.Core.GameStats;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.SourceCapture;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Hosting;

/// <summary>One lifecycle owner for the source spool, live recorder and cancellable background delivery.</summary>
public sealed class SourceCaptureHost : IDisposable, IAsyncDisposable
{
    private readonly AppSettings _settings;
    private readonly GameSession _session;
    private readonly SourceCaptureClient _client;
    private readonly string _directory;
    private readonly Action _persistSettings;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task _configuration = Task.CompletedTask;
    private Task? _loop;
    private volatile SourceCaptureQueue? _queue;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The active delivery operation owns this temporary reference and disposes it in LoopAsync finally after removing it under the gate.")]
    private CancellationTokenSource? _sending;
    private volatile bool _disposed;
    private long _revision;
    private SourceCaptureState? _failure;

    public SourceCaptureHost(AppSettings settings, HttpClient http, GameSession session, string directory, Action persistSettings)
    {
        _settings = settings; _session = session; _directory = directory; _persistSettings = persistSettings;
        _client = new(http);
        _session.SourceCaptureRecorded += OnFrame;
    }

    public SourceCaptureState State => _failure ?? _queue?.State ?? new("disabled", 0, 0, 0);
    public bool Capturing => !_disposed && _failure is null && _queue?.Capturing == true;
    public event Action<SourceCaptureState>? StateChanged;

    /// <summary>Called only by live hosts; constructing services during a render creates no spool or network work.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null) return;
            _loop = Task.Run(LoopAsync);
        }
    }

    /// <summary>Capture settings at the user action, stop new recording immediately, then serialize durable changes.</summary>
    public Task ApplySettingsAsync(bool explicitEnable = false)
    {
        if (_disposed) return Task.CompletedTask;
        _session.SetSourceCaptureEnabled(false);
        var iid = GameStatsClient.InstallId(_settings);
        var endpoint = _settings.SourceCaptureNoticeVersion >= SourceCaptureClient.NoticeVersion ? PoolClient.Endpoint(_settings) : null;
        var sharing = !_settings.GameStatsOff;
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _sending?.Cancel();
            var revision = ++_revision;
            var before = _configuration;
            _configuration = Task.Run(async () =>
            {
                try { await before.ConfigureAwait(false); } catch (Exception) { /* Each operation publishes its own safe failure. */ }
                try
                {
                    var queue = _queue ??= await SourceCaptureQueue.OpenAsync(_directory).ConfigureAwait(false);
                    // Withdrawal must survive even when settings storage is broken. Enabling needs a durable identity first.
                    if (!sharing) await queue.ConfigureAsync(iid, endpoint, sharing: false).ConfigureAwait(false);
                    _persistSettings();
                    if (sharing) await queue.ConfigureAsync(iid, endpoint, sharing: true, explicitEnable).ConfigureAwait(false);
                    _failure = null;
                    if (!_disposed && revision == Interlocked.Read(ref _revision)) _session.SetSourceCaptureEnabled(queue.Capturing);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    _failure = new("unavailable", 0, _queue?.State.PendingCaptures ?? 0, _queue?.State.PendingDeletions ?? 0);
                    if (_queue is { } queue)
                    {
                        try { await queue.ConfigureAsync(iid, null, sharing).ConfigureAwait(false); }
                        catch (Exception pauseError) when (pauseError is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
                    }
                    Diag.Info("Source capture: unavailable");
                }
                Publish(); Wake();
            });
            return _configuration;
        }
    }

    private void OnFrame(SourceCaptureFrame frame)
    {
        _queue?.TryAppend(frame);
        if (frame.Record.Kind == "end") Wake();
    }

    private void Wake()
    {
        lock (_gate)
            if (!_disposed && _wake.CurrentCount == 0) _wake.Release();
    }

    private void Publish() => StateChanged?.Invoke(State);

    /// <summary>Explicit durability/delivery pass, also used by diagnostics without starting a periodic loop.</summary>
    public async Task SendPendingAsync(CancellationToken cancellationToken = default)
    {
        Task configuration;
        lock (_gate) { if (_disposed) return; configuration = _configuration; }
        await configuration.ConfigureAwait(false);
        if (_queue is not { } queue) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        await queue.FlushAsync().ConfigureAwait(false);
        await queue.SendPendingAsync(_client, stop.Token).ConfigureAwait(false);
        Publish();
    }

    private async Task LoopAsync()
    {
        try
        {
            await ApplySettingsAsync().ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                Task configuration;
                CancellationTokenSource sending;
                lock (_gate)
                {
                    configuration = _configuration;
                    sending = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    _sending = sending;
                }
                try
                {
                    await configuration.ConfigureAwait(false);
                    if (_queue is { } queue) await queue.SendPendingAsync(_client, sending.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
                { Diag.Info("Source capture: delivery postponed"); }
                finally
                {
                    lock (_gate) { if (_sending == sending) _sending = null; }
                    sending.Dispose();
                }
                Publish();
                await _wake.WaitAsync(TimeSpan.FromSeconds(30), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop; Task configuration;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            loop = _loop; configuration = _configuration;
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        _session.StopSourceCapture();
        _session.SetSourceCaptureEnabled(false);
        _session.SourceCaptureRecorded -= OnFrame;
        await configuration.ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);
        if (_queue is { } queue) { await queue.FlushAsync().ConfigureAwait(false); await queue.DisposeAsync().ConfigureAwait(false); }
        _wake.Dispose(); _stop.Dispose();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "The WPF/Avalonia shutdown hooks are synchronous. All source I/O runs off-context, the sender is cancelled first, and source is flushed before the process exits.")]
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
