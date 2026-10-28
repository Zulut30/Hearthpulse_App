using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Which Arena card number stands in for the opening-hand win rate of a deck's mulligan stats.</summary>
public enum ArenaCardBasis
{
    /// <summary>Win rate of the games the card was drawn in (HSReplay's "winrate when drawn").</summary>
    Drawn,

    /// <summary>Win rate of the games the card was played in: only when the data has no drawn win rates.</summary>
    Played,

    /// <summary>Win rate of the decks holding the card: an older cache without the drawn and played win rates.</summary>
    Deck,
}

/// <summary>
/// The mulligan fallback for Arena: every Arena deck is unique, so there are no mulligan stats per deck, but every card
/// has its Arena results (<see cref="ArenaMeta"/>). The card's win rate when drawn stands in for the opening-hand win
/// rate, against the average of the class's and neutral cards; the guide's maths (<see cref="MulliganGuide.Compute"/>)
/// then calls keep or replace against the average card left in the deck. The data has no split by the coin or the
/// opponent's class.
/// </summary>
public static class MulliganArena
{
    /// <summary>
    /// No keep rates in Arena: a neutral half stands in, so the duplicate rule (a cheap second copy kept when most
    /// players keep the card) keeps a cheap second copy. Not shown.
    /// </summary>
    private const double UnknownKeepRate = 0.5;

    public static bool IsArena(GameState game) => game.GameType.Contains("ARENA", StringComparison.Ordinal);

    /// <summary>Underground Arena (true), normal Arena (false), or null outside Arena.</summary>
    public static bool? IsUnderground(GameState game) => IsArena(game) ? game.GameType.Contains("UNDERGROUND", StringComparison.Ordinal) : null;

    /// <summary>The drawn win rate when the data has it, else the played one, else the deck win rate.</summary>
    public static ArenaCardBasis BasisOf(ArenaMeta meta) =>
        meta.Cards.Any(c => c.DrawnWinRate > 0) ? ArenaCardBasis.Drawn
        : meta.Cards.Any(c => c.PlayedWinRate > 0) ? ArenaCardBasis.Played
        : ArenaCardBasis.Deck;

    /// <summary>
    /// The Arena card numbers in the guide's shape: the cards of the player's class and neutral cards with at least
    /// <see cref="MulliganGuide.MinSample"/> games, the chosen win rate as their opening-hand win rate. Null when none has one.
    /// </summary>
    public static GuideArchetype? Guide(ArenaMeta meta, CardDb cards, string playerClass, ArenaCardBasis basis)
    {
        var rows = new List<GuideCard>();
        foreach (var score in meta.Cards)
        {
            var card = cards.Get(score.CardId) ?? (score.DbfId > 0 ? cards.Get(score.DbfId) : null);
            if (card is null || score.Games < MulliganGuide.MinSample || RateOf(score, basis) is not { } rate || rate <= 0
                || !(card.CardClass == "NEUTRAL" || string.Equals(card.CardClass, playerClass, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            rows.Add(new GuideCard(card.Id, card.DbfId, card.Cost, score.Games, UnknownKeepRate, score.Games, rate, null));
        }

        return rows.Count == 0 ? null : new GuideArchetype(0, string.Empty, playerClass, rows, [], SourceOf(basis));
    }

    /// <summary>
    /// The mulligan panel from the Arena card numbers, or null when they hold nothing for the hand's class. Each card also
    /// carries its draft score and tier letter (as on the draft screen); keep rates stay empty.
    /// </summary>
    internal static MulliganView? Build(ArenaMeta meta, CardDb cards, string playerClass, IReadOnlyList<OfferedCard> offered, IReadOnlyDictionary<string, int>? deck,
        bool? underground)
    {
        var basis = BasisOf(meta);
        if (Guide(meta, cards, playerClass, basis) is not { } guide
            || MulliganGuide.Compute(guide, offered, deck, null, null, string.Empty) is not { } view)
        {
            return null;
        }

        // The server publishes one Arena mode's games; a run of the other mode says so.
        var source = underground is { } run && meta.Underground is { } slice && run != slice
            ? guide.Source + " · " + Loc.T(slice ? "Arena.Slice.Underground" : "Arena.Slice.Normal")
            : guide.Source;
        return view with
        {
            Source = source,
            ArenaBasis = basis,
            Cards = view.Cards.Select(c =>
            {
                var score = meta.Card(c.CardId) ?? (cards.Get(c.CardId)?.DbfId is int dbf and > 0 ? meta.Card(dbf) : null);
                return c with { KeepRate = null, PickScore = score?.Score, PickTier = score?.Tier };
            }).ToList(),
        };
    }

    private static double? RateOf(ArenaCardScore score, ArenaCardBasis basis) => basis switch
    {
        ArenaCardBasis.Drawn => score.DrawnWinRate,
        ArenaCardBasis.Played => score.PlayedWinRate,
        _ => score.WinRate,
    };

    /// <summary>"Arena · card win rate when drawn": the summary line's source.</summary>
    public static string SourceOf(ArenaCardBasis basis) => Loc.T(basis switch
    {
        ArenaCardBasis.Drawn => "Mulligan.Arena.Source.Drawn",
        ArenaCardBasis.Played => "Mulligan.Arena.Source.Played",
        _ => "Mulligan.Arena.Source.Deck",
    });
}
