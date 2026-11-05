using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Accounts;

/// <summary>How the app stands with hearthpulse.net, in one word.</summary>
public enum AccountLink
{
    /// <summary>No account: nothing goes to the site.</summary>
    SignedOut,

    /// <summary>Signed in, and the site answered last time.</summary>
    Connected,

    /// <summary>Signed in, but the site could not be reached (or answered with a failure) last time.</summary>
    Offline,

    /// <summary>The site ended the sign-in (or its 30 days ran out): the player has to sign in again.</summary>
    SessionEnded,
}

/// <summary>
/// The one connection state of the account center and the main window's indicator, derived from the
/// <see cref="AccountService"/> and its <see cref="AccountActivity"/>: <see cref="At"/> is when the site last answered
/// (<see cref="AccountLink.Connected"/>) or when the next try is due (<see cref="AccountLink.Offline"/>).
/// </summary>
public sealed record AccountConnection(AccountLink Link, DateTime? At)
{
    // Codes that say the site could not be reached or failed for now; any other failure is a real answer. NO_TOKEN (no
    // sign-in to send with) is not one: a refresh that failed for want of a connection says so in the account's record.
    private static readonly HashSet<string> Unreachable =
        ["OFFLINE", "SERVER_ERROR", "TEMPORARILY_UNAVAILABLE", "RATE_LIMITED", "BAD_RESPONSE"];

    // Account failures that do not end the sign-in (the session is kept and tried again).
    private static readonly HashSet<string> Transient =
        ["OFFLINE", "SERVER_ERROR", "TEMPORARILY_UNAVAILABLE", "RATE_LIMITED", "NOT_AVAILABLE", "BAD_RESPONSE", "SIGNED_OUT", "NO_TOKEN"];

    // The services whose failures say something about the connection (crash reports go without the sign-in).
    private static readonly SiteService[] Linked = [SiteService.Account, SiteService.Games, SiteService.Replays, SiteService.Limits];

    /// <summary>The state as of <paramref name="utcNow"/>.</summary>
    public static AccountConnection Of(AccountService account, DateTime utcNow)
    {
        var activity = account.Activity;
        var me = activity[SiteService.Account];
        if (!account.SignedIn)
        {
            // The site took the sign-in back (at start or later): say so until the player signs in or out.
            return me.Failing && me.LastError is { } code && !Transient.Contains(code)
                ? new AccountConnection(AccountLink.SessionEnded, me.LastErrorAt)
                : new AccountConnection(AccountLink.SignedOut, null);
        }

        if (account.SessionExpires is { } end && utcNow >= end)
        {
            return new AccountConnection(AccountLink.SessionEnded, end);
        }

        var records = Linked.Select(s => activity[s]).ToList();
        var lastSuccess = records.Select(r => r.LastSuccess).Max();
        var down = records.Where(r => r.Failing && IsUnreachable(r.LastError) && (lastSuccess is not { } ok || r.LastErrorAt > ok)).ToList();
        if (down.Count > 0)
        {
            // The earliest try still ahead: the account's own retry (AccountService.NextTry) or a service's.
            var next = down.Select(r => r.NextTry).Append(account.NextTry).Where(t => t > utcNow).Min();
            return new AccountConnection(AccountLink.Offline, next);
        }

        return new AccountConnection(AccountLink.Connected, lastSuccess);
    }

    /// <summary>A code that means "could not reach the site" (HTTP 5xx included).</summary>
    public static bool IsUnreachable(string? code) =>
        code is not null && (Unreachable.Contains(code) || code.StartsWith("HTTP_5", StringComparison.Ordinal));

    /// <summary>"Подключено · проверено в 14:05", "Нет связи с сайтом — повтор в 14:20", "Вход истёк — войдите снова".</summary>
    public string Text(DateTime utcNow) => Link switch
    {
        AccountLink.Connected => At is { } at ? Loc.F("Account.Link.ConnectedAt", Clock(at, utcNow)) : Loc.T("Account.Link.Connected"),
        AccountLink.Offline => At is { } next ? Loc.F("Account.Link.OfflineRetry", Clock(next, utcNow)) : Loc.T("Account.Link.Offline"),
        AccountLink.SessionEnded => Loc.T("Account.Link.SessionEnded"),
        _ => Loc.T("Account.Link.SignedOut"),
    };

    /// <summary>The indicator's short label: "Подключено", "Нет связи", "Вход истёк".</summary>
    public string Short => Link switch
    {
        AccountLink.Connected => Loc.T("Account.Link.Connected"),
        AccountLink.Offline => Loc.T("Account.Link.OfflineShort"),
        AccountLink.SessionEnded => Loc.T("Account.Link.SessionEndedShort"),
        _ => Loc.T("Account.Link.SignedOut"),
    };

    /// <summary>A moment in the player's time: "14:05" today, "3 ноя 14:05" on another day.</summary>
    public static string Clock(DateTime utc, DateTime utcNow)
    {
        var culture = Loc.IsRussian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture;
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var today = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc).ToLocalTime().Date;
        return local.ToString(local.Date == today ? "HH:mm" : "d MMM HH:mm", culture);
    }
}
