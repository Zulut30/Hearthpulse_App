using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Updates;

/// <summary>What the update loop reports. Every callback runs on a pool thread.</summary>
/// <param name="Available">This copy only tells; once per version, again for a newer one.</param>
/// <param name="Staged">Downloaded and verified; once per version, again for a newer one.</param>
/// <param name="Failed">Check/download/signature failed, or the version is blocked after 2 failed installs.</param>
/// <param name="Checked">Every finished check, before the three above.</param>
public sealed record UpdateCallbacks(
    Action<UpdateInfo> Available,
    Action<UpdateInfo> Staged,
    Action<Func<string>> Failed,
    Action<UpdateCheck> Checked);

/// <summary>The "Check now" button's way into the loop.</summary>
public sealed class UpdateTrigger
{
    /// <summary>The shortest time between two checks the player asks for.</summary>
    internal static readonly TimeSpan MinimumGap = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private readonly Func<DateTimeOffset> _now;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _busy;
    private DateTimeOffset? _lastRequest;
    private DateTimeOffset? _blockedUntil;
    private Refusal _refusal;

    public UpdateTrigger()
        : this(() => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>A trigger on a given clock (tests).</summary>
    internal UpdateTrigger(Func<DateTimeOffset> now)
    {
        _now = now;
        RefusalMessage = Describe;
    }

    private enum Refusal
    {
        None,
        Busy,
        TooSoon,
        Limited,
    }

    /// <summary>Why the last <see cref="Request"/> was refused, built on read (follows the language).</summary>
    public Func<string> RefusalMessage { get; }

    /// <summary>The end of GitHub's limit the last check reported; null when there is none.</summary>
    internal DateTimeOffset? BlockedUntil
    {
        get
        {
            lock (_gate)
            {
                return _blockedUntil;
            }
        }
    }

    /// <summary>
    /// Asks for a check now. False (nothing sent) while a check runs, within 60 s of the last request, or before
    /// <see cref="UpdateCheck.RetryAfter"/>; the reason is in <see cref="RefusalMessage"/>.
    /// </summary>
    public bool Request()
    {
        lock (_gate)
        {
            var now = _now();
            if (_busy)
            {
                _refusal = Refusal.Busy;
                return false;
            }

            if (_blockedUntil is { } until && until > now)
            {
                _refusal = Refusal.Limited;
                return false;
            }

            if (_lastRequest is { } last && now - last < MinimumGap)
            {
                _refusal = Refusal.TooSoon;
                return false;
            }

            _refusal = Refusal.None;
            _lastRequest = now;
            _busy = true;
            _wake.TrySetResult();
            return true;
        }
    }

    /// <summary>Waits for <paramref name="wait"/> (infinite allowed) or a <see cref="Request"/>; true when a request woke it.</summary>
    internal async Task<bool> WaitAsync(TimeSpan wait, CancellationToken cancellationToken = default)
    {
        Task wake;
        lock (_gate)
        {
            wake = _wake.Task;
        }

        if (!wake.IsCompleted && wait != TimeSpan.Zero)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(wait, cancel.Token);
            await Task.WhenAny(wake, delay).ConfigureAwait(false);
            await cancel.CancelAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (!_wake.Task.IsCompleted)
            {
                return false;
            }

            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    /// <summary>The loop starts a check (automatic or requested): <see cref="Request"/> is refused until it ends.</summary>
    internal void BeginCheck()
    {
        lock (_gate)
        {
            _busy = true;
        }
    }

    /// <summary>The check ended; <paramref name="retryAfter"/> is GitHub's limit it reported (null clears it).</summary>
    internal void EndCheck(DateTimeOffset? retryAfter)
    {
        lock (_gate)
        {
            _busy = false;
            _blockedUntil = retryAfter;
        }
    }

    private string Describe()
    {
        Refusal refusal;
        DateTimeOffset? until;
        lock (_gate)
        {
            refusal = _refusal;
            until = _blockedUntil;
        }

        return refusal switch
        {
            Refusal.Busy => Loc.T("Core.Update.InProgress"),
            Refusal.TooSoon => Loc.T("Core.Update.TooSoon"),
            Refusal.Limited when until is { } at => UpdateService.TryLaterText(at),
            _ => string.Empty,
        };
    }
}

public static partial class UpdateService
{
    /// <summary>How often the automatic loop asks GitHub.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    /// <summary>
    /// The update loop both apps run in the background: 20 seconds after it starts, then every six hours, asks GitHub
    /// for a newer release. One this copy must not install itself goes to <paramref name="available"/>, one downloaded
    /// and verified to <paramref name="staged"/>, anything else (an up-to-date answer included) to <paramref name="failed"/>
    /// as the text to show. Kept for hosts not yet on
    /// <see cref="WatchAsync(HttpClient, bool, bool, UpdateCallbacks, UpdateTrigger)"/>; the loop no longer ends after
    /// the first release it finds.
    /// </summary>
    public static Task WatchAsync(HttpClient http, bool notifyOnly, Action<UpdateInfo> available, Action<UpdateInfo> staged, Action<Func<string>> failed) =>
        WatchAsync(http, notifyOnly, automatic: true, LegacyCallbacks(available, staged, failed), new UpdateTrigger());

    /// <summary>
    /// The update loop. With <paramref name="automatic"/> it checks 20 seconds after it starts, then every six hours
    /// (never before GitHub's limit ends); without, only on <see cref="UpdateTrigger.Request"/> (AutoUpdate off). A
    /// request runs one full round: check, then for a new version the download and <see cref="UpdateCallbacks.Staged"/>.
    /// The loop never ends: a newer release replaces a staged one (its file is deleted only once the new one is in
    /// place), and a version already reported is not reported again.
    /// </summary>
    /// <param name="automatic">false runs a check only on <see cref="UpdateTrigger.Request"/> (AutoUpdate off).</param>
    /// <param name="cancellationToken">Stops the loop: a wait, check or download in progress ends, and no callback follows.</param>
    public static Task WatchAsync(HttpClient http, bool notifyOnly, bool automatic, UpdateCallbacks callbacks, UpdateTrigger trigger,
        CancellationToken cancellationToken = default) =>
        WatchAsync(http, notifyOnly, automatic, callbacks, trigger, new WatchOptions(
            TimeSpan.FromSeconds(20), CheckInterval, DetectTarget(), AppContext.BaseDirectory, File.Exists, UpdateContext.Default()), cancellationToken);

    /// <summary>
    /// The old overload's test entry: first wait, target and folder passed in. It trusts no key, so the delta and
    /// download tests do not depend on the key this build carries (the signature has its own tests).
    /// </summary>
    internal static Task WatchAsync(HttpClient http, bool notifyOnly, Action<UpdateInfo> available, Action<UpdateInfo> staged, Action<Func<string>> failed,
        TimeSpan firstWait, UpdateTarget target, string appDirectory, Func<string, bool> exists) =>
        WatchAsync(http, notifyOnly, automatic: true, LegacyCallbacks(available, staged, failed), new UpdateTrigger(),
            new WatchOptions(firstWait, CheckInterval, target, appDirectory, exists, UpdateContext.Default() with { Keys = [] }));

    /// <summary>The loop with its waits, copy and state passed in (tests run it with intervals in milliseconds and stop it).</summary>
    internal static async Task WatchAsync(HttpClient http, bool notifyOnly, bool automatic, UpdateCallbacks callbacks, UpdateTrigger trigger, WatchOptions options,
        CancellationToken cancellationToken = default)
    {
        var round = new WatchRound(http, notifyOnly, callbacks, options);
        var wait = automatic ? options.FirstWait : Timeout.InfiniteTimeSpan;
        var cleaned = false;
        while (true)
        {
            var requested = await trigger.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var now = options.Context.Now();
            if (!requested && trigger.BlockedUntil is { } until && until > now)
            {
                // An automatic round never asks before GitHub's limit ends.
                wait = Capped(until - now);
                continue;
            }

            if (!cleaned && options.Target == UpdateTarget.WindowsInstalled)
            {
                cleaned = true;
                DeltaUpdate.CleanUp(options.AppDirectory, CurrentVersion);
            }

            trigger.BeginCheck();
            DateTimeOffset? retryAfter = null;
            try
            {
                retryAfter = (await round.RunAsync(cancellationToken).ConfigureAwait(false))?.RetryAfter;
            }
            catch (Exception ex)
            {
                // Stopped while checking or downloading: whatever the cancelled step threw is not a failure.
                if (!cancellationToken.IsCancellationRequested)
                {
                    Diag.Error("Update check failed", ex);
                }
            }
            finally
            {
                trigger.EndCheck(retryAfter);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!automatic)
            {
                wait = Timeout.InfiniteTimeSpan;
                continue;
            }

            var untilLimit = retryAfter is { } end ? end - options.Context.Now() : TimeSpan.Zero;
            wait = Capped(untilLimit > options.Interval ? untilLimit : options.Interval);
        }
    }

    /// <summary>The longest wait between automatic checks, whatever a reply said (Task.Delay takes at most ~49 days).</summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);

    private static TimeSpan Capped(TimeSpan wait) => wait > LongestWait ? LongestWait : wait;

    /// <summary>
    /// The old callbacks on the new ones: an up-to-date answer and a failed check went to <paramref name="failed"/>
    /// (the hosts show its text in Settings → About), as before.
    /// </summary>
    private static UpdateCallbacks LegacyCallbacks(Action<UpdateInfo> available, Action<UpdateInfo> staged, Action<Func<string>> failed) =>
        new(available, staged, failed, check =>
        {
            if (check.Outcome == UpdateCheckOutcome.UpToDate)
            {
                var message = check.Message;
                failed(() => message);
            }
        });

    /// <summary>The loop's waits, this copy's target and folder, and the updater's state.</summary>
    internal sealed record WatchOptions(TimeSpan FirstWait, TimeSpan Interval, UpdateTarget Target, string AppDirectory,
        Func<string, bool> Exists, UpdateContext Context);

    /// <summary>One check and what follows it, remembering what was already reported so a version is reported once.</summary>
    private sealed class WatchRound(HttpClient http, bool notifyOnly, UpdateCallbacks callbacks, WatchOptions options)
    {
        private string? _available;
        private string? _staged;

        /// <summary>One round; once <paramref name="cancellationToken"/> is cancelled no callback runs any more.</summary>
        public async Task<UpdateCheck?> RunAsync(CancellationToken cancellationToken)
        {
            // A delta that could not put its old files back left a folder of two versions: the full setup of the
            // newest release repairs it, even when that is the version this copy reports.
            var repair = options.Target == UpdateTarget.WindowsInstalled && DeltaUpdate.IsBroken(options.AppDirectory);
            var check = await CheckForUpdateAsync(http, options.Target, options.Context, cancellationToken, repair).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var info = repair && check.Update is { } broken ? broken with { Delta = null } : check.Update;
            callbacks.Checked(check);
            if (info is null)
            {
                if (check.Outcome == UpdateCheckOutcome.Failed)
                {
                    var message = check.Message;
                    callbacks.Failed(() => message);
                }

                return check;
            }

            if (notifyOnly || !InstallsItself(options.Target, options.AppDirectory, options.Exists))
            {
                if (_available != info.Version)
                {
                    _available = info.Version;
                    callbacks.Available(info);
                }

                return check;
            }

            if (_staged == info.Version)
            {
                return check;
            }

            await StageAsync(info, check.Message, cancellationToken).ConfigureAwait(false);
            return check;
        }

        private async Task StageAsync(UpdateInfo info, string checkMessage, CancellationToken cancellationToken)
        {
            if (InstallState.FailedAttempts(options.Context.StateDirectory, info.Version) >= InstallState.MaxAttempts)
            {
                Diag.Info($"Update {info.Version} failed to install {InstallState.MaxAttempts} times; it is left to a manual install.");
                callbacks.Failed(() => Loc.F("Core.Update.InstallFailedManual", info.Version));
                return;
            }

            if (info.Sha256 is null)
            {
                Diag.Info(checkMessage);
                TryDelete(StagedPath(info));
                callbacks.Failed(() => checkMessage);
                return;
            }

            var verified = await UpdateSignature.VerifyAsync(http, info, options.Context.Keys, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (verified is null)
            {
                callbacks.Failed(() => Loc.F("Core.Update.Unsigned", info.Version));
                return;
            }

            // The delta first (a few MB); when it does not fit this copy or fails, the full setup as before.
            if (verified.Delta is not null && options.Target == UpdateTarget.WindowsInstalled
                && await DeltaUpdate.PrepareAsync(http, verified, options.AppDirectory, CurrentVersion, cancellationToken).ConfigureAwait(false))
            {
                DeleteOldDownloads(StagedPath(verified));
                Stage(verified);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var downloaded = await DownloadAsync(http, verified, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (downloaded is not null)
            {
                Stage(verified);
                return;
            }

            callbacks.Failed(() => Loc.F("Host.Update.DownloadFailed", info.Version));
        }

        private void Stage(UpdateInfo info)
        {
            _staged = info.Version;
            callbacks.Staged(info);
        }
    }
}
