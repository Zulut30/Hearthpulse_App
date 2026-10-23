using System.Text.RegularExpressions;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// One collectible Constructed card as the pool engine sees it: the fields generator filters ask about (type, class,
/// tribe, school, keywords, cost) and the English text the "Card Draw"/"Heal"/"Deal Face Damage" rules read.
/// </summary>
/// <param name="Classes">The card's classes: one, several for multi-class cards, or NEUTRAL.</param>
/// <param name="Races">Minion types; "ALL" for Amalgam-like minions that count as every type.</param>
/// <param name="Standard">In the current Standard format; every card here is in Wild.</param>
public sealed record PoolCard(
    string Id,
    int DbfId,
    string Name,
    string Text,
    string Type,
    string Rarity,
    int Cost,
    int? Attack,
    int? Health,
    IReadOnlyList<string> Classes,
    IReadOnlyList<string> Races,
    string? SpellSchool,
    IReadOnlyList<string> Mechanics,
    IReadOnlyList<string> ReferencedTags,
    string Set,
    bool Standard)
{
    /// <summary>The Russian name when the source has one (shown in a ruRU client).</summary>
    public string? NameRu { get; init; }

    public bool IsNeutral => Classes.Count == 0 || Classes.Contains("NEUTRAL");

    public bool HasClass(string cardClass) => Classes.Contains(cardClass);

    public bool HasRace(string race) => Races.Contains(race) || Races.Contains("ALL");

    public bool HasMechanic(string tag) => Mechanics.Contains(tag)
                                           // Some keywords are only listed as referenced tags on their own cards (Imbue).
                                           || (PoolText.ReferenceOnlyKeywords.Contains(tag) && ReferencedTags.Contains(tag));
}

/// <summary>Card text helpers shared by the parser and the stats rules.</summary>
public static partial class PoolText
{
    /// <summary>Keywords whose cards carry them only in referencedTags in HearthstoneJSON and the API.</summary>
    public static readonly IReadOnlySet<string> ReferenceOnlyKeywords = new HashSet<string>(StringComparer.Ordinal) { "IMBUE", "HERALD" };

    /// <summary>
    /// Plain one-line English text: no markup, no layout codes ([x], $, #, @, &amp;), plural forms "|4(card,cards)"
    /// reduced to the plural, whitespace collapsed.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var t = TagRegex().Replace(text, string.Empty);
        t = PluralRegex().Replace(t, "$2");
        t = t.Replace("[x]", string.Empty, StringComparison.Ordinal)
            .Replace('$', ' ').Replace('#', ' ').Replace('@', ' ').Replace('&', ' ')
            .Replace(' ', ' ').Replace('\n', ' ').Replace('\r', ' ')
            .Replace('’', '\'');
        t = SpaceRegex().Replace(t, " ").Trim();
        // "$3 damage" loses its marker; "Battlecry: " stays readable after the '&' no-break hints are gone.
        return t.Replace(" .", ".", StringComparison.Ordinal).Replace(" ,", ",", StringComparison.Ordinal).Replace(" :", ":", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\|4\(([^,)]*),([^)]*)\)")]
    private static partial Regex PluralRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
