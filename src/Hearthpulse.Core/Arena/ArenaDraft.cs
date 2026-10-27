using Hearthpulse.Core.Cards;

namespace Hearthpulse.Core.Arena;

/// <summary>One of the cards (or heroes) offered at a draft pick.</summary>
/// <param name="Score">ArenaSmith score of a card (of the whole package when it has one); null for a hero or a card without a score.</param>
/// <param name="Tier">Tier letter of the score, or of the class win rate for a hero.</param>
/// <param name="IsBest">The highest score (or class win rate) of the offer.</param>
/// <param name="PlayerClass">Class key of a hero option.</param>
/// <param name="WinRate">Class win rate of a hero option, 0..1.</param>
/// <param name="Kind">Card, hero or hero power.</param>
/// <param name="Cost">Mana cost of a card option.</param>
/// <param name="CardType">MINION, SPELL, WEAPON, LOCATION, HERO... of a card option.</param>
/// <param name="SameCost">Cards of the deck so far in the curve bucket of this card (0..6, 7+).</param>
/// <param name="SameType">Cards of the deck so far of this card's type.</param>
/// <param name="Package">The cards that come with this one (a legendary's package).</param>
/// <param name="ClassRank">Place of a hero's class by win rate, 1 = best, among <see cref="ArenaDraftView.Classes"/>.</param>
public sealed record ArenaPickOption(
    string CardId,
    string Name,
    double? Score,
    string? Tier,
    bool IsBest,
    string? PlayerClass = null,
    double? WinRate = null,
    ArenaChoiceKind Kind = ArenaChoiceKind.Card,
    int? Cost = null,
    string? CardType = null,
    int SameCost = 0,
    int SameType = 0,
    IReadOnlyList<string>? Package = null,
    int? ClassRank = null);

/// <summary>One distinct card of the drafted deck.</summary>
public sealed record ArenaDeckCard(string CardId, string Name, int Cost, int Count, double? Score, string? Tier);

/// <summary>A class with its community results, for the hero pick.</summary>
/// <param name="Offered">The class is one of the three heroes on screen.</param>
public sealed record ArenaClassChoice(string PlayerClass, double WinRate, double? SevenPlus, bool Offered);

/// <summary>Community results of the run's class once the hero is picked.</summary>
/// <param name="Rank">Place by win rate, 1 = best, of <paramref name="Of"/> classes.</param>
public sealed record ArenaRunClass(string PlayerClass, double WinRate, double? SevenPlus, int Rank, int Of);

/// <summary>Score summary of a deck (the draft so far, or a finished run's deck).</summary>
/// <param name="Average">Mean ArenaSmith score over scored cards, copies counted; null when none is scored.</param>
/// <param name="Curve">Card counts per mana cost 0, 1, ... 6, 7+ (8 entries).</param>
/// <param name="Scored">Cards (copies counted) that have a score.</param>
public sealed record ArenaDeckScore(double? Average, string? Tier, IReadOnlyList<int> Curve, int Cards, int Scored)
{
    public static ArenaDeckScore Empty { get; } = new(null, null, new int[ArenaDraft.CurveBuckets], 0, 0);
}

/// <summary>Card types of a deck, copies counted; <see cref="Other"/> holds heroes and anything new.</summary>
public sealed record ArenaDeckTypes(int Minions, int Spells, int Weapons, int Locations, int Other)
{
    public static ArenaDeckTypes Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Cards of the type <paramref name="cardType"/> (MINION, SPELL...).</summary>
    public int Of(string? cardType) => cardType switch
    {
        "MINION" => Minions,
        "SPELL" => Spells,
        "WEAPON" => Weapons,
        "LOCATION" => Locations,
        _ => Other,
    };
}

/// <summary>What the offer on screen is.</summary>
public enum ArenaDraftPhase
{
    /// <summary>No offer is known (between picks, or the memory read gave nothing).</summary>
    None,
    Hero,
    HeroPower,
    Cards,
}

