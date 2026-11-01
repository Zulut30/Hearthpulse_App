using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// The cards a card in hand is about, shown beside it when hovered (<c>docs/discover-pool.md</c>, "Related cards"): the
/// tokens it makes and the collectible cards its text names. From the card data only, no hand-made table: a token's id
/// is its card's id with a suffix (<c>CS2_101</c> → <c>CS2_101t</c>; a <c>CORE_</c> reprint's tokens keep the original
/// id), and a named card is a collectible card's English name in the English text, outside bold keywords (a keyword such
/// as <b>Charge</b> is also a card's name). The same popup and rows as the "this game" list (<see cref="MatchLists"/>).
/// </summary>
public static partial class RelatedCards
{
    /// <summary>Card types a related card can be: never an enchantment, a hero power or the game's own entities.</summary>
    private static readonly HashSet<string> Types = new(StringComparer.Ordinal) { "MINION", "SPELL", "WEAPON", "LOCATION", "HERO" };

    /// <summary>Game keywords that are also card names but no mechanic in the card data (the text uses them as keywords).</summary>
    private static readonly string[] TextKeywords = ["Rewind", "Resurrect", "Silence", "Freeze", "Discover", "Recruit", "Corrupt", "Overload"];

    /// <summary>The longest list: a card that names more is about something else (a pool, a class).</summary>
    private const int MaxRows = 12;

    private static readonly ConditionalWeakTable<CardDb, Dictionary<string, List<string>>> Tokens = new();

    private static readonly ConditionalWeakTable<CardPoolCatalog, List<(string Name, string Id)>> Names = new();

    /// <summary>The related list of <paramref name="cardId"/>, or null when it has none.</summary>
    public static MatchListView? Of(string cardId, CardDb cards, CardPoolCatalog catalog)
    {
        if (cards.Get(cardId) is not { } source)
        {
            return null;
        }

        var ids = new List<string>();
        var tokens = Tokens.GetValue(cards, BuildTokens);
        foreach (var root in new[] { cardId, cardId.StartsWith("CORE_", StringComparison.Ordinal) ? cardId[5..] : null }.OfType<string>())
        {
            if (tokens.TryGetValue(root, out var made))
            {
                ids.AddRange(made);
            }
        }

        if (catalog.Get(cardId) is { Text.Length: > 0 } english)
        {
            var text = Bold().Replace(english.Text, " ");
            ids.AddRange(Names.GetValue(catalog, BuildNames)
                .Where(n => n.Id != cardId && n.Name != english.Name && Mentions(text, n.Name))
                .Select(n => n.Id));
        }

        var rows = ids.Distinct(StringComparer.Ordinal)
            .Select(cards.Get)
            .OfType<Card>()
            .Where(c => Types.Contains(c.Type) && c.Id != cardId)
            .Select(c => new MatchListRow(c.Id, c.Name, c.Cost, c.Rarity, 1))
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        return rows.Count is 0 or > MaxRows ? null : new MatchListView(cardId, source.Name, Loc.T("Related.Title"), rows.Count, rows);
    }

    /// <summary>Token ids by the id they hang off: <c>CS2_101t</c>, <c>CS2_101t2</c>, <c>EX1_506a</c> under their card.</summary>
    private static Dictionary<string, List<string>> BuildTokens(CardDb cards)
    {
        var tokens = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var card in cards.All)
        {
            if (!card.Collectible && Types.Contains(card.Type) && TokenId().Match(card.Id) is { Success: true } match
                && cards.Get(match.Groups["root"].Value) is { } root && root.Set == card.Set)
            {
                (tokens.TryGetValue(root.Id, out var list) ? list : tokens[root.Id] = []).Add(card.Id);
            }
        }

        return tokens;
    }

    /// <summary>
    /// Each collectible card's English name of two words or six letters at least, once (a reprint shares its name). A
    /// name that is also a keyword (Charge, Windfury, Silence: a mechanic or a referenced tag of some card) is left out:
    /// the text uses the keyword far more often than the card.
    /// </summary>
    private static List<(string, string)> BuildNames(CardPoolCatalog catalog)
    {
        var keywords = catalog.Cards.SelectMany(c => c.Mechanics.Concat(c.ReferencedTags))
            .Select(tag => tag.Replace('_', ' '))
            .Concat(TextKeywords)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return catalog.Cards
            .Where(c => (c.Name.Length >= 6 || c.Name.Contains(' ', StringComparison.Ordinal)) && !keywords.Contains(c.Name))
            .GroupBy(c => c.Name, StringComparer.Ordinal)
            .Select(g => (g.Key, g.First().Id))
            .ToList();
    }

    /// <summary>True when <paramref name="text"/> holds <paramref name="name"/> as whole words (case as written).</summary>
    private static bool Mentions(string text, string name)
    {
        for (var at = text.IndexOf(name, StringComparison.Ordinal); at >= 0; at = text.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            var end = at + name.Length;
            if ((at == 0 || !IsWord(text[at - 1])) && (end == text.Length || !IsWord(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '\'' or '_';

    [GeneratedRegex(@"^(?<root>[A-Za-z0-9_]+?)(?:t\d{0,2}[a-z]?|[ab])$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenId();

    [GeneratedRegex(@"<b>.*?</b>", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex Bold();
}
