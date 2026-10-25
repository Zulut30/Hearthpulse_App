using Hearthpulse.Core.Cards;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Secrets;

/// <summary>Which card pool a game's Secrets come from.</summary>
public enum SecretFormat
{
    Standard,
    Wild,
    Classic,

    /// <summary>Arena: its rotating pool is not in the card data, so every Secret of the class (as in Wild).</summary>
    Arena,
}

/// <summary>
/// One Secret the opponent may have: every printing of the card (original, Core, other reprints) folded into one.
/// <see cref="Key"/> is the reprint-free id of the printing shown.
/// </summary>
public sealed record SecretOption(string Key, string CardId, string Name, int Cost, string CardClass, bool Legendary, SecretRule? Rule, IReadOnlyList<string> CardIds);

/// <summary>
/// Collectible Secrets per class and format, from the card database (HearthstoneJSON, mechanic SECRET). Built once per
/// class and format and kept until the card database changes.
/// </summary>
public sealed class SecretPool
{
    private readonly Dictionary<(string Class, SecretFormat Format), IReadOnlyList<SecretOption>> _cache = new();
    private readonly List<Card> _secrets;

    public SecretPool(CardDb cards)
    {
        Cards = cards;
        _secrets = cards.All
            .Where(c => c.Collectible && c.Type == "SPELL" && c.Mechanics.Contains("SECRET"))
            .ToList();
    }

    public CardDb Cards { get; }

    /// <summary>The format the opponent's Secrets come from in a game.</summary>
    public static SecretFormat FormatOf(GameState game) =>
        game.GameType.Contains("ARENA", StringComparison.Ordinal) ? SecretFormat.Arena
        : game.FormatType switch
        {
            "FT_STANDARD" => SecretFormat.Standard,
            "FT_CLASSIC" => SecretFormat.Classic,
            // Wild, Twist and anything new: the widest pool, so the real Secret is never missing.
            _ => SecretFormat.Wild,
        };

    /// <summary>The class's Secrets in a format, sorted by cost and name; empty for a class without any.</summary>
    public IReadOnlyList<SecretOption> For(string cardClass, SecretFormat format)
    {
        if (_cache.TryGetValue((cardClass, format), out var cached))
        {
            return cached;
        }

        var options = _secrets
            .Where(c => c.CardClass == cardClass && InFormat(c, format))
            .GroupBy(c => SecretRules.BaseId(c.Id), StringComparer.Ordinal)
            // A reprint under a new id (Snipe in Wonders) is still the same Secret: fold printings with one name.
            .GroupBy(g => g.First().Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => Option(g.SelectMany(x => x).ToList()))
            .OrderBy(o => o.Cost).ThenBy(o => o.Name, StringComparer.CurrentCulture)
            .ToList();
        _cache[(cardClass, format)] = options;
        return options;
    }

    private static bool InFormat(Card card, SecretFormat format) => format switch
    {
        SecretFormat.Standard => CardPoolTables.StandardSets.Contains(card.Set),
        SecretFormat.Classic => card.Set == "VANILLA",
        _ => card.Set != "VANILLA",
    };

    private static SecretOption Option(IReadOnlyList<Card> printings)
    {
        // The printing shown: one in Standard, else the original rather than a hidden Core copy.
        var shown = printings
            .OrderByDescending(c => CardPoolTables.StandardSets.Contains(c.Set))
            .ThenBy(c => c.Set == "CORE_HIDDEN")
            .ThenBy(c => c.Id.Length)
            .First();
        var rule = printings.Select(c => c.Id).FirstOrDefault(SecretRules.Knows) is { } known ? SecretRules.For(known) : null;
        return new SecretOption(
            SecretRules.BaseId(shown.Id),
            shown.Id,
            shown.Name,
            shown.Cost,
            shown.CardClass,
            printings.Any(c => c.IsLegendary),
            rule,
            printings.Select(c => c.Id).ToList());
    }
}
