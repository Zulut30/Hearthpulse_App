using System.Text.RegularExpressions;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.Core.CardPool;

/// <summary>What a "this game" card looks back at.</summary>
public enum MatchEvent
{
    /// <summary>Minions that died this game ("friendly minion that died this game", Resurrect).</summary>
    Died,

    /// <summary>Spells you've cast this game.</summary>
    Cast,

    /// <summary>Cards (or minions) you've played this game.</summary>
    Played,

    /// <summary>Minions you've summoned this game.</summary>
    Summoned,
}

public enum MatchSide
{
    Friendly,
    Enemy,
    Both,
}

/// <summary>
/// The cards of this game a card's text is about: "Trigger the Deathrattle of a random friendly minion that died this
/// game" is Died, friendly, with Deathrattle.
/// </summary>
/// <param name="Mechanic">A mechanic the cards must have (DEATHRATTLE, TAUNT...), from the card data.</param>
/// <param name="Race">A minion type (UNDEAD, BEAST...).</param>
/// <param name="School">A spell school, as the SPELL_SCHOOL tag numbers it (2 Fire, 7 Fel...).</param>
/// <param name="CardType">MINION or SPELL when the text says so; null for any card.</param>
/// <param name="Cost">"1-Cost": the card's base cost.</param>
public sealed record MatchRule(MatchEvent Event, MatchSide Side, string? Mechanic = null, string? Race = null, int? School = null, string? CardType = null, int? Cost = null);

/// <summary>One card of the list with how many times it counts ("×2").</summary>
public sealed record MatchListRow(string CardId, string Name, int Cost, string Rarity, int Count);

/// <summary>The list shown beside a hovered card in hand: its title in the interface language, the total and the cards.</summary>
public sealed record MatchListView(string SourceCardId, string SourceName, string Title, int Total, IReadOnlyList<MatchListRow> Rows);

/// <summary>
/// Cards whose text looks back at this game ("died this game", "you've cast this game", Resurrect) and the cards of the
/// current game that fit. Only what the player saw: their own plays, and deaths on the board (public for both sides).
/// The rule is read from the English card text (the pool catalog's), so it works in every client language.
/// </summary>
public static partial class MatchLists
{
    private static readonly Dictionary<string, string> Races = new(StringComparer.OrdinalIgnoreCase)
    {
        ["beast"] = "BEAST", ["beasts"] = "BEAST", ["demon"] = "DEMON", ["demons"] = "DEMON", ["dragon"] = "DRAGON", ["dragons"] = "DRAGON",
        ["elemental"] = "ELEMENTAL", ["elementals"] = "ELEMENTAL", ["mech"] = "MECHANICAL", ["mechs"] = "MECHANICAL", ["murloc"] = "MURLOC",
        ["murlocs"] = "MURLOC", ["naga"] = "NAGA", ["pirate"] = "PIRATE", ["pirates"] = "PIRATE", ["quilboar"] = "QUILBOAR", ["undead"] = "UNDEAD",
        ["totem"] = "TOTEM", ["totems"] = "TOTEM", ["draenei"] = "DRAENEI",
    };

    private static readonly Dictionary<string, int> Schools = new(StringComparer.OrdinalIgnoreCase)
    {
        ["arcane"] = 1, ["fire"] = 2, ["frost"] = 3, ["nature"] = 4, ["holy"] = 5, ["shadow"] = 6, ["fel"] = 7,
    };

    /// <summary>What the list can hold: a text about "each friendly Treant" or "Ysondre" (a name) has no list.</summary>
    private static readonly HashSet<string> Nouns = new(StringComparer.OrdinalIgnoreCase) { "minion", "minions", "card", "cards", "spell", "spells" };

    private static readonly (string Word, string Mechanic)[] Mechanics =
    [
        ("Deathrattle", "DEATHRATTLE"), ("Taunt", "TAUNT"), ("Divine Shield", "DIVINE_SHIELD"), ("Rush", "RUSH"), ("Lifesteal", "LIFESTEAL"),
        ("Reborn", "REBORN"), ("Battlecry", "BATTLECRY"),
    ];