/// <summary>Everything the Arena draft panels show.</summary>
/// <param name="PlayerClass">Class of the run, or null before the hero pick.</param>
/// <param name="Picks">Cards in the deck so far (30 at the end).</param>
/// <param name="Options">The offer on screen (empty when it is not known).</param>
/// <param name="HeroPick">The offer is the three heroes, not cards.</param>
/// <param name="Classes">All classes with results, best first; filled for the hero pick.</param>
/// <param name="Phase">Hero, hero power or card pick.</param>
/// <param name="Underground">The run is an Underground Arena run; null when the client did not say.</param>
/// <param name="ScoresFromOtherMode">The scores come from the other Arena mode than the run's (the server has one slice).</param>
/// <param name="Types">Card types of the deck so far.</param>
/// <param name="RunClass">Results of the run's class after the hero pick; null before it or without class numbers.</param>
public sealed record ArenaDraftView(
    string? PlayerClass,
    string? HeroCardId,
    int Picks,
    IReadOnlyList<ArenaPickOption> Options,
    bool HeroPick,
    IReadOnlyList<ArenaDeckCard> Deck,
    ArenaDeckScore Score,
    IReadOnlyList<ArenaClassChoice> Classes,
    bool HasData,
    ArenaDraftPhase Phase = ArenaDraftPhase.None,
    bool? Underground = null,
    bool ScoresFromOtherMode = false,
    ArenaDeckTypes? Types = null,
    ArenaRunClass? RunClass = null);

/// <summary>Builds <see cref="ArenaDraftView"/> from the log state, the offer read from memory and the community numbers.</summary>
public static class ArenaDraft
{
    public const int DeckSize = 30;

    public const int CurveBuckets = 8;

    /// <summary>The view for an offer given as card ids only (render scenes, tests).</summary>
    public static ArenaDraftView Build(ArenaDraftState state, IReadOnlyList<string>? offer, ArenaMeta? meta, CardDb cards, bool? underground = null) =>
        Build(state, offer is null ? null : new ArenaDraftOffer(offer, underground), meta, cards, underground);

