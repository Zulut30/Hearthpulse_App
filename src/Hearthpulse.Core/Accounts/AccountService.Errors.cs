using System.Globalization;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Accounts;

public sealed partial class AccountService
{
    /// <summary>An HTTP status as a journal code: 404 means the site has no such route yet (NOT_AVAILABLE); other 4xx are <c>HTTP_4xx</c>.</summary>
    internal static string StatusCode(int status) => status switch
    {
        404 => "NOT_AVAILABLE",
        429 => "RATE_LIMITED",
        >= 500 => "SERVER_ERROR",
        _ => "HTTP_" + status.ToString(CultureInfo.InvariantCulture),
    };

    private static string Day(DateTime utc, string format) =>
        utc.ToLocalTime().ToString(format, Loc.IsRussian ? CultureInfo.GetCultureInfo("ru-RU") : CultureInfo.InvariantCulture);

    /// <summary>Turns an error code into a message for the account card.</summary>
    internal static AccountError Describe(string code) => new(code, Loc.T(code switch
    {
        "CANCELLED" => "Core.Account.Error.Cancelled",
        "ACCESS_DENIED" => "Core.Account.Error.AccessDenied",
        "EXPIRED_TOKEN" => "Core.Account.Error.ExpiredToken",
        "OFFLINE" => "Core.Account.Error.Offline",
        "SERVER_ERROR" or "TEMPORARILY_UNAVAILABLE" => "Core.Account.Error.ServerError",
        "RATE_LIMITED" => "Core.Account.Error.RateLimited",
        "NOT_AVAILABLE" or "INVALID_REQUEST" or "INVALID_CLIENT" or "UNAUTHORIZED_CLIENT" or "UNSUPPORTED_GRANT_TYPE"
            => "Core.Account.Error.NotAvailable",
        "INVALID_GRANT" or "INVALID_ACCESS_TOKEN" or "WRONG_ACCOUNT" => "Core.Account.Error.SignInEnded",
        "INSUFFICIENT_SCOPE" => "Core.Account.Error.InsufficientScope",
        "BAD_RESPONSE" => "Core.Account.Error.BadResponse",
        "SIGNED_OUT" => "Core.Account.Error.SignedOut",
        _ => "Core.Account.Error.Other",
    }));
}
