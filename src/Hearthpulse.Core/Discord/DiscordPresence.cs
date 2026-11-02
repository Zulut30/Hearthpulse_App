namespace Hearthpulse.Core.Discord;

/// <summary>The presence's link to the Discord app, for the setting's status line.</summary>
public enum DiscordState
{
    /// <summary>The switch is off.</summary>
    Off,

    /// <summary>No app id is registered in this build (<see cref="DiscordPresence.ApplicationId"/>).</summary>
    NotConfigured,

    /// <summary>On, but the Discord app is not running (or did not answer); tried again every half minute.</summary>
    Waiting,

    Connected,
}

/// <summary>
/// The player's Discord status (<c>docs/discord.md</c>): the host hands it the activity it wants on every snapshot
/// (<see cref="Show"/>); it connects to the local Discord app, sends only changes and at most once every
/// <see cref="MinInterval"/> (Discord takes five a 20 s), keeps the latest wanted one, and clears the status when switched
/// off. All work runs off the caller's thread; nothing is shown to the player when Discord is not there.
/// </summary>
public sealed class DiscordPresence : IAsyncDisposable
{
    /// <summary>
    /// The Discord application's id ("Hearthpulse" in the Discord Developer Portal). Empty until the owner registers the
    /// app: the presence then stays <see cref="DiscordState.NotConfigured"/> and never connects.
    /// </summary>
    public const string ApplicationId = "";

    /// <summary>The app's picture in the registered app's Rich Presence art assets ("logo"); empty sends no picture.</summary>
    public const string LogoKey = "";

    /// <summary>The least time between two sends.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long it waits before looking for the Discord app again.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    private readonly DiscordIpc _ipc;
    private readonly string _applicationId;
    private readonly TimeProvider _time;
    private readonly int _processId = Environment.ProcessId;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private bool _enabled;
    private DiscordActivity? _wanted;
    private DiscordActivity? _sent;
    private bool _anySent;
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    private Task _pump = Task.CompletedTask;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DiscordState _state = DiscordState.Off;

    /// <param name="applicationId">The app id (tests pass one; the app uses <see cref="ApplicationId"/>).</param>
    public DiscordPresence(DiscordIpc? ipc = null, string applicationId = ApplicationId, TimeProvider? time = null)
    {
        _ipc = ipc ?? new DiscordIpc();
        _applicationId = applicationId;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised (on a pool thread) when <see cref="State"/> changes.</summary>
    public event Action<DiscordState>? StateChanged;

    /// <summary>Whether this build can show a status at all (an app id is registered).</summary>
    public bool Configured => _applicationId.Length > 0;

    public DiscordState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>The status wanted now: <paramref name="enabled"/> is the setting; a null activity clears the status.</summary>
    public void Show(bool enabled, DiscordActivity? activity)
    {
        lock (_gate)
        {
            if (enabled == _enabled && activity == _wanted)
            {
                return;
            }

            _enabled = enabled;
            _wanted = activity;
            if (_pump.IsCompleted)
            {
                _pump = Task.Run(PumpAsync);
            }
            else
            {
                _wake.TrySetResult();
            }
        }
    }

    /// <summary>Raised when the pump starts waiting on the clock (tests move their clock only then).</summary>
    internal event Action? WaitingForClock;

    /// <summary>The latest work (tests wait for it).</summary>
    internal Task Pending
    {
        get
        {
            lock (_gate)
            {
                return _pump;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }

        await _ipc.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task PumpAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            bool enabled;
            DiscordActivity? wanted;
            Task wake;
            lock (_gate)
            {
                enabled = _enabled && Configured;
                wanted = _wanted;
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                wake = _wake.Task;
            }

            if (!enabled)
            {
                // Off (or no app id): the status goes away at once, the connection with it.
                await ClearAsync(token).ConfigureAwait(false);
                SetState(_enabled ? DiscordState.NotConfigured : DiscordState.Off);
                if (Settled(enabled: false, wanted))
                {
                    return;
                }

                continue;
            }

            if (!_ipc.Connected && !await _ipc.ConnectAsync(_applicationId, token).ConfigureAwait(false))
            {
                SetState(DiscordState.Waiting);
                _anySent = false;
                await WaitAsync(RetryInterval, wake, token).ConfigureAwait(false);
                continue;
            }

            SetState(DiscordState.Connected);
            if (_anySent && wanted == _sent)
            {
                if (Settled(enabled: true, wanted))
                {
                    return;
                }

                continue;
            }

            var due = _lastSend + MinInterval - _time.GetUtcNow();
            if (due > TimeSpan.Zero)
            {
                await WaitAsync(due, wake, token).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _ipc.SetActivityAsync(wanted, _processId, token).ConfigureAwait(false);
                (_sent, _anySent, _lastSend) = (wanted, true, _time.GetUtcNow());
            }
            catch (IOException ex)
            {
                Diag.Info("Discord: the status was not sent (" + ex.Message + "); waiting for the app");
                _anySent = false;
                SetState(DiscordState.Waiting);
            }
        }
    }

    /// <summary>Nothing new was asked for since this round read the wanted state: the pump may end.</summary>
    private bool Settled(bool enabled, DiscordActivity? wanted)
    {
        lock (_gate)
        {
            return (_enabled && Configured) == enabled && _wanted == wanted && !_wake.Task.IsCompleted;
        }
    }

    private async Task ClearAsync(CancellationToken token)
    {
        if (!_ipc.Connected)
        {
            return;
        }

        try
        {
            await _ipc.SetActivityAsync(null, _processId, token).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Gone already: nothing to clear.
        }

        await _ipc.CloseAsync().ConfigureAwait(false);
        (_sent, _anySent) = (null, false);
    }

    /// <summary>Waits <paramref name="delay"/> on the provider's clock, or less when <paramref name="wake"/> completes.</summary>
    private async Task WaitAsync(TimeSpan delay, Task wake, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var timer = Task.Delay(delay, _time, linked.Token);
        WaitingForClock?.Invoke();
        await Task.WhenAny(timer, wake).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private void SetState(DiscordState state)
    {
        lock (_gate)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
        }

        StateChanged?.Invoke(state);
    }
}
