using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Arena;

/// <summary>One of the cards (or heroes) offered at a draft pick.</summary>
/// <param name="Score">ArenaSmith score of a card; null for a hero or a card without a score.</param>
/// <param name="Tier">Tier letter of the score, or of the class win rate for a hero.</param>
/// <param name="IsBest">The highest score (or class win rate) of the offer.</param>
/// <param name="PlayerClass">Class key of a hero option.</param>
/// <param name="WinRate">Class win rate of a hero option, 0..1.</param>
public sealed record ArenaPickOption(string CardId, string Name, double? Score, string? Tier, bool IsBest, string? PlayerClass = null, double? WinRate = null);

/// <summary>One distinct card of the drafted deck.</summary>
public sealed record ArenaDeckCard(string CardId, string Name, int Cost, int Count, double? Score, string? Tier);

/// <summary>A class with its community results, for the hero pick.</summary>
/// <param name="Offered">The class is one of the three heroes on screen.</param>
public sealed record ArenaClassChoice(string PlayerClass, double WinRate, double? SevenPlus, bool Offered);

/// <summary>Score summary of a deck (the draft so far, or a finished run's deck).</summary>
/// <param name="Average">Mean ArenaSmith score over scored cards, copies counted; null when none is scored.</param>
/// <param name="Curve">Card counts per mana cost 0, 1, ... 6, 7+ (8 entries).</param>
/// <param name="Scored">Cards (copies counted) that have a score.</param>
public sealed record ArenaDeckScore(double? Average, string? Tier, IReadOnlyList<int> Curve, int Cards, int Scored)
{
    public static ArenaDeckScore Empty { get; } = new(null, null, new int[ArenaDraft.CurveBuckets], 0, 0);
}

/// <summary>Everything the Arena draft panels show.</summary>
/// <param name="PlayerClass">Class of the run, or null before the hero pick.</param>
/// <param name="Picks">Cards in the deck so far (30 at the end).</param>
/// <param name="Options">The offer on screen (empty when it is not known).</param>
/// <param name="HeroPick">The offer is the three heroes, not cards.</param>
/// <param name="Classes">All classes with results, best first; filled for the hero pick.</param>
public sealed record ArenaDraftView(
    string? PlayerClass,
    string? HeroCardId,
    int Picks,
    IReadOnlyList<ArenaPickOption> Options,
    bool HeroPick,
    IReadOnlyList<ArenaDeckCard> Deck,
    ArenaDeckScore Score,
    IReadOnlyList<ArenaClassChoice> Classes,
    bool HasData);

/// <summary>Builds <see cref="ArenaDraftView"/> from the log state, the offer read from memory and the community numbers.</summary>
public static class ArenaDraft
{
    public const int DeckSize = 30;

    public const int CurveBuckets = 8;

    public static ArenaDraftView Build(ArenaDraftState state, IReadOnlyList<string>? offer, ArenaMeta? meta, CardDb cards)
    {
        meta ??= ArenaMeta.Empty;
        var playerClass = ClassOfHero(state.HeroCardId, cards);
        var options = offer is { Count: > 0 } ? Options(offer, meta, cards) : [];
        var heroPick = options.Count > 0 && options.All(o => o.PlayerClass is not null);
        var offeredClasses = heroPick ? options.Select(o => o.PlayerClass!).ToHashSet(StringComparer.Ordinal) : [];
        var classes = playerClass is null || heroPick
            ? meta.Classes.Select(c => new ArenaClassChoice(c.PlayerClass, c.WinRate, c.SevenPlus, offeredClasses.Contains(c.PlayerClass))).ToList()
            : [];
        var deck = Deck(state.Cards, meta, cards);
        return new ArenaDraftView(
            playerClass,
            state.HeroCardId,
            state.Cards.Count,
            options,
            heroPick,
            deck,
            ScoreOf(state.Cards, meta, cards),
            classes,
            meta.HasCards || meta.Classes.Count > 0);
    }

