using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// The pool panel's texts in the interface language, made when they are shown: the view keeps the English labels and
/// tags (matched and compared), so a language switch only needs the panel rebuilt.
/// </summary>
public static class PoolDisplay
{
    private static readonly Dictionary<string, string> ReasonKeys = new(StringComparer.Ordinal)
    {
        ["a named group of cards that the catalog does not mark"] = "Pool.Reason.NamedGroup",
        ["the pool depends on another card"] = "Pool.Reason.OtherCard",
        ["the pool depends on the game state"] = "Pool.Reason.GameState",
        [CardPoolEngine.CatalogCannotAnswer] = "Pool.Reason.CatalogCannot",
    };

    /// <summary>A histogram's title ("Cost", "Attack", "Health").</summary>
    public static string Stat(string title) => Loc.Or("Pool.Stat." + title, title);

    /// <summary>A mechanic's label (<see cref="PoolKeywords.Shown"/>) by its tag.</summary>
    public static PoolMechanic Mechanic(PoolMechanic mechanic) =>
        mechanic with { Label = Loc.Or("Pool.Mechanic." + mechanic.Tag.TrimStart('#'), mechanic.Label) };

    /// <summary>Why a pool is not computed (<see cref="CardPoolView.Uncomputed"/>).</summary>
    public static string Reason(string reason) => ReasonKeys.TryGetValue(reason, out var key) ? Loc.T(key) : reason;

    /// <summary>Where the pool comes from (<see cref="CardPoolView.Source"/>): the card text, or the source's name.</summary>
    public static string Source(string source) => source == CardPoolEngine.TextSource ? Loc.T("Pool.Source.CardText") : source;

    /// <summary>Why the featured card is highlighted (<see cref="CardPoolView.FeaturedReason"/>).</summary>
    public static string FeaturedReason(string? reason) => reason is null ? string.Empty : Loc.T("Pool.FeaturedReason");
}
