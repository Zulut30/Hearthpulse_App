using System.Text.RegularExpressions;
using Hearthlens.Core.Cards;

namespace Hearthlens.Core.Battlegrounds.Effects;

/// <summary>How much a card's text matters once a fight has started (boards are captured at the first attack).</summary>
public enum Relevance
{
    /// <summary>Tavern-only text (battlecries, end of turn, deathrattles that give cards...).</summary>
    None,

    /// <summary>Only keywords, which the simulator applies itself.</summary>
    Keywords,

    /// <summary>A Start of Combat effect that has already resolved when the boards are captured.</summary>
    Resolved,

    /// <summary>Matters only if another in-combat effect feeds it (e.g. "whenever you cast a spell"); not counted.</summary>
    Conditional,

    /// <summary>Changes the fight: needs an effect implementation, else the odds are an estimate.</summary>
    Relevant,
}

public enum Origin
{
    /// <summary>Pool minion labelled by hand from its card text.</summary>
    Pool,

    /// <summary>Token or summoned non-pool minion.</summary>
    Token,

    /// <summary>Classified by the text heuristic (Timewarped minions).</summary>
    Heuristic,
}

/// <summary>One row of the generated table.</summary>
public sealed record CardRelevance(string CardId, string GoldenCardId, Relevance Relevance, string Category, string Name, Origin Origin);

/// <summary>
/// Which minions' card text changes a fight, from a table generated from hand-labelled research
/// (<c>CombatRelevance.g.cs</c>, see tools/effects/gen_combat_relevance.py), with a conservative fallback for cards a
/// newer patch adds. "Not simulated" = combat-relevant and no registered effect.
/// </summary>
public static partial class CombatRelevance
{
    private static readonly Dictionary<string, (CardRelevance Row, bool Golden)> ById = BuildIndex();

    // Card mechanics (or entity tags) that suggest a combat effect when the text cannot be read (non-English card data).
    private static readonly string[] EffectMechanics = ["DEATHRATTLE", "TRIGGER_VISUAL", "AURA", "AVENGE", "OVERKILL", "BACON_RALLY"];

    /// <summary>Effect markers an enchantment can put on a minion (Dark Gifts, Tamsin...); see <see cref="HasGrantedEffect"/>.</summary>
    private static readonly string[] GrantableMechanics = ["DEATHRATTLE", "AVENGE", "BACON_RALLY"];

    /// <summary>Patch the table was generated for.</summary>
    public static string Patch => CombatRelevanceTable.Patch;

    public static IReadOnlyList<CardRelevance> Table => CombatRelevanceTable.Entries;

    /// <summary>The table row for a normal or golden card id, or null for cards it does not know.</summary>
    public static CardRelevance? Find(string? cardId) =>
        cardId is { Length: > 0 } && ById.TryGetValue(cardId, out var hit) ? hit.Row : null;

    /// <summary>Maps a golden card id to its normal id (table first, then card data); other ids map to themselves.</summary>
    public static (string CardId, bool Golden) Normalize(string cardId, CardDb? cards = null)
    {
        if (cardId.Length > 0 && ById.TryGetValue(cardId, out var hit))
        {
            return (hit.Row.CardId, hit.Golden);
        }

        if (cards?.Get(cardId) is { BattlegroundsNormalDbfId: > 0 } golden && cards.Get(golden.BattlegroundsNormalDbfId) is { } normal)
        {
            return (normal.Id, true);
        }

        return (cardId, false);
    }

    /// <summary>
    /// True when the card's text changes a fight after capture. Unknown cards fall back to the text heuristic for
    /// English card data, else (other locales, no text) to the card's mechanics, which over-reports on purpose.
    /// </summary>
    /// <param name="cardId">Normal or golden card id.</param>
    /// <param name="card">Card data, used only when the table does not know the card (null when unknown too).</param>
    /// <param name="locale">Locale of <paramref name="card"/>'s text, e.g. "enUS".</param>
    public static bool IsCombatRelevant(string cardId, Card? card, string locale)
    {
        if (Find(cardId) is { } row)
        {
            return row.Relevance == Relevance.Relevant;
        }

        if (card is null)
        {
            return false;
        }

        if (locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) && card.Text.Length > 0)
        {
            return TextSuggestsCombatEffect(card.Text);
        }

