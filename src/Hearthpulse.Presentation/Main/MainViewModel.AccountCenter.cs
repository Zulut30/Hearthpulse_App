#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
#else
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Main.Match;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Diagnostics;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The account center (Settings → Account, TASK-2026-11-05-account-center): the connection state with "Check now", the
/// games sent to the profile, the replays on the site, the subscription with today's limit and the crash reports; and
/// the main window's indicator built from the same state (<see cref="AccountConnection"/>). Everything comes from the
/// account service, the uploader and the site journal (<see cref="AccountActivity"/>); nothing here calls the site
/// except "Check now".
/// </summary>
public sealed partial class MainViewModel
{
    private RelayCommand? _checkAccountNow;
    private RelayCommand? _openAccountReplays;
    private RelayCommand? _confirmUploadOff;
    private RelayCommand? _keepUploadOn;
    private bool _accountChecking;
    private int _uploadOffPending;
    private int _siteReplays;
    private DateTime? _siteReplayLast;
    private int _crashWaiting;

    /// <summary>The clock of the center's texts; tests set it.</summary>
    internal Func<DateTime> AccountClock { get; set; } = () => DateTime.UtcNow;

    /// <summary>The one connection state of the center and the indicator.</summary>
    public AccountConnection AccountConnection => _account is null
        ? new AccountConnection(AccountLink.SignedOut, null)
        : AccountConnection.Of(_account, AccountClock());

    /// <summary>"Подключено · проверено в 14:05", "Нет связи с сайтом — повтор в 14:20", "Вход истёк — войдите снова".</summary>
    public string AccountLinkText => AccountConnection.Text(AccountClock());

    /// <summary>The indicator's word: "Подключено", "Нет связи", "Вход истёк".</summary>
    public string AccountLinkShort => AccountConnection.Short;

    /// <summary>The indicator's tip and accessible name: the state line and what a click does.</summary>
    public string AccountLinkTip => Loc.F("Account.Link.Tip", AccountLinkText);

    /// <summary>The indicator shows only with an account (or an ended sign-in); signed out it stays hidden.</summary>
    public bool AccountLinkVisible => AccountConnection.Link != AccountLink.SignedOut;

    public bool AccountLinkOk => AccountConnection.Link == AccountLink.Connected;

    public bool AccountLinkWarn => AccountConnection.Link == AccountLink.Offline;

    public bool AccountLinkBad => AccountConnection.Link == AccountLink.SessionEnded;

    /// <summary>"Check now" is running.</summary>
    public bool AccountChecking
    {
        get => _accountChecking;
        private set => Set(ref _accountChecking, value);
    }

    /// <summary>Re-reads <c>/me</c> (profile and subscription) and the daily limits, then the center's counts.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD101:Avoid unsupported async delegates", Justification = "RelayCommand takes an Action in both UIs. Kept on purpose (Q7, 2026-11-02): the account service reports failures as results, and anything else that escapes still reaches the dispatcher's crash window instead of only the log.")]
    public RelayCommand CheckAccountNow => _checkAccountNow ??= new RelayCommand(async () =>
    {
        if (_account is null || AccountChecking)
        {
            return;
        }

        AccountChecking = true;
        try
        {
            var error = await _account.CheckNowAsync();
            var message = error?.Message ?? string.Empty;
            ShowAccountMessage(() => message);
            await RefreshAccountCountsAsync();
        }
        finally
        {
            AccountChecking = false;
            RaiseAccountCenter();
        }
    });

    // ---- games in the profile ------------------------------------------------------------------

    /// <summary>"Отправлено 12 игр · последняя — 14:02", or that none went yet.</summary>
    public string AccountGamesSent => _account?.Activity is not { } a
        ? string.Empty
        : a.GamesSent > 0 && a.LastGameSentAt is { } last
            ? Loc.P("Account.Center.GamesSent", a.GamesSent, AccountConnection.Clock(last, AccountClock()))
            : Loc.T("Account.Center.GamesNone");

    /// <summary>"Ждут отправки: 3"; empty when nothing waits or sending is off.</summary>
    public string AccountGamesWaiting => UploadGames && _uploader?.PendingCount is > 0 and var n
        ? Loc.F("Account.Center.Waiting", n)
        : string.Empty;

