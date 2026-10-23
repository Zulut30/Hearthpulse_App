using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Community mulligan numbers for one card in the opening hand.</summary>
/// <param name="EntityId">The offered card's entity, so the overlay can toggle it.</param>
/// <param name="KeepRate">Share of players who kept the card when offered it.</param>
/// <param name="WinRate">Win rate of games where the card was in hand after the mulligan.</param>
/// <param name="Delta">WinRate minus the deck's baseline: above 0 favours keeping.</param>
/// <param name="Sample">Games the card was offered in.</param>
public sealed record MulliganCardStat(int EntityId, string CardId, string Name, double? KeepRate, double? WinRate, double? Delta, int Sample)
{
    /// <summary>1 for the leftmost copy of a card in the hand, 2 for the next.</summary>
    public int Copy { get; init; } = 1;

    public int Cost { get; init; }

    /// <summary>The turn the deck usually plays the card on (HSReplay's average), else its cost.</summary>
    public double ExpectedTurn { get; init; }

    public MulliganCall Call { get; init; }
}

/// <summary>The mulligan panel: whose numbers they are and one row per offered card.</summary>
/// <param name="BaseWinRate">The deck's win rate with an average opening hand, moved by the opponent's class or the coin when known.</param>
public sealed record MulliganView(string Context, double BaseWinRate, IReadOnlyList<MulliganCardStat> Cards, string Source)
{
    /// <summary>What replacing a card is worth on average (win rate delta of a card drawn from the rest of the deck).</summary>
    public double ReplaceDelta { get; init; }

    /// <summary>The class <see cref="BaseWinRate"/> was adjusted for; null when it was not.</summary>
    public string? OpponentClass { get; init; }

    public double? ClassShift { get; init; }

    /// <summary>Going first (true) or on the coin (false), when the baseline was adjusted for it.</summary>
    public bool? GoingFirst { get; init; }

    /// <summary>Entity ids of the cards the guide would keep.</summary>
    public IReadOnlyList<int> Recommended { get; init; } = [];

    /// <summary>False while no server endpoint publishes card pairs: synergies cannot be shown.</summary>
    public bool HasPairData { get; init; }

    public IReadOnlyList<MulliganSynergy> Synergies { get; init; } = [];
}

/// <summary>
/// Builds the mulligan panel while the local player chooses their opening hand (docs/mulligan-v2.md). Numbers come
/// from the Hearthpulse API (HSReplay Legend, Standard) for the archetype the deck matches; the meta's archetypes
/// (<see cref="ConstructedMeta"/>, matched by core cards) are the fallback, pooled over the class when none matches.
/// </summary>
public static class Mulligan
{
    /// <summary>Fewer offers than this and the numbers are too noisy to show.</summary>
    public const int MinSample = MulliganGuide.MinSample;

    public static bool InProgress(GameState game) =>
        game.LocalPlayerEntity?.GetString(GameTag.MulliganState) is "INPUT" or "DEALING";

    public static MulliganView? Build(GameState game, CardDb cards, DeckDefinition? deck, ConstructedMeta? meta, string playerClass, GuideArchetype? guide = null)
    {
        if ((guide is null && meta is null) || !InProgress(game))
        {
            return null;
        }

        // The coin (on the coin it sits in hand already) has cosmetic variants such as JAIL_COIN3; they share its name.
        var coin = cards.Get("GAME_005")?.Name;
        var offered = game.Controlled(game.LocalPlayerId, "HAND")
            .Where(e => e.CardId.Length > 0 && e.CardId != "GAME_005" && (coin is null || cards.Get(e.CardId)?.Name != coin))
            .OrderBy(e => e.ZonePosition)
            .Select(e => new OfferedCard(e.Id, e.CardId, cards.NameOf(e.CardId), cards.Get(e.CardId)?.Cost ?? e.GetInt(GameTag.Cost)))
            .ToList();
        if (offered.Count == 0)
        {
            return null;
        }

        var deckIds = deck?.Cards
            .Select(c => (Id: cards.Get(c.Key)?.Id, Copies: c.Value))
            .Where(c => c.Id is not null)
            .GroupBy(c => c.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Copies), StringComparer.Ordinal);
        var opponentClass = cards.Get(game.OpponentHero?.CardId)?.CardClass;
        bool? goingFirst = game.LocalPlayerEntity?.GetString(GameTag.FirstPlayer) is { Length: > 0 } first ? first == "1" : null;