        return MechanicsSuggestCombatEffect(card.Mechanics);
    }

    public static bool MechanicsSuggestCombatEffect(IEnumerable<string> mechanics) => mechanics.Any(m => EffectMechanics.Contains(m));

    /// <summary>Entity tags that suggest an effect, for minions whose card is unknown.</summary>
    public static bool TagsSuggestCombatEffect(Func<string, bool> hasTag) => EffectMechanics.Any(hasTag);

    /// <summary>
    /// The minion carries an effect its card does not print (a deathrattle, Avenge or Rally granted by an enchantment):
    /// the entity has the marker tag but the card's mechanics do not.
    /// </summary>
    public static bool HasGrantedEffect(Func<string, bool> hasTag, IReadOnlyList<string> cardMechanics) =>
        GrantableMechanics.Any(m => hasTag(m) && !cardMechanics.Contains(m));

    /// <summary>
    /// "Not simulated": the card is combat-relevant and has no registered effect for this side, or an enchantment granted
    /// it an effect that no registered enchantment effect covers.
    /// </summary>
    public static bool IsUnsimulated(string cardId, bool golden, bool combatRelevant, bool grantedEffect,
                                     IReadOnlyList<string> enchantments, bool isOpponent, EffectRegistry effects) =>
        (combatRelevant && !effects.Implements(cardId, golden, isOpponent))
        || (grantedEffect && !enchantments.Any(e => effects.Implements(e, golden: false, isOpponent)));

    /// <summary>
    /// Text heuristic for "needs combat simulation" (English card text). Mirrors needs_sim() in
    /// tools/effects/gen_combat_relevance.py; on the hand-labelled pool it finds 64 of the 65 combat-relevant cards with 5
    /// false alarms. Tuned on that pool, so expect it to be weaker on a new patch.
    /// </summary>
    public static bool TextSuggestsCombatEffect(string? text)
    {
        var t = NormalizeText(text);
        if (t.Length == 0)
        {
            return false;
        }

        foreach (var clauseRegex in ClauseRegexes)
        {
            var clause = clauseRegex.Match(t);
            if (clause.Success)
            {
                var body = clause.Groups[1].Value.Trim();
                if (CombatVerbRegex().IsMatch(body) && !NotCombatRegex().IsMatch(body))
                {
                    return true;
                }
            }
        }

        return OnAttackRegex().IsMatch(t)
            || CleaveRegex().IsMatch(t)
            || OnSummonRegex().IsMatch(t)
            || DynamicRegex().IsMatch(t)
            || PersistentRegex().IsMatch(t)
            || CombatTriggerRegex().IsMatch(t);
    }

    internal static string NormalizeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var at = text.IndexOf('@');
        var t = at >= 0 ? text[..at] : text;
        t = TagRegex().Replace(t, string.Empty).Replace("[x]", string.Empty).Replace('_', ' ').Replace('\n', ' ');
        return SpaceRegex().Replace(t, " ").Trim();
    }

    private static readonly Regex[] ClauseRegexes = [DeathrattleClauseRegex(), RallyClauseRegex(), AvengeClauseRegex()];

    private static Dictionary<string, (CardRelevance, bool)> BuildIndex()
    {
        var index = new Dictionary<string, (CardRelevance, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in CombatRelevanceTable.Entries)
        {
            index[row.CardId] = (row, false);
            if (row.GoldenCardId.Length > 0)
            {
                index[row.GoldenCardId] = (row, true);
            }
        }

        return index;
    }

    private const string ClauseTail = @"(?: \(\d+\))?: (.*?)(?=(?:Deathrattle|Rally|Avenge|Battlecry)[^:]*:|$)";

    [GeneratedRegex("Deathrattle" + ClauseTail)]
    private static partial Regex DeathrattleClauseRegex();

    [GeneratedRegex("Rally" + ClauseTail)]
    private static partial Regex RallyClauseRegex();

    [GeneratedRegex("Avenge" + ClauseTail)]
    private static partial Regex AvengeClauseRegex();

    [GeneratedRegex(@"(Summon|summon|Give|give|Gain|gain|Deal|deal|Destroy|destroy|Remove|remove|Cast|cast|plays? \d* ?(a )?Blood Gem|Reborn|Divine Shield|Attack|have \+|Blood \|4)")]
    private static partial Regex CombatVerbRegex();

    [GeneratedRegex(@"^(Get|get|Add|You and your teammate|Your teammate|Reduce|Your Tavern spells|Gain \d+ free|Gain .*Refresh|Give minions in the Tavern|After the Tavern)")]
    private static partial Regex NotCombatRegex();

    [GeneratedRegex(@"(friendly|another|other)[^.]*\battacks\b|After this attacks|Whenever this attacks|is attacked")]
    private static partial Regex OnAttackRegex();

    [GeneratedRegex(@"Also damages|excess damage")]
    private static partial Regex CleaveRegex();

    [GeneratedRegex(@"(Whenever|After) you summon|summon a (Beast|minion) in combat")]
    private static partial Regex OnSummonRegex();

    [GeneratedRegex(@"is Reborn|deals damage, gain|that died this game|Deathrattle you've triggered|Deathrattles trigger|while this has Divine Shield|Once this reaches")]
    private static partial Regex DynamicRegex();

    [GeneratedRegex(@"rest of this combat|When you have space|attack immediately")]
    private static partial Regex PersistentRegex();

    [GeneratedRegex(@"(Whenever|After) this takes damage|(Whenever|After) this kills|attacks and kills|(Whenever|After) a friendly [A-Za-z ]*dies|loses Divine Shield")]
    private static partial Regex CombatTriggerRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
