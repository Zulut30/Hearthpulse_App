using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Diagnostics;
using Hearthpulse.Core.Hosting;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;
using Microsoft.Extensions.Time.Testing;

namespace Hearthpulse.Tests;

/// <summary>
/// The account center (TASK-2026-11-05-account-center): the site journal <see cref="AccountActivity"/>, the one connection
/// state (<see cref="AccountConnection"/>), the restore error that is kept, the center's texts and the
/// confirmation before sending is turned off, with a fake hearthpulse.net.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class AccountCenterTests : IDisposable
{
    private const string Base = "https://site.test/api/v1/";
    private readonly string _dir = TestDataFolder.Scratch("account-center");
    private readonly FakeSite _site = new();
    private readonly List<Uri> _opened = [];

    // The retry while offline and the journal's delayed write run on this clock: nothing fires unless a test moves it.
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);

    public AccountCenterTests()
    {
        Directory.CreateDirectory(_dir);
        // On the headless UI thread, started first: the view models post their updates to it, and live views follow the language.
        LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(Loc.English));
    }

    public void Dispose() => TestDataFolder.TryDelete(_dir);

    private string JournalPath => Path.Combine(_dir, "account-activity.json");

    /// <summary>A data folder of the test's own (its uploaded replays and crash reports are counted).</summary>
    private AppData Data()
    {
        var replays = new ReplayStore(Path.Combine(_dir, "replays"));
        return new AppData(_dir, replays, new ReplayLibrary(replays, Path.Combine(_dir, "saved"), Path.Combine(_dir, "imported")),
            new ReplayUploads(Path.Combine(_dir, "uploads.json")));
    }

    private AccountClient Client() => new(new HttpClient(_site), Base);

    private AccountService Service(AccountActivity? activity = null) =>
        new(Client(), new TokenStore(Path.Combine(_dir, "account.dat")), url => { _opened.Add(url); return true; }, activity ?? Journal()) { Time = _time };

    /// <summary>A journal in memory (no path) or in the test's file, on the test's clock.</summary>
    private AccountActivity Journal(string? path = "") => new(path == "" ? JournalPath : path) { Time = _time };

    /// <summary>A sign-in saved by an earlier start, restored now.</summary>
    private async Task<AccountService> RestoredAsync(DateTime? expires = null, AccountActivity? activity = null)
    {
        var store = new TokenStore(Path.Combine(_dir, "account.dat"));
        Assert.True(store.Save("ref-0", new AccountUser("u-42", "Anna", Entitlements.None), expires ?? DateTime.UtcNow.AddDays(20), "https://site.test"));
        var account = Service(activity);
        await account.RestoreAsync();
        return account;
    }

    [Fact]
    public void The_journal_survives_a_restart_and_a_broken_file_starts_over()
    {
        var journal = new AccountActivity(JournalPath);
        var changes = 0;
        journal.Changed += () => changes++;
        journal.Succeeded(SiteService.Account);
        journal.Failed(SiteService.Games, "OFFLINE", new DateTime(2026, 11, 5, 14, 20, 0, DateTimeKind.Utc));
        journal.CountGames(accepted: 3, refused: 1, withoutStatistics: 2);
        journal.SetUsage(new UsageToday("2026-11-05", true, "battlegrounds", 2, 3, DateTime.UnixEpoch));
        Assert.Equal(4, changes);
        journal.Flush();

        var again = new AccountActivity(JournalPath);
        Assert.NotNull(again[SiteService.Account].LastSuccess);
        Assert.True(again[SiteService.Games].Failing);
        Assert.Equal("OFFLINE", again[SiteService.Games].LastError);
        Assert.Equal(new DateTime(2026, 11, 5, 14, 20, 0, DateTimeKind.Utc), again[SiteService.Games].NextTry);
        Assert.Equal((3L, 1L, 2L), (again.GamesSent, again.GamesRefused, again.GamesWithoutStatistics));
        Assert.NotNull(again.LastGameSentAt);
        Assert.Equal(2, again.Usage!.Used);
        Assert.Equal(SiteServiceRecord.Empty, again[SiteService.CrashReports]);

        // A broken file is only lost history: read as empty, never thrown, and written over by the next change.
        File.WriteAllText(JournalPath, "{ not json");
        var broken = new AccountActivity(JournalPath);
        Assert.Equal(0, broken.GamesSent);
        Assert.False(broken[SiteService.Games].Failing);
        broken.CountGames(1, 0, 0);
        broken.Flush();
        Assert.Equal(1, new AccountActivity(JournalPath).GamesSent);

        // A folder in the file's place cannot be written: the journal keeps working in memory.
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(blocked);
        var memory = new AccountActivity(blocked);
        memory.CountGames(2, 0, 0);
        memory.Flush();
        Assert.Equal(2, memory.GamesSent);
    }

    [Fact]
    public async Task Each_connection_state_follows_the_account_and_the_journal()
    {
        var now = DateTime.UtcNow;

        // Never signed in: signed out, nothing to say.
        var none = Service();
        Assert.Equal(AccountLink.SignedOut, AccountConnection.Of(none, now).Link);
        Assert.Equal("Not signed in", AccountConnection.Of(none, now).Text(now));

        // The site answered at start: connected, checked just now.
        var connected = await RestoredAsync();
        var state = AccountConnection.Of(connected, DateTime.UtcNow);
        Assert.Equal(AccountLink.Connected, state.Link);
        Assert.NotNull(state.At);
        Assert.Equal("Connected · checked at " + AccountConnection.Clock(state.At!.Value, DateTime.UtcNow), state.Text(DateTime.UtcNow));
        Assert.Null(connected.LastCheckError);

        // Offline at start: still signed in, the error kept; the uploader's next try names the retry time.
        _site.Offline = true;
        var offline = await RestoredAsync(activity: new AccountActivity(null));
        Assert.True(offline.SignedIn);
        Assert.Equal("OFFLINE", offline.LastCheckError?.Code);
        // The account asks again in a minute: the state line names that try.
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddMinutes(1), offline.NextTry);
        Assert.Equal(new AccountConnection(AccountLink.Offline, offline.NextTry), AccountConnection.Of(offline, DateTime.UtcNow));
        Assert.Equal("Can't reach the site — retrying at " + AccountConnection.Clock(offline.NextTry!.Value, DateTime.UtcNow),
            AccountConnection.Of(offline, DateTime.UtcNow).Text(DateTime.UtcNow));
        // A later try of the uploader does not hide the account's earlier one.
        var retry = DateTime.UtcNow.AddMinutes(15);
        offline.Activity.Failed(SiteService.Games, "OFFLINE", retry);
        state = AccountConnection.Of(offline, DateTime.UtcNow);
        Assert.Equal(new AccountConnection(AccountLink.Offline, offline.NextTry), state);
        // A "not on the site yet" answer is not an outage.
        var notYet = await RestoredAsync(activity: new AccountActivity(null));
        _site.Offline = false;
        notYet.Activity.Succeeded(SiteService.Account);
        notYet.Activity.Failed(SiteService.Replays, "NOT_AVAILABLE");
        Assert.Equal(AccountLink.Connected, AccountConnection.Of(notYet, DateTime.UtcNow).Link);

        // The site took the session back at start: signed out, but the card says the sign-in ended until the player acts.
        _site.RejectRefresh = true;
        var ended = await RestoredAsync(activity: new AccountActivity(null));
        Assert.False(ended.SignedIn);
        Assert.Equal("INVALID_GRANT", ended.LastCheckError?.Code);
        Assert.Equal(AccountLink.SessionEnded, AccountConnection.Of(ended, DateTime.UtcNow).Link);
        Assert.Equal("Sign-in expired — sign in again", AccountConnection.Of(ended, DateTime.UtcNow).Text(DateTime.UtcNow));
        await ended.SignOutAsync();
        Assert.Equal(AccountLink.SignedOut, AccountConnection.Of(ended, DateTime.UtcNow).Link);
        Assert.Null(ended.LastCheckError);

        // The 30 days ran out while the app was offline.
        _site.RejectRefresh = false;
        _site.Offline = true;
        var expired = await RestoredAsync(DateTime.UtcNow.AddMinutes(-1), new AccountActivity(null));
        Assert.True(expired.SignedIn);
        Assert.Equal(AccountLink.SessionEnded, AccountConnection.Of(expired, DateTime.UtcNow).Link);
    }

    [Fact]
    public async Task The_indicator_shows_only_with_an_account_and_takes_the_state_colour()
    {
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), Data());
        vm.AttachAccount(Service(new AccountActivity(null)));
        Assert.False(vm.AccountLinkVisible);
        Assert.Equal((false, false, false), (vm.AccountLinkOk, vm.AccountLinkWarn, vm.AccountLinkBad));

        _site.Offline = true;
        var vm2 = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), Data());
        vm2.AttachAccount(await RestoredAsync(activity: new AccountActivity(null)));
        Assert.True(vm2.AccountLinkVisible);
        Assert.Equal((false, true, false), (vm2.AccountLinkOk, vm2.AccountLinkWarn, vm2.AccountLinkBad));
        Assert.Equal("Offline", vm2.AccountLinkShort);
        Assert.StartsWith("hearthpulse.net: Can't reach the site — retrying at ", vm2.AccountLinkTip, StringComparison.Ordinal);
        Assert.EndsWith(". Click to open your account.", vm2.AccountLinkTip, StringComparison.Ordinal);

        _site.Offline = false;
        var vm3 = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), Data());
        vm3.AttachAccount(await RestoredAsync(activity: new AccountActivity(null)));
        Assert.Equal((true, false, false), (vm3.AccountLinkOk, vm3.AccountLinkWarn, vm3.AccountLinkBad));
        Assert.Equal("Connected", vm3.AccountLinkShort);

        _site.RejectRefresh = true;
        var vm4 = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), Data());
        vm4.AttachAccount(await RestoredAsync(activity: new AccountActivity(null)));
        Assert.True(vm4.AccountLinkVisible);
        Assert.Equal((false, false, true), (vm4.AccountLinkOk, vm4.AccountLinkWarn, vm4.AccountLinkBad));
        Assert.Equal("Sign-in expired", vm4.AccountLinkShort);
    }

    [Fact]
    public async Task The_center_texts_come_from_the_journal()
    {
        var account = await RestoredAsync(activity: new AccountActivity(null));
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), Data());
        var now = DateTime.UtcNow;
        vm.AccountClock = () => now;
        vm.AttachAccount(account);

        Assert.Equal("No games sent yet.", vm.AccountGamesSent);
        Assert.Equal(string.Empty, vm.AccountGamesProblems);
        account.Activity.CountGames(12, 2, 1);
        Assert.Equal("12 games sent · the last at " + AccountConnection.Clock(account.Activity.LastGameSentAt!.Value, now), vm.AccountGamesSent);
        Assert.Equal("Refused by the site: 2 · stored without statistics: 1", vm.AccountGamesProblems);

        // A pause names its reason and the next try.
        Assert.Equal(string.Empty, vm.AccountGamesPause);
        var next = now.AddMinutes(5);
        account.Activity.Failed(SiteService.Games, "SERVER_ERROR", next);
        Assert.Equal($"Sending is paused: the site is answering with errors. Next try at {AccountConnection.Clock(next, now)}.", vm.AccountGamesPause);
        // No token to send with is "not signed in", not an outage (review m1).
        account.Activity.Failed(SiteService.Games, "NO_TOKEN");
        Assert.Equal("Sending is paused: you are not signed in to the site.", vm.AccountGamesPause);
        Assert.Equal(AccountLink.Connected, vm.AccountConnection.Link);

        // Limits: off, used today, and not on the site yet.
        Assert.Equal(string.Empty, vm.AccountLimitsText);
        var today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        account.Activity.SetUsage(new UsageToday(today, false, string.Empty, 0, 0, DateTime.MinValue));
        Assert.Equal("There are no daily limits right now.", vm.AccountLimitsText);
        var resets = now.Date.AddDays(1);
        account.Activity.SetUsage(new UsageToday(today, true, Entitlements.Battlegrounds, 2, 3, resets));
        Assert.Equal($"Today you used 2 of 3 free games with community stats; resets at {UsageGate.ResetText(resets)}.", vm.AccountLimitsText);
        account.Activity.SetUsage(new UsageToday("2000-01-01", true, Entitlements.Battlegrounds, 2, 3, resets));
        Assert.StartsWith("Today you used 0 of 3", vm.AccountLimitsText, StringComparison.Ordinal);
        Assert.False(vm.AccountLimitsUnavailable);
        account.Activity.Failed(SiteService.Limits, "NOT_AVAILABLE");
        Assert.True(vm.AccountLimitsUnavailable);
        Assert.StartsWith("Subscription checked at ", vm.AccountSubscriptionChecked, StringComparison.Ordinal);

        // Replays and crash reports, counted in the data folder.
        await vm.RefreshAccountCountsAsync();
        Assert.StartsWith("You haven't uploaded any replays yet.", vm.AccountReplaysText, StringComparison.Ordinal);
        var uploads = new ReplayUploads(Path.Combine(_dir, "uploads.json"));
        var uploaded = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        uploads.Set("games/a.hpr", new ReplayUploadResult("abcdefghij", "https://site.test/r/abcdefghij", "t1") { UploadedAt = uploaded.AddDays(-1) });
        uploads.Set("games/b.hpr", new ReplayUploadResult("bcdefghijk", "https://site.test/r/bcdefghijk", "t2") { UploadedAt = uploaded });
        Directory.CreateDirectory(Path.Combine(_dir, CrashReporter.FolderName));
        File.WriteAllText(Path.Combine(_dir, CrashReporter.FolderName, "x.json"), "{}");
        await vm.RefreshAccountCountsAsync();
        Assert.Equal("2 replays on the site, the last from " + uploaded.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture), vm.AccountReplaysText);
        Assert.Equal("Waiting to be sent: 1", vm.AccountCrashWaiting);
        Assert.Equal(Loc.T("Account.Center.CrashOff"), vm.AccountCrashText);
        Assert.Contains("hearthpulse.net", vm.AccountCrashText, StringComparison.Ordinal);
        vm.CrashReports = true;
        Assert.Equal(Loc.T("Account.Center.CrashOn"), vm.AccountCrashText);

        // "My replays on the site": the profile's replays section.
        vm.OpenAccountReplays.Execute(null);
        Assert.Equal("https://site.test/profile/#account-replays", _opened[^1].AbsoluteUri);
    }

    [Fact]
    public void Turning_sending_off_while_games_wait_asks_first()
    {
        var history = HistoryStore.InMemory();
        var start = DateTime.UtcNow.AddHours(-1);
        var game = new GameRecord
        {
            Key = $"{start:yyyyMMddHHmmss}|Secret#1234|GT_RANKED", StartedAt = start, EndedAt = start.AddMinutes(8), Mode = "Ranked",
            GameType = "GT_RANKED", Format = "FT_STANDARD", PlayerName = "Secret#1234", OpponentName = "Rival#5678", PlayerClass = "MAGE",
            OpponentClass = "WARRIOR", Result = "Win",
        };
        Assert.True(history.Add(game));
        var settings = new AppSettings();
        var uploader = new ProfileUploader(Client(), (_, _) => Task.FromResult<string?>(null), () => new AccountSession("u-42", 1), () => settings.UploadGamesToProfile,
            history, new ProfileUploadQueue(Path.Combine(_dir, "profile-upload.json")));
        uploader.OnGameFinished(new GameOutcome(game.Mode, game.Result, game.Placement, game.RatingBefore, null, game.HeroCardId) { Key = game.Key });
        Assert.Equal(1, uploader.PendingCount);

        var vm = new MainViewModel(history, settings, Data());
        vm.AttachUploader(uploader);
        Assert.Equal("Waiting to be sent: 1", vm.AccountGamesWaiting);

        // Off asks first; "Keep it on" changes nothing.
        vm.UploadGames = false;
        Assert.True(vm.UploadGames);
        Assert.True(settings.UploadGamesToProfile);
        Assert.True(vm.UploadOffConfirming);
        Assert.Equal("1 game hasn't been sent yet. If you turn sending off, it won't reach your profile.", vm.UploadOffConfirmText);
        vm.KeepUploadOn.Execute(null);
        Assert.False(vm.UploadOffConfirming);
        Assert.True(settings.UploadGamesToProfile);
        Assert.Equal(1, uploader.PendingCount);

        // "Turn off" does it, and the waiting game is dropped as before.
        vm.UploadGames = false;
        vm.ConfirmUploadOff.Execute(null);
        Assert.False(vm.UploadGames);
        Assert.False(settings.UploadGamesToProfile);
        Assert.False(vm.UploadOffConfirming);
        Assert.Equal(0, uploader.PendingCount);
        Assert.Equal(string.Empty, vm.AccountGamesWaiting);

        // With nothing waiting it turns off and on at once.
        vm.UploadGames = true;
        vm.UploadGames = false;
        Assert.False(settings.UploadGamesToProfile);
        Assert.False(vm.UploadOffConfirming);
    }

    [Fact]
    public async Task Limits_replays_and_crash_reports_feed_the_journal_and_a_404_reads_not_available()
    {
        var account = await RestoredAsync(activity: new AccountActivity(null));

        // The limits: read now ("Check now" forces it past the six-hour pause), today's use goes to the journal.
        var gate = new UsageGate(Client(), account, Path.Combine(_dir, "usage.json"));
        await gate.RefreshLimitsAsync(force: true);
        Assert.False(account.Activity[SiteService.Limits].Failing);
        Assert.Equal((true, Entitlements.Battlegrounds, 0, 3), (account.Activity.Usage!.Enabled, account.Activity.Usage.Section, account.Activity.Usage.Used, account.Activity.Usage.Limit));
        _site.LimitsStatus = 404;
        await gate.RefreshLimitsAsync();
        Assert.False(account.Activity[SiteService.Limits].Failing); // within six hours: not asked again
        await gate.RefreshLimitsAsync(force: true);
        Assert.Equal("NOT_AVAILABLE", account.Activity[SiteService.Limits].LastError);

        // "Check now" re-reads /me and asks the host for the limits.
        var asked = 0;
        account.CheckRequested += () => asked++;
        Assert.Null(await account.CheckNowAsync());
        Assert.Equal(1, asked);

        // A replay upload the site has no route for yet.
        var (result, error) = await account.UploadReplayAsync("H4sI");
        Assert.Null(result);
        Assert.Equal("NOT_AVAILABLE", error);
        Assert.Equal("NOT_AVAILABLE", account.Activity[SiteService.Replays].LastError);

        // Crash reports: a send before the journal is attached is recorded when it is.
        var folder = Path.Combine(_dir, CrashReporter.FolderName);
        var reporter = new CrashReporter(new HttpClient(_site), new Uri(Base + "crash-reports"), folder, () => true, "avalonia", "1.0.0");
        reporter.Queue("Test", new InvalidOperationException("boom"));
        Assert.Equal(1, CrashReporter.PendingIn(folder));
        _site.CrashStatus = 404;
        Assert.False(await reporter.SendPendingAsync());
        reporter.Activity = account.Activity;
        Assert.Equal("NOT_AVAILABLE", account.Activity[SiteService.CrashReports].LastError);
        _site.CrashStatus = 201;
        Assert.True(await reporter.SendPendingAsync());
        Assert.False(account.Activity[SiteService.CrashReports].Failing);
        Assert.Equal(0, CrashReporter.PendingIn(folder));
    }

    [Theory]
    [InlineData("https://site.test/r/abcdefghij", true)]
    [InlineData("https://replays.site.test/r/abcdefghij", true)]
    [InlineData("http://site.test/r/abcdefghij", false)]
    [InlineData("https://site.test.evil.example/r/x", false)]
    [InlineData("https://evil.example/?https://site.test/", false)]
    [InlineData("https://user@site.test/r/x", false)]
    [InlineData("https://site.test:8443/r/x", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("", false)]
    public void Replay_links_open_only_on_the_site(string url, bool opens)
    {
        var account = Service(new AccountActivity(null));
        Assert.Equal(opens, account.OpenSiteLink(url));
        Assert.Equal(opens ? 1 : 0, _opened.Count);
    }

    [Fact]
    public void A_crash_report_is_announced_as_going_not_gone()
    {
        Assert.Equal("A crash report will be sent automatically (Settings → Data).", Loc.T("Host.Crash.AutoSent"));
        Assert.Equal("Отчёт о сбое будет отправлен автоматически (Настройки → Данные).", Loc.Table("ru")["Host.Crash.AutoSent"]);
    }

    [Fact]
    public async Task Offline_asks_the_site_again_on_a_backoff_and_a_later_success_connects()
    {
        _site.Offline = true;
        var account = await RestoredAsync(activity: new AccountActivity(null));
        Assert.Equal(AccountLink.Offline, AccountConnection.Of(account, DateTime.UtcNow).Link);

        // While the site stays out of reach: a light /me after 1, 2, 4, 8, then every 15 minutes, one request each.
        foreach (var minutes in new[] { 1, 2, 4, 8, 15, 15 })
        {
            Assert.Equal(_time.GetUtcNow().UtcDateTime.AddMinutes(minutes), account.NextTry);
            var before = _site.Requests;
            _time.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Equal(before, _site.Requests);
            _time.Advance(TimeSpan.FromSeconds(1));
            await account.PendingRetry!;
            Assert.Equal(before + 1, _site.Requests);
            Assert.Equal(AccountLink.Offline, AccountConnection.Of(account, DateTime.UtcNow).Link);
            Assert.True(account.SignedIn);
        }

        // The site is back: the next try connects, and nothing more is scheduled.
        _site.Offline = false;
        _time.Advance(TimeSpan.FromMinutes(15));
        await account.PendingRetry!;
        Assert.Equal(AccountLink.Connected, AccountConnection.Of(account, DateTime.UtcNow).Link);
        Assert.Null(account.NextTry);
        Assert.Null(account.LastCheckError);
        var settled = _site.Requests;
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(settled, _site.Requests);

        // Signing out stops a retry that was due.
        account.Activity.Failed(SiteService.Games, "OFFLINE");
        Assert.NotNull(account.NextTry);
        await account.SignOutAsync();
        Assert.Null(account.NextTry);
        settled = _site.Requests;
        _time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(settled, _site.Requests);
    }

    [Fact]
    public async Task Turning_sending_off_never_leaves_the_connection_offline()
    {
        var account = await RestoredAsync(activity: new AccountActivity(null));
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings { UploadGamesToProfile = true }, Data());
        vm.AttachAccount(account);
        account.Activity.Failed(SiteService.Games, "OFFLINE", DateTime.UtcNow.AddMinutes(5));
        Assert.Equal(AccountLink.Offline, vm.AccountConnection.Link);

        vm.UploadGames = false;
        Assert.False(account.Activity[SiteService.Games].Failing);
        Assert.Equal(AccountLink.Connected, vm.AccountConnection.Link);
        Assert.Equal(string.Empty, vm.AccountGamesPause);
        Assert.Null(account.NextTry);
    }

    [Fact]
    public async Task A_5xx_with_a_code_is_no_connection_and_a_replay_4xx_keeps_its_code()
    {
        // The site fails with its own code in the body: still "no connection", the sign-in is kept.
        _site.TokenFailure = (503, new { error = "maintenance" });
        var account = await RestoredAsync(activity: new AccountActivity(null));
        Assert.True(account.SignedIn);
        Assert.Equal("SERVER_ERROR", account.LastCheckError?.Code);
        Assert.Equal(AccountLink.Offline, AccountConnection.Of(account, DateTime.UtcNow).Link);
        _site.TokenFailure = (500, new { error = new { code = "invalid_grant", message = "db down" } });
        Assert.Equal("SERVER_ERROR", (await account.CheckNowAsync())?.Code);
        Assert.True(account.SignedIn);

        // A replay the site turns down with its own 4xx: REFUSED to the card, the site's code in the journal.
        _site.TokenFailure = null;
        Assert.Null(await account.CheckNowAsync());
        _site.Replay = (422, new { error = new { code = "replay_invalid" } });
        Assert.Equal("REFUSED", (await account.UploadReplayAsync("H4sI")).Error);
        Assert.Equal("REPLAY_INVALID", account.Activity[SiteService.Replays].LastError);
        _site.Replay = (410, new { });
        Assert.Equal("REFUSED", (await account.UploadReplayAsync("H4sI")).Error);
        Assert.Equal("REFUSED", account.Activity[SiteService.Replays].LastError);
        _site.Replay = (502, new { error = "bad_gateway" });
        Assert.Equal("SERVER_ERROR", (await account.UploadReplayAsync("H4sI")).Error);
        Assert.Equal(AccountLink.Offline, AccountConnection.Of(account, DateTime.UtcNow).Link);
    }

    [Theory]
    [InlineData(503, "maintenance", "SERVER_ERROR")]
    [InlineData(503, "temporarily_unavailable", "TEMPORARILY_UNAVAILABLE")]
    [InlineData(500, null, "SERVER_ERROR")]
    [InlineData(400, "invalid_grant", "INVALID_GRANT")]
    [InlineData(404, null, "NOT_AVAILABLE")]
    [InlineData(401, null, "BAD_RESPONSE")]
    public void A_failed_answer_names_its_code(int status, string? code, string expected) =>
        Assert.Equal(expected, AccountClient.FailureCode(status, code));

    [Theory]
    [InlineData("OFFLINE", "OFFLINE")]
    [InlineData("replay_invalid", "REPLAY_INVALID")]
    [InlineData("HTTP_403", "HTTP_403")]
    [InlineData("v1.2-beta", "V1.2-BETA")]
    [InlineData("two words", "OTHER")]
    [InlineData("<script>", "OTHER")]
    [InlineData("код", "OTHER")]
    [InlineData("", "OTHER")]
    public void The_journal_keeps_only_short_plain_codes(string code, string kept)
    {
        var journal = new AccountActivity(null);
        journal.Failed(SiteService.Replays, code);
        Assert.Equal(kept, journal[SiteService.Replays].LastError);
        journal.Failed(SiteService.Replays, new string('A', 200));
        Assert.Equal(new string('A', AccountActivity.MaxCodeLength), journal[SiteService.Replays].LastError);
    }

    [Fact]
    public async Task Another_account_starts_the_game_counters_over()
    {
        // The journal counted games for another account on this machine.
        var journal = Journal(null);
        journal.BindAccount("u-1");
        journal.CountGames(12, 2, 1);
        journal.Failed(SiteService.Games, "SERVER_ERROR", DateTime.UtcNow.AddMinutes(5));
        journal.Succeeded(SiteService.Replays);

        var account = await RestoredAsync(activity: journal);
        Assert.Equal("u-42", journal.AccountId);
        Assert.Equal((0L, 0L, 0L), (journal.GamesSent, journal.GamesRefused, journal.GamesWithoutStatistics));
        Assert.Null(journal.LastGameSentAt);
        Assert.Equal(SiteServiceRecord.Empty, journal[SiteService.Games]);
        Assert.NotNull(journal[SiteService.Replays].LastSuccess);

        // The same account again keeps them; a journal from before accounts were recorded is taken as the account's own.
        journal.CountGames(3, 0, 0);
        await account.CheckNowAsync();
        Assert.Equal(3, journal.GamesSent);
        var old = Journal(null);
        old.CountGames(5, 0, 0);
        old.BindAccount("u-42");
        Assert.Equal(5, old.GamesSent);
    }

    [Fact]
    public async Task My_replays_open_on_the_site_even_when_me_names_another_host()
    {
        _site.ProfileUrl = "https://evil.example/profile/";
        var account = await RestoredAsync(activity: new AccountActivity(null));
        Assert.Equal("https://site.test/profile/#account-replays", account.ReplaysUrl.AbsoluteUri);
        _site.ProfileUrl = "https://site.test/u/anna/";
        Assert.Null(await account.CheckNowAsync());
        Assert.Equal("https://site.test/u/anna/#account-replays", account.ReplaysUrl.AbsoluteUri);
    }

    [Fact]
    public void A_burst_of_changes_is_one_write_after_a_moment()
    {
        var journal = Journal();
        journal.Succeeded(SiteService.Account);
        journal.CountGames(3, 0, 0);
        journal.Failed(SiteService.Games, "OFFLINE");
        Assert.Equal(0, journal.Writes);
        Assert.False(File.Exists(JournalPath));

        _time.Advance(AccountActivity.WriteDelay);
        Assert.Equal(1, journal.Writes);
        var again = new AccountActivity(JournalPath);
        Assert.Equal(3, again.GamesSent);
        Assert.Equal("OFFLINE", again[SiteService.Games].LastError);

        // Nothing new: nothing written; the next change waits for its own moment.
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, journal.Writes);
        journal.CountGames(1, 0, 0);
        journal.Flush();
        Assert.Equal(2, journal.Writes);
        Assert.Equal(4, new AccountActivity(JournalPath).GamesSent);
    }

    /// <summary>hearthpulse.net in miniature: token refresh, <c>/me</c>, the limits, crash reports; anything else is 404.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        private int _issued;

        public bool Offline { get; set; }

        public bool RejectRefresh { get; set; }

        public int LimitsStatus { get; set; } = 200;

        public int CrashStatus { get; set; } = 201;

        /// <summary>A failed answer to the token refresh (status and JSON body); null: the refresh works.</summary>
        public (int Status, object Body)? TokenFailure { get; set; }

        /// <summary>The answer to a replay upload; null: no such route yet (404).</summary>
        public (int Status, object Body)? Replay { get; set; }

        public string ProfileUrl { get; set; } = "https://site.test/profile/";

        /// <summary>Requests that reached the site (or tried to, offline).</summary>
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var path = request.RequestUri!.AbsolutePath["/api/v1/".Length..];
            return Task.FromResult(path switch
            {
                "oauth/token" when RejectRefresh => Json(400, new { error = "invalid_grant" }),
                "oauth/token" when TokenFailure is { } failure => Json(failure.Status, failure.Body),
                "tracker/replays" when Replay is { } replay => Json(replay.Status, replay.Body),
                "oauth/token" => Json(200, new { access_token = $"acc-{++_issued}", refresh_token = $"ref-{_issued}", token_type = "Bearer", expires_in = 900, scope = AccountClient.Scope }),
                "oauth/revoke" => Json(200, new { }),
                "me" => Json(200, new
                {
                    user = new { id = "u-42", publicProfileId = "", profileUrl = ProfileUrl, email = "a@b.c", name = "Anna", avatarInitials = "AN" },
                    subscription = new { hasAccess = false, source = "none", checkedAt = "2026-11-05T10:00:00.000Z", stale = false, entitlements = new { } },
                }),
                "usage/limits" when LimitsStatus == 200 => Json(200, new
                {
                    enabled = true,
                    resetsAt = "2026-11-06T00:00:00.000Z",
                    features = new Dictionary<string, object> { [UsageGate.BgPoolStats] = new { section = Entitlements.Battlegrounds, freePerDay = 3 } },
                }),
                "usage/limits" => new HttpResponseMessage((HttpStatusCode)LimitsStatus),
                "crash-reports" => new HttpResponseMessage((HttpStatusCode)CrashStatus),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(int status, object value) =>
            new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