    /// <param name="underground">The run's mode when the offer does not say (the last one seen on this draft screen).</param>
    public static ArenaDraftView Build(ArenaDraftState state, ArenaDraftOffer? offer, ArenaMeta? meta, CardDb cards, bool? underground = null)
    {
        meta ??= ArenaMeta.Empty;
        var playerClass = ClassOfHero(state.HeroCardId, cards);
        // A hero power logged as a choice is no card of the deck.
        var deckIds = state.Cards.Where(id => ArenaDraftTracker.KindOf(id, cards) == ArenaChoiceKind.Card).ToList();
        var score = ScoreOf(deckIds, meta, cards);
        var types = TypesOf(deckIds, cards);
        var options = offer is { CardIds.Count: > 0 } ? Options(offer, meta, cards, score, types) : [];
        var phase = options.Count == 0 ? ArenaDraftPhase.None
            : options.All(o => o.Kind == ArenaChoiceKind.Hero) ? ArenaDraftPhase.Hero
            : options.All(o => o.Kind == ArenaChoiceKind.HeroPower) ? ArenaDraftPhase.HeroPower
            : ArenaDraftPhase.Cards;
        var heroPick = phase == ArenaDraftPhase.Hero;
        var offeredClasses = heroPick ? options.Select(o => o.PlayerClass!).ToHashSet(StringComparer.Ordinal) : [];
        var classes = playerClass is null || heroPick
            ? meta.Classes.Select(c => new ArenaClassChoice(c.PlayerClass, c.WinRate, c.SevenPlus, offeredClasses.Contains(c.PlayerClass))).ToList()
            : [];
        var runStat = heroPick ? null : meta.Class(playerClass);
        var runClass = runStat is null ? null
            : new ArenaRunClass(runStat.PlayerClass, runStat.WinRate, runStat.SevenPlus, IndexOf(meta.Classes, runStat) + 1, meta.Classes.Count);
        var mode = offer?.Underground ?? underground;
        return new ArenaDraftView(
            playerClass,
            state.HeroCardId,
            deckIds.Count,
            options,
            heroPick,
            Deck(deckIds, meta, cards),
            score,
            classes,
            meta.HasCards || meta.Classes.Count > 0,
            phase,
            mode,
            meta.HasCards && mode is { } m && meta.Underground is { } slice && m != slice,
            types,
            runClass);
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
            curve[Bucket(cards.Get(id)?.Cost ?? 0)]++;
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

    /// <summary>Card types of a deck given as card ids (a card listed once per copy).</summary>
    public static ArenaDeckTypes TypesOf(IEnumerable<string> cardIds, CardDb cards)
    {
        int minions = 0, spells = 0, weapons = 0, locations = 0, other = 0;
        foreach (var id in cardIds)
        {
            switch (cards.Get(id)?.Type)
            {
                case "MINION": minions++; break;
                case "SPELL": spells++; break;
                case "WEAPON": weapons++; break;
                case "LOCATION": locations++; break;
                default: other++; break;
            }
        }

        return new ArenaDeckTypes(minions, spells, weapons, locations, other);
    }

    /// <summary>Curve bucket of a mana cost: 0..6, and 7 for 7 or more.</summary>
    public static int Bucket(int cost) => Math.Clamp(cost, 0, CurveBuckets - 1);

    /// <summary>Class key of a hero card (HERO_08 -> MAGE), or null.</summary>
    public static string? ClassOfHero(string? heroCardId, CardDb cards) =>
        heroCardId is null ? null : ArenaMeta.ClassKey(cards.Get(heroCardId)?.CardClass);

    private static List<ArenaPickOption> Options(ArenaDraftOffer offer, ArenaMeta meta, CardDb cards, ArenaDeckScore deck, ArenaDeckTypes types)
    {
        var options = new List<ArenaPickOption>(offer.CardIds.Count);
        for (var i = 0; i < offer.CardIds.Count; i++)
        {
            var id = offer.CardIds[i];
            var card = cards.Get(id);
            var name = card?.Name ?? id;
            switch (ArenaDraftTracker.KindOf(id, cards))
            {
                case ArenaChoiceKind.Hero:
                {
                    var key = ArenaMeta.ClassKey(card?.CardClass);
                    var stat = meta.Class(key);
                    var rank = stat is null ? (int?)null : IndexOf(meta.Classes, stat) + 1;
                    options.Add(new ArenaPickOption(id, name, null, TierOfWinRate(stat?.WinRate), false, key ?? string.Empty, stat?.WinRate, ArenaChoiceKind.Hero, ClassRank: rank));
                    break;
                }

                case ArenaChoiceKind.HeroPower:
                    options.Add(new ArenaPickOption(id, name, null, null, false, Kind: ArenaChoiceKind.HeroPower));
                    break;
                default:
                {
                    // A legendary that brings a package is taken as a whole: the score is the package's average.
                    var package = offer.PackageOf(i);
                    var score = package.Count == 0 ? meta.Score(id) : ScoreOf(package.Prepend(id), meta, cards).Average;
                    var cost = card?.Cost;
                    options.Add(new ArenaPickOption(id, name, score, ArenaMeta.TierOf(score), false,
                        Kind: ArenaChoiceKind.Card,
                        Cost: cost,
                        CardType: card?.Type,
                        SameCost: cost is { } c ? deck.Curve[Bucket(c)] : 0,
                        SameType: card is null ? 0 : types.Of(card.Type),
                        Package: package));
                    break;
                }
            }
        }

        // The best is marked only when it can be compared with at least one other option.
        var values = options.Select(o => o.Kind == ArenaChoiceKind.Hero ? o.WinRate : o.Score).ToList();
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

    private static int IndexOf(IReadOnlyList<ArenaClassStat> classes, ArenaClassStat stat)
    {
        for (var i = 0; i < classes.Count; i++)
        {
            if (ReferenceEquals(classes[i], stat))
            {
                return i;
            }
        }

        return -1;
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