        if (guide is not null)
        {
            return MulliganGuide.Compute(guide, offered, deckIds, opponentClass, goingFirst, guide.Name);
        }

        if (!FormatMatches(game.FormatType, meta!.Format) || FromMeta(meta, cards, playerClass, deckIds) is not { } fallback)
        {
            return null;
        }

        return MulliganGuide.Compute(fallback, offered, deckIds, opponentClass, goingFirst, fallback.Name);
    }

    /// <summary>
    /// The meta's numbers in the guide's shape: the archetype the deck matches, or every archetype of the class pooled
    /// (weighted by how often each card was offered) when the deck is unknown or matches none.
    /// </summary>
    internal static GuideArchetype? FromMeta(ConstructedMeta meta, CardDb cards, string playerClass, IReadOnlyDictionary<string, int>? deck)
    {
        // An archetype without mulligan numbers (no HSReplay counterpart) leaves the class pool to speak for it.
        var archetype = deck is { Count: > 0 } ? meta.Match(playerClass, deck.Keys.ToList()) : null;
        if (archetype is { Mulligan.Count: > 0 })
        {
            return new GuideArchetype(archetype.Id, archetype.Name, archetype.Class, archetype.Mulligan.Select(m => Card(m, cards)).ToList(), [], meta.Source)
            {
                BaselineWinRate = archetype.MulliganBaseline ?? archetype.WinRate,
                WinRate = archetype.WinRate,
                FirstWinRate = archetype.FirstGames >= MulliganGuide.MinClassGames ? archetype.FirstWinRate : null,
                CoinWinRate = archetype.CoinGames >= MulliganGuide.MinClassGames ? archetype.CoinWinRate : null,
            };
        }

        // Several archetypes can share one HSReplay archetype's numbers: count those once.
        var archetypes = meta.ForClass(playerClass).Where(a => a.Mulligan.Count > 0)
            .DistinctBy(a => a.MulliganArchetypeId is { } id ? $"hsreplay-{id}" : $"own-{a.Id}")
            .ToList();
        if (archetypes.Count == 0)
        {
            return null;
        }

        var pooled = archetypes.SelectMany(a => a.Mulligan)
            .GroupBy(m => m.CardId, StringComparer.Ordinal)
            .Select(g =>
            {
                var drawn = g.Sum(m => m.Drawn);
                var inHand = g.Sum(m => m.InHand);
                return new MetaMulliganCard(g.Key, drawn, drawn > 0 ? g.Sum(m => m.KeepRate * m.Drawn) / drawn : 0, inHand, inHand > 0 ? g.Sum(m => m.InHandWinRate * m.InHand) / inHand : 0);
            })
            .Select(m => Card(m, cards))
            .ToList();
        var games = archetypes.Sum(a => a.Games);
        return new GuideArchetype(0, $"{Title(playerClass)} (all decks)", playerClass, pooled, [], meta.Source)
        {
            // Mixed sources (HSGuru win rates, HSReplay cards): the cards' own sample gives the baseline.
            BaselineWinRate = games > 0 && archetypes.All(a => a.MulliganBaseline is null) ? archetypes.Sum(a => a.WinRate * a.Games) / games : null,
        };
    }

    private static GuideCard Card(MetaMulliganCard m, CardDb cards) =>
        new(m.CardId, cards.Get(m.CardId)?.DbfId ?? 0, cards.Get(m.CardId)?.Cost ?? 0, m.Drawn, m.KeepRate, m.InHand, m.InHand > 0 ? m.InHandWinRate : null, null);

    private static bool FormatMatches(string formatType, string metaFormat) => formatType switch
    {
        "FT_STANDARD" => metaFormat == "standard",
        "FT_WILD" => metaFormat == "wild",
        _ => false,
    };

    private static string Title(string upper) => upper.Length == 0 ? upper : upper[0] + upper[1..].ToLowerInvariant();
}
