namespace Hearthpulse.Core.Updates;

/// <summary>
/// The host's running update loop. "Update now" and the exit stop it and wait for it before <see cref="UpdateService.ApplyStaged"/>:
/// a download still in progress would otherwise put a newer file in place and delete the staged one the installer is
/// about to run.
/// </summary>
public sealed class UpdateLoop : IDisposable
{
    /// <summary>How long a host waits for the loop to end before installing anyway (a check or download in progress
    /// ends on cancellation within moments; a stuck one must not hold the window).</summary>
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(3);

    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _gate = new();
    private Task? _task;
    private bool _stopped;

    /// <summary>Runs <paramref name="loop"/> on the pool with the token <see cref="Stop"/> cancels; once only, never after a stop.</summary>
    public void Start(Func<CancellationToken, Task> loop)
    {
        lock (_gate)
        {
            if (_task is not null || _stopped)
            {
                return;
            }

            var token = _stop.Token;
            _task = Task.Run(() => loop(token), token);
        }
    }

    /// <summary>The loop is running (started and not stopped).</summary>
    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _task is not null && !_stopped;
            }
        }
    }

    /// <summary>Cancels the loop and waits up to <paramref name="wait"/> for it to end. True when it has ended (or never
    /// started); false when it is still running after the wait.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD002:Avoid problematic synchronous waits", Justification = "Called from the UI thread right before the installer starts or the process exits, where nothing can be awaited. The loop's awaits use ConfigureAwait(false) and its callbacks only post to the dispatcher, so the wait cannot deadlock, and it is bounded.")]
    public bool Stop(TimeSpan wait)
    {
        Task? task;
        bool cancel;
        lock (_gate)
        {
            cancel = !_stopped;
            _stopped = true;
            task = _task;
        }

        if (cancel)
        {
            // Outside the lock: cancellation runs the loop's continuations inline.
            _stop.Cancel();
        }

        if (task is null)
        {
            return true;
        }

        try
        {
            return task.Wait(wait);
        }
        catch (AggregateException)
        {
            // Ended by throwing (cancelled or failed): it has ended either way, and the loop logged a failure itself.
            return true;
        }
    }

    public void Dispose()
    {
        Stop(TimeSpan.Zero);
        _stop.Dispose();
    }
}
