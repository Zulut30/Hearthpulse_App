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
    private bool _running;
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
            if (!_running)
            {
                _running = true;
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
            bool switchedOn;
            DiscordActivity? wanted;
            Task wake;
            lock (_gate)
            {
                switchedOn = _enabled;
                enabled = _enabled && Configured;
                wanted = _wanted;
                if (_wake.Task.IsCompleted)
                {
                    _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                wake = _wake.Task;
            }

            try
            {
                if (await RoundAsync(enabled, switchedOn, wanted, wake, token).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Whatever broke (a pipe that refused us, a frame we did not expect), the loop lives on: it starts over
                // with a fresh connection after a pause, and the player sees "waiting" at worst.
                Diag.Error("Discord: the status update failed; trying again", ex);
                await _ipc.CloseAsync().ConfigureAwait(false);
                _anySent = false;
                SetState(DiscordState.Waiting);
                await WaitAsync(RetryInterval, wake, token).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One round of the pump; true when nothing is left to do and the pump has stopped.</summary>
    private async Task<bool> RoundAsync(bool enabled, bool switchedOn, DiscordActivity? wanted, Task wake, CancellationToken token)
    {
        if (!enabled)
        {
            // Off (or no app id): the status goes away at once, the connection with it.
            await ClearAsync(token).ConfigureAwait(false);
            SetState(switchedOn ? DiscordState.NotConfigured : DiscordState.Off);
            return Settled(enabled: false, wanted);
        }

        if (!_ipc.Connected && !await _ipc.ConnectAsync(_applicationId, token).ConfigureAwait(false))
        {
            SetState(DiscordState.Waiting);
            _anySent = false;
            await WaitAsync(RetryInterval, wake, token).ConfigureAwait(false);
            return false;
        }

        SetState(DiscordState.Connected);
        if (_anySent && wanted == _sent)
        {
            return Settled(enabled: true, wanted);
        }

        var due = _lastSend + MinInterval - _time.GetUtcNow();
        if (due > TimeSpan.Zero)
        {
            await WaitAsync(due, wake, token).ConfigureAwait(false);
            return false;
        }

        try
        {
            await _ipc.SetActivityAsync(wanted, _processId, token).ConfigureAwait(false);
            (_sent, _anySent, _lastSend) = (wanted, true, _time.GetUtcNow());
        }
        catch (IOException ex)
        {
            // Discord went away: the next round connects again at once (and waits only if it is not there).
            Diag.Info("Discord: the status was not sent (" + ex.Message + "); connecting again");
            _anySent = false;
            SetState(DiscordState.Waiting);
        }

        return false;
    }

    /// <summary>
    /// Nothing new was asked for since this round read the wanted state: the pump stops, under the same lock
    /// <see cref="Show"/> takes, so a later call starts a new one instead of waking a pump that is gone.
    /// </summary>
    private bool Settled(bool enabled, DiscordActivity? wanted)
    {
        lock (_gate)
        {
            if ((_enabled && Configured) != enabled || _wanted != wanted || _wake.Task.IsCompleted)
            {
                return false;
            }

            _running = false;
            return true;
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