    /// <summary>Why sending waits and when it tries again; empty while it is not paused.</summary>
    public string AccountGamesPause
    {
        get
        {
            if (!UploadGames || _account?.Activity[SiteService.Games] is not { Failing: true, LastError: var code } games)
            {
                return string.Empty;
            }

            var reason = Loc.T(ReasonKey(code));
            return games.NextTry is { } next && next > AccountClock()
                ? Loc.F("Account.Center.GamesPaused", reason, AccountConnection.Clock(next, AccountClock()))
                : Loc.F("Account.Center.GamesPausedNow", reason);
        }
    }

    /// <summary>"Отклонено сайтом: 2 · сохранено без статистики: 1" (all time); empty when none.</summary>
    public string AccountGamesProblems => _account?.Activity is { } a && (a.GamesRefused > 0 || a.GamesWithoutStatistics > 0)
        ? Loc.F("Account.Center.GamesProblems", a.GamesRefused, a.GamesWithoutStatistics)
        : string.Empty;

    /// <summary>The question shown before turning sending off while games still wait; empty when none is asked.</summary>
    public string UploadOffConfirmText => _uploadOffPending > 0 ? Loc.P("Account.Center.ConfirmOff", _uploadOffPending) : string.Empty;

    public bool UploadOffConfirming => _uploadOffPending > 0;

    /// <summary>"Turn off" in the question: sending stops and what waits is dropped.</summary>
    public RelayCommand ConfirmUploadOff => _confirmUploadOff ??= new RelayCommand(() =>
    {
        SetUploadOffPending(0);
        ApplyUploadGames(false);
    });

    /// <summary>"Keep it on": nothing changes.</summary>
    public RelayCommand KeepUploadOn => _keepUploadOn ??= new RelayCommand(() =>
    {
        SetUploadOffPending(0);
        Raise(nameof(UploadGames));
    });

    private void SetUploadOffPending(int pending)
    {
        _uploadOffPending = pending;
        Raise(nameof(UploadOffConfirmText));
        Raise(nameof(UploadOffConfirming));
    }

    // Codes of the journal in words of the pause line. No token to send with is "not signed in", unless the account
    // itself cannot reach the site (a refresh that failed offline leaves the uploader without a token too).
    private string ReasonKey(string? code) => code switch
    {
        "RATE_LIMITED" => "Account.Center.Reason.RateLimited",
        "SERVER_ERROR" or "TEMPORARILY_UNAVAILABLE" or "BAD_RESPONSE" => "Account.Center.Reason.Server",
        "NO_TOKEN" => AccountConnection.Link == AccountLink.Offline ? "Account.Center.Reason.Offline" : "Account.Center.Reason.SignedOut",
        _ when code?.StartsWith("HTTP_5", StringComparison.Ordinal) == true => "Account.Center.Reason.Server",
        _ when AccountConnection.IsUnreachable(code) => "Account.Center.Reason.Offline",
        _ => "Account.Center.Reason.Other",
    };

    // ---- replays on the site -------------------------------------------------------------------

    /// <summary>"На сайте 3 реплея, последний — 3 нояб.", or how to upload the first one.</summary>
    public string AccountReplaysText => _siteReplays > 0 && _siteReplayLast is { } last
        ? Loc.P("Account.Center.ReplaysCount", _siteReplays, LocalDay(last))
        : Loc.T("Account.Center.ReplaysNone");

    /// <summary>The site answered the last replay call with "no such route" (its replays are not out yet).</summary>
    public bool AccountReplaysUnavailable => NotOnSite(SiteService.Replays);

    /// <summary>The account page's replays section in the browser.</summary>
    public RelayCommand OpenAccountReplays => _openAccountReplays ??= new RelayCommand(() =>
    {
        if (_account?.OpenReplays() == false)
        {
            ShowAccountMessage(() => Loc.T("Main.Account.BrowserFailed"));
        }
    });

    /// <summary>A library card's "Open on the site": its link in the browser, https on the site's own host only.</summary>
    internal void OpenLibraryCardOnSite(LibraryCard card)
    {
        if (_account?.OpenSiteLink(card.SiteUrl) != true)
        {
            Library.Failed(Loc.T("Account.Library.OpenFailed"));
        }
    }

    // ---- subscription and limits -----------------------------------------------------------------