    /// <summary>The rule of a card text, or null when it does not look back at this game.</summary>
    /// <param name="ownName">The card's English name: a text that counts the card itself ("each time Ysondre has died") has no list.</param>
    public static MatchRule? Parse(string text, string? ownName = null)
    {
        var clean = Clean(text);
        MatchEvent kind;
        int at;
        string clause;
        if (DiedRegex().Match(clean) is { Success: true } died)
        {
            (kind, at) = (MatchEvent.Died, died.Index);
            clause = ClauseBefore(clean, at);
            if (QualifiedAfter(clean, died))
            {
                return null;
            }
        }
        else if (YouRegex().Match(clean) is { Success: true } you)
        {
            if (QualifiedAfter(clean, you))
            {
                return null;
            }

            kind = you.Groups["verb"].Value.ToLowerInvariant() switch
            {
                "cast" => MatchEvent.Cast,
                "summoned" => MatchEvent.Summoned,
                _ => MatchEvent.Played,
            };
            at = you.Index;
            clause = ClauseBefore(clean, at);
        }
        else if (ResurrectRegex().Match(clean) is { Success: true } resurrect)
        {
            kind = MatchEvent.Died;
            at = resurrect.Index;
            var rest = clean[(resurrect.Index + resurrect.Length)..];
            var end = rest.IndexOfAny(['.', ';', ':', '(']);
            clause = "friendly " + (end < 0 ? rest : rest[..end]);
        }
        else
        {
            return null;
        }

        if (ownName is { Length: > 0 } && clause.Contains(ownName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var words = WordRegex().Matches(clause).Select(m => m.Value).ToList();
        if (words.Count == 0 || !(Nouns.Contains(words[^1]) || Races.ContainsKey(words[^1])))
        {
            return null;
        }

        var side = clause.Contains("enemy", StringComparison.OrdinalIgnoreCase) || clause.Contains("opponent", StringComparison.OrdinalIgnoreCase) ? MatchSide.Enemy
            : kind != MatchEvent.Died || words.Any(w => w.Equals("friendly", StringComparison.OrdinalIgnoreCase) || w.Equals("your", StringComparison.OrdinalIgnoreCase)) ? MatchSide.Friendly
            : MatchSide.Both;
        if (side == MatchSide.Enemy && kind != MatchEvent.Died)
        {
            return null;
        }

        var mechanic = Mechanics.FirstOrDefault(m => clause.Contains(m.Word, StringComparison.Ordinal)).Mechanic;
        var race = words.Select(w => Races.GetValueOrDefault(w)).FirstOrDefault(r => r is not null);
        var spell = words.Any(w => w.StartsWith("spell", StringComparison.OrdinalIgnoreCase));
        int? school = null;
        for (var i = 0; i + 1 < words.Count; i++)
        {
            if (Schools.TryGetValue(words[i], out var s) && words[i + 1].StartsWith("spell", StringComparison.OrdinalIgnoreCase))
            {
                school = s;
            }
        }

        var type = kind is MatchEvent.Died or MatchEvent.Summoned ? "MINION"
            : kind == MatchEvent.Cast || spell ? "SPELL"
            : words.Any(w => w.StartsWith("minion", StringComparison.OrdinalIgnoreCase)) || race is not null ? "MINION"
            : null;
        // "a 1, 2, and 3-Cost minion" names several costs: the list keeps them all.
        int? cost = CostRegex().Match(clause) is { Success: true } c && !clause.Contains(',', StringComparison.Ordinal)
            ? int.Parse(c.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new MatchRule(kind, side, mechanic, race, school, type, cost);
    }

    /// <summary>The cards of the current game that fit <paramref name="rule"/>, most first by cost then name.</summary>
    public static MatchListView Build(string cardId, MatchRule rule, GameState game, CounterTracker tracker, CardDb cards)
    {
        var ids = new List<string>();
        foreach (var player in Players(game, rule.Side))
        {
            var log = tracker.For(player);
            ids.AddRange(rule.Event switch
            {
                MatchEvent.Died => log.Died,
                MatchEvent.Summoned => log.Summoned,
                _ => log.Played.Where(p => rule.School is null || p.SpellSchool == rule.School).Select(p => p.CardId),
            });
        }

        var matching = ids.Where(id => id.Length > 0 && Fits(cards.Get(id), rule)).ToList();
        var rows = matching
            .GroupBy(id => id, StringComparer.Ordinal)
            .Select(g => (Card: cards.Get(g.Key)!, Count: g.Count()))
            .Select(r => new MatchListRow(r.Card.Id, r.Card.Name, r.Card.Cost, r.Card.Rarity, r.Count))
            .OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.CurrentCulture)
            .ToList();
        return new MatchListView(cardId, cards.Get(cardId)?.Name ?? cardId, Title(rule), matching.Count, rows);
    }

    /// <summary>"Your minions that died · Deathrattle", in the interface language.</summary>
    public static string Title(MatchRule rule)
    {
        var what = rule switch
        {
            { Event: MatchEvent.Died, Side: MatchSide.Friendly } => Loc.T("Pool.List.Died.Friendly"),
            { Event: MatchEvent.Died, Side: MatchSide.Enemy } => Loc.T("Pool.List.Died.Enemy"),
            { Event: MatchEvent.Died } => Loc.T("Pool.List.Died.Both"),
            { Event: MatchEvent.Cast } or { Event: MatchEvent.Played, CardType: "SPELL" } => Loc.T("Pool.List.Cast"),
            { Event: MatchEvent.Summoned } => Loc.T("Pool.List.Summoned"),
            { CardType: "MINION" } => Loc.T("Pool.List.PlayedMinions"),
            _ => Loc.T("Pool.List.Played"),
        };
        var parts = new List<string> { what };
        if (rule.Mechanic is { } m)
        {
            parts.Add(Loc.Or("Pool.Mechanic." + m, m));
        }

        if (rule.Race is { } r)
        {
            parts.Add(Loc.Or("Core.Tribe." + r, r));
        }

        if (rule.School is { } s)
        {
            parts.Add(Loc.T("Pool.School." + s.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        if (rule.Cost is { } cost)
        {
            parts.Add(Loc.F("Pool.List.Cost", cost));
        }

        return string.Join(" · ", parts);
    }

    private static bool Fits(Card? card, MatchRule rule) =>
        card is not null
        && (rule.CardType is null || card.Type == rule.CardType)
        && (rule.Mechanic is null || card.Mechanics.Contains(rule.Mechanic))
        && (rule.Race is null || card.HasRace(rule.Race))
        && (rule.Cost is null || card.Cost == rule.Cost);

    private static IEnumerable<int> Players(GameState game, MatchSide side)
    {
        if (side != MatchSide.Enemy && game.LocalPlayerId != 0)
        {
            yield return game.LocalPlayerId;
        }

        if (side != MatchSide.Friendly && game.OpponentPlayerId != 0)
        {
            yield return game.OpponentPlayerId;
        }
    }

    /// <summary>"cards you played this game that didn't start in your deck": a condition the list cannot check.</summary>
    private static bool QualifiedAfter(string text, Match match)
    {
        var tail = text[(match.Index + match.Length)..];
        var end = tail.IndexOfAny(['.', ',', ';', ':', '(']);
        return (end < 0 ? tail : tail[..end]).Contains(" that ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The text before <paramref name="at"/> back to the start of its sentence or clause ("Battlecry:", "(").</summary>
    private static string ClauseBefore(string text, int at)
    {
        var start = text.LastIndexOfAny(['.', ';', ':', '(', ')', '!'], Math.Max(0, at - 1)) + 1;
        return text[start..at];
    }

    private static string Clean(string text) => TagRegex().Replace(text.Replace("\n", " ", StringComparison.Ordinal), string.Empty).Replace("[x]", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"\b(?:that |which |who )?(?:have |has |had )?died this (?:game|match)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DiedRegex();

    [GeneratedRegex(@"\byou(?:'ve| have)? (?<verb>cast|played|summoned) this (?:game|match)\b", RegexOptions.IgnoreCase)]
    private static partial Regex YouRegex();

    [GeneratedRegex(@"\bResurrect(?:s|ed)?\b")]
    private static partial Regex ResurrectRegex();

    [GeneratedRegex(@"[A-Za-z']+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"(\d+)-Cost")]
    private static partial Regex CostRegex();

    [GeneratedRegex(@"<[^>]+>|\$|#")]
    private static partial Regex TagRegex();
}
