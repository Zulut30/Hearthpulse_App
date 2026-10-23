using System.Text.Json;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.Accounts;

/// <summary>
/// Paid access of the HeartPulse account, from the <c>subscription</c> part of <c>GET /api/v1/me</c>: whether any paid
/// tier is active, which providers grant it (Boosty, Patreon, Telegram) and which site sections it opens. The site
/// derives this from its own provider checks; the app only reads it (paywalls key off <see cref="Has"/>).
/// </summary>
public sealed record Entitlements(bool HasAccess, IReadOnlyList<string> Sources, IReadOnlyList<string> Sections, bool Stale, DateTime? CheckedAt)
{
    public const string Arena = "arena";
    public const string Battlegrounds = "battlegrounds";
    public const string Standard = "standard";
    public const string Contests = "contests";
    public const string GuidesArchive = "guidesArchive";
    public const string ArenaArticles = "arenaArticles";
    public const string BattlegroundsArticles = "battlegroundsArticles";

    // The site's SubscriptionEntitlementKey values, in its order, with the names the app shows.
    private static readonly (string Key, string Label)[] Known =
    [
        (Arena, "Arena"), (Battlegrounds, "Battlegrounds"), (Standard, "Standard"), (Contests, "Contests"),
        (GuidesArchive, "Guides archive"), (ArenaArticles, "Arena articles"), (BattlegroundsArticles, "Battlegrounds articles"),
    ];

    /// <summary>No paid access (signed out, or the site has not confirmed a subscription).</summary>
    public static readonly Entitlements None = new(false, [], [], Stale: true, CheckedAt: null);

    /// <summary>The section (one of the constants above) is paid for.</summary>
    public bool Has(string section) => HasAccess && Sections.Contains(section, StringComparer.Ordinal);

    public bool Boosty => Sources.Contains("boosty", StringComparer.Ordinal);

    public bool Patreon => Sources.Contains("patreon", StringComparer.Ordinal);

    /// <summary>One line for the account card, e.g. "Boosty subscription: Arena, Battlegrounds, Standard".</summary>
    public string Summary
    {
        get
        {
            if (!HasAccess || Sections.Count == 0)
            {
                return Loc.T("Core.Account.NoSubscription");
            }

            var providers = string.Join(" + ", Sources.Select(s => s switch
            {
                "boosty" => "Boosty",
                "patreon" => "Patreon",
                "telegram" => "Telegram",
                _ => s,
            }));
            var sections = Known.All(k => Sections.Contains(k.Key, StringComparer.Ordinal))
                ? Loc.T("Core.Account.AllSections")
                : string.Join(", ", Known.Where(k => Sections.Contains(k.Key, StringComparer.Ordinal)).Select(k => Loc.Or("Core.Account.Section." + k.Key, k.Label)));
            return providers.Length > 0 ? Loc.F("Core.Account.ProviderSubscription", providers, sections) : Loc.F("Core.Account.Subscription", sections);
        }
    }

    /// <summary>
    /// Reads <c>{"hasAccess", "source": "boosty,patreon" | "none", "checkedAt", "stale", "entitlements": {key: bool}}</c>.
    /// Anything missing or malformed counts as no access (fail closed).
    /// </summary>
    public static Entitlements Parse(JsonElement subscription)
    {
        if (subscription.ValueKind != JsonValueKind.Object)
        {
            return None;
        }

        var sections = subscription.TryGetProperty("entitlements", out var map) && map.ValueKind == JsonValueKind.Object
            ? map.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.True).Select(p => p.Name).ToList()
            : [];
        var source = subscription.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : string.Empty;
        var sources = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x != "none").ToList();
        DateTime? checkedAt = subscription.TryGetProperty("checkedAt", out var c) && c.ValueKind == JsonValueKind.String && c.TryGetDateTime(out var date)
            ? date.ToUniversalTime()
            : null;
        return new Entitlements(
            subscription.TryGetProperty("hasAccess", out var a) && a.ValueKind == JsonValueKind.True && sections.Count > 0,
            sources,
            sections,
            !subscription.TryGetProperty("stale", out var st) || st.ValueKind != JsonValueKind.False,
            checkedAt);
    }
}