    /// <summary>
    /// Today's use of the community stats: "Сегодня использовано 2 из 3 …, сбросится в 03:00", the subscription that lifts
    /// it, or that there are no limits; empty before the site said anything about limits.
    /// </summary>
    public string AccountLimitsText
    {
        get
        {
            if (_account?.Activity.Usage is not { } usage)
            {
                return string.Empty;
            }

            if (!usage.Enabled)
            {
                return Loc.T("Account.Center.LimitsOff");
            }

            if (_account.Entitlements.Has(usage.Section))
            {
                return Loc.T("Account.Center.LimitsUnlimited");
            }

            var today = AccountClock().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var used = usage.Day == today ? usage.Used : 0;
            return Loc.F("Account.Center.LimitsUsed", used, usage.Limit, UsageGate.ResetText(usage.ResetsAt));
        }
    }

    /// <summary>"Подписка проверена в 14:05": the site's own check time, else when /me last answered.</summary>
    public string AccountSubscriptionChecked => _account is { SignedIn: true } account
        && (account.Entitlements.CheckedAt ?? account.Activity[SiteService.Account].LastSuccess) is { } at
            ? Loc.F("Account.Center.SubscriptionChecked", AccountConnection.Clock(at, AccountClock()))
            : string.Empty;

    public bool AccountLimitsUnavailable => NotOnSite(SiteService.Limits);

    // ---- crash reports ----------------------------------------------------------------------------

    /// <summary>Whether reports go by themselves, and that they go to hearthpulse.net.</summary>
    public string AccountCrashText => Loc.T(Settings.SendCrashReports ? "Account.Center.CrashOn" : "Account.Center.CrashOff");

    /// <summary>"Ждут отправки: 2" (in the data folder's crash-reports); empty when none.</summary>
    public string AccountCrashWaiting => _crashWaiting > 0 ? Loc.F("Account.Center.Waiting", _crashWaiting) : string.Empty;

    public bool AccountCrashUnavailable => NotOnSite(SiteService.CrashReports);

    // ---- plumbing ---------------------------------------------------------------------------------

    private bool NotOnSite(SiteService service) =>
        _account?.Activity[service] is { Failing: true, LastError: "NOT_AVAILABLE" };

    private static string LocalDay(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()
            .ToString("d MMM", Loc.IsRussian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture);

    /// <summary>Connects the center to the site journal; called by <see cref="AttachAccount"/>.</summary>
    private void AttachAccountCenter(AccountService account)
    {
        account.Activity.Changed += () => DispatchToUi(() =>
        {
            RaiseAccountCenter();
            RefreshAccountCountsAsync().Forget("Account center counts");
        });
        RefreshAccountCountsAsync().Forget("Account center counts");
    }

    /// <summary>Reads the uploaded replays and the waiting crash reports off the UI thread, then shows them.</summary>
    internal async Task RefreshAccountCountsAsync()
    {
        var uploads = LibraryUploads;
        var crashFolder = System.IO.Path.Combine(DataFolder, CrashReporter.FolderName);
        var (count, last, crashes) = await Task.Run(() =>
        {
            var all = uploads.All();
            return (all.Count, all.Count == 0 ? (DateTime?)null : all.Values.Max(u => u.UploadedAt), CrashReporter.PendingIn(crashFolder));
        }).ConfigureAwait(true);
        if (_accountRenderReplays is { } sample)
        {
            // --render-ui's sample account (MainViewModel.AccountRender.cs): its replays, not the data copy's.
            (count, last) = (sample.Count, sample.Last);
        }

        (_siteReplays, _siteReplayLast, _crashWaiting) = (count, last, crashes);
        Raise(nameof(AccountReplaysText));
        Raise(nameof(AccountCrashWaiting));
    }

    private void RaiseAccountCenter()
    {
        Raise(nameof(AccountConnection));
        Raise(nameof(AccountLinkText));
        Raise(nameof(AccountLinkShort));
        Raise(nameof(AccountLinkTip));
        Raise(nameof(AccountLinkVisible));
        Raise(nameof(AccountLinkOk));
        Raise(nameof(AccountLinkWarn));
        Raise(nameof(AccountLinkBad));
        Raise(nameof(AccountGamesSent));
        Raise(nameof(AccountGamesWaiting));
        Raise(nameof(AccountGamesPause));
        Raise(nameof(AccountGamesProblems));
        Raise(nameof(AccountReplaysText));
        Raise(nameof(AccountReplaysUnavailable));
        Raise(nameof(AccountLimitsText));
        Raise(nameof(AccountSubscriptionChecked));
        Raise(nameof(AccountLimitsUnavailable));
        Raise(nameof(AccountCrashText));
        Raise(nameof(AccountCrashWaiting));
        Raise(nameof(AccountCrashUnavailable));
    }
}