    /// <summary>Scores a deck given as card ids (a card listed once per copy).</summary>
    public static ArenaDeckScore ScoreOf(IEnumerable<string> cardIds, ArenaMeta? meta, CardDb cards)
    {
        meta ??= ArenaMeta.Empty;
        var curve = new int[CurveBuckets];
        double sum = 0;
        int count = 0, scored = 0;
        foreach (var id in cardIds)
        {
            count++;
            curve[Math.Clamp(cards.Get(id)?.Cost ?? 0, 0, CurveBuckets - 1)]++;
            if (meta.Score(id) is { } score)
            {
                sum += score;
                scored++;
            }
        }

        double? average = scored > 0 ? sum / scored : null;
        return new ArenaDeckScore(average, ArenaMeta.TierOf(average), curve, count, scored);
    }

    /// <summary>Scores a deck code's card list (dbf id -> copies), as History saves it.</summary>
    public static ArenaDeckScore ScoreOf(DeckDefinition deck, ArenaMeta? meta, CardDb cards) =>
        ScoreOf(deck.Cards.SelectMany(c => Enumerable.Repeat(cards.Get(c.Key)?.Id ?? meta?.Card(c.Key)?.CardId ?? string.Empty, c.Value)).Where(id => id.Length > 0), meta, cards);

    /// <summary>Class key of a hero card (HERO_08 -> MAGE), or null.</summary>
    public static string? ClassOfHero(string? heroCardId, CardDb cards) =>
        heroCardId is null ? null : ArenaMeta.ClassKey(cards.Get(heroCardId)?.CardClass);

    private static List<ArenaPickOption> Options(IReadOnlyList<string> offer, ArenaMeta meta, CardDb cards)
    {
        var options = new List<ArenaPickOption>(offer.Count);
        foreach (var id in offer)
        {
            var card = cards.Get(id);
            var name = card?.Name ?? id;
            if (ArenaDraftTracker.IsHeroId(id) || card?.Type == "HERO" && ArenaMeta.ClassKey(card.CardClass) is not null && !card.Collectible)
            {
                var key = ArenaMeta.ClassKey(card?.CardClass);
                var stat = meta.Class(key);
                options.Add(new ArenaPickOption(id, name, null, TierOfWinRate(stat?.WinRate), false, key ?? string.Empty, stat?.WinRate));
            }
            else
            {
                var score = meta.Score(id);
                options.Add(new ArenaPickOption(id, name, score, ArenaMeta.TierOf(score), false));
            }
        }

        // The best is marked only when it can be compared with at least one other option.
        var values = options.Select(o => o.PlayerClass is not null ? o.WinRate : o.Score).ToList();
        if (values.Count(v => v is not null) >= 2)
        {
            var best = values.Max(v => v ?? double.MinValue);
            for (var i = 0; i < options.Count; i++)
            {
                if (values[i] is { } v && Math.Abs(v - best) < 1e-9)
                {
                    options[i] = options[i] with { IsBest = true };
                }
            }
        }

        return options;
    }

    private static List<ArenaDeckCard> Deck(IReadOnlyList<string> cardIds, ArenaMeta meta, CardDb cards) =>
        cardIds.GroupBy(id => id, StringComparer.Ordinal)
            .Select(g =>
            {
                var card = cards.Get(g.Key);
                var score = meta.Score(g.Key);
                return new ArenaDeckCard(g.Key, card?.Name ?? g.Key, card?.Cost ?? 0, g.Count(), score, ArenaMeta.TierOf(score));
            })
            .OrderBy(c => c.Cost)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();

    /// <summary>
    /// Tier of a class win rate: Arena class win rates sit between about 35% and 56%, so the letters are spread over
    /// that range (S 54%+, A 50%+, B 46%+, C 42%+, D 38%+, else E).
    /// </summary>
    public static string? TierOfWinRate(double? winRate) => winRate switch
    {
        null => null,
        >= 0.54 => "S",
        >= 0.50 => "A",
        >= 0.46 => "B",
        >= 0.42 => "C",
        >= 0.38 => "D",
        _ => "E",
    };
}
