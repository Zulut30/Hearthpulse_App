using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>Community mulligan numbers for one card in the opening hand.</summary>
/// <param name="EntityId">The offered card's entity, so the overlay can toggle it.</param>
/// <param name="KeepRate">Share of players who kept the card when offered it.</param>
/// <param name="WinRate">Win rate of games where the card was in hand after the mulligan.</param>
/// <param name="Delta">WinRate minus the deck's baseline, moved by <see cref="CoinShift"/> and <see cref="OpponentShift"/>.</param>
/// <param name="Sample">Games the card was offered in.</param>
public sealed record MulliganCardStat(int EntityId, string CardId, string Name, double? KeepRate, double? WinRate, double? Delta, int Sample)
{
    /// <summary>1 for the leftmost copy of a card in the hand, 2 for the next.</summary>
    public int Copy { get; init; } = 1;

    public int Cost { get; init; }

    /// <summary>The turn the deck usually plays the card on (HSReplay's average), else its cost.</summary>
    public double ExpectedTurn { get; init; }

    public MulliganCall Call { get; init; }

    /// <summary>
    /// What keeping the card is worth against replacing it, the other cards as recommended, in win rate (0.019 = +1.9
    /// points): usually <see cref="Delta"/> minus the average replacement's, plus a kept synergy, minus what a kept second
    /// copy already gives. The call always follows its sign; the overlay's bar and number show it. Null for a second copy,
    /// which the duplicate heuristic calls.
    /// </summary>
    public double? Edge { get; init; }

    /// <summary>The part of <see cref="Delta"/> that comes from going first / the coin (HSGuru slices); null when not adjusted.</summary>
    public double? CoinShift { get; init; }

    /// <summary>The part of <see cref="Delta"/> that comes from the opponent's class (HSGuru slices); null when not adjusted.</summary>
    public double? OpponentShift { get; init; }

    /// <summary>Win rate when the offered card was kept / replaced, against the deck's (HSGuru, <see cref="Slice"/>).</summary>
    public double? KeptDelta { get; init; }

    public double? ReplacedDelta { get; init; }

    /// <summary>Win rate when the card is drawn at any point, against the deck's (HSGuru slice, else HSReplay).</summary>
    public double? DrawnDelta { get; init; }

    /// <summary>The HSGuru slice the keep rate and the kept / replaced / drawn numbers come from; null for HSReplay's.</summary>
    public MulliganSlice? Slice { get; init; }

    /// <summary>The card's Arena draft score and its tier letter (as on the draft screen); null outside Arena.</summary>
    public double? PickScore { get; init; }

    public string? PickTier { get; init; }
}

/// <summary>The mulligan panel: whose numbers they are and one row per offered card.</summary>
/// <param name="BaseWinRate">The deck's win rate with an average opening hand, moved by the opponent's class or the coin when known.</param>
public sealed record MulliganView(string Context, double BaseWinRate, IReadOnlyList<MulliganCardStat> Cards, string Source)
{
    /// <summary>What replacing a card is worth on average (win rate delta of a card drawn from the rest of the deck).</summary>
    public double ReplaceDelta { get; init; }

    /// <summary>The average hand's win rate the card deltas are measured against (before the class and coin adjustments).</summary>
    public double CardBaseline { get; init; }

    /// <summary>The class <see cref="BaseWinRate"/> was adjusted for; null when it was not.</summary>
    public string? OpponentClass { get; init; }

    public double? ClassShift { get; init; }

    /// <summary>Going first (true) or on the coin (false), when the baseline or the cards were adjusted for it.</summary>
    public bool? GoingFirst { get; init; }

    /// <summary>The HSGuru rank most cards' slice numbers come from ("legend", "all"); null without slices.</summary>
    public string? SliceRank { get; init; }

    /// <summary>Entity ids of the cards the guide would keep.</summary>
    public IReadOnlyList<int> Recommended { get; init; } = [];

    /// <summary>False while no server endpoint publishes card pairs: synergies cannot be shown.</summary>
    public bool HasPairData { get; init; }

    public IReadOnlyList<MulliganSynergy> Synergies { get; init; } = [];

    /// <summary>
    /// Why there are no numbers ("No mulligan stats for this deck yet"); null when the guide or the meta answered. The
    /// overlay shows the offered cards anyway from the start of the mulligan, with this line instead of the impact.
    /// </summary>
    public string? NoStats { get; init; }

    /// <summary>
    /// The numbers are the Arena cards' own results (<see cref="MulliganArena"/>), with this win rate standing in for the
    /// opening hand's; null for a deck's or an archetype's mulligan stats.
    /// </summary>
    public ArenaCardBasis? ArenaBasis { get; init; }
}

/// <summary>
/// Builds the mulligan panel while the local player chooses their opening hand (docs/mulligan-v2.md). Numbers come
/// from the Hearthpulse API (HSReplay Legend, Standard) for the archetype the deck matches; the meta's archetypes
/// (<see cref="ConstructedMeta"/>, matched by core cards) are the fallback, pooled over the class when none matches.
/// Arena decks are unique and have no mulligan stats: there the Arena cards' own results speak (<see cref="MulliganArena"/>).
/// </summary>
public static class Mulligan
{
    /// <summary>Fewer offers than this and the numbers are too noisy to show.</summary>
    public const int MinSample = MulliganGuide.MinSample;

    public static bool InProgress(GameState game) =>
        game.LocalPlayerEntity?.GetString(GameTag.MulliganState) is "INPUT" or "DEALING";

    /// <summary>
    /// The panel while the local player's mulligan is on (INPUT, DEALING), null otherwise. Without numbers for the deck
    /// (the guide still loading, an unknown deck or archetype) it still lists the offered cards, with <see cref="MulliganView.NoStats"/>.
    /// </summary>
    /// <param name="arena">Arena card results, for an Arena game.</param>
    public static MulliganView? Build(GameState game, CardDb cards, DeckDefinition? deck, ConstructedMeta? meta, string playerClass, GuideArchetype? guide = null,
        ArenaMeta? arena = null)
    {
        if (!InProgress(game))
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
        var goingFirst = GoingFirst(game, cards);

        if (MulliganArena.IsArena(game))
        {
            // Constructed numbers do not fit an Arena deck: only the Arena cards' results.
            if (arena is { HasCards: true } && MulliganArena.Build(arena, cards, playerClass, offered, deckIds, MulliganArena.IsUnderground(game)) is { } fromArena)
            {
                return fromArena;
            }

            return NoNumbers(offered, goingFirst, arena is { HasCards: true } ? Loc.T("Mulligan.NoStats.ArenaNone") : Loc.T("Mulligan.NoStats.ArenaNotYet"));
        }

        // A guide that cannot compute (no win rates in its rows) leaves the meta to speak.
        if (guide is not null && MulliganGuide.Compute(guide, offered, deckIds, opponentClass, goingFirst, guide.Name) is { } view)
        {
            return view;
        }

        if (meta is not null && FormatMatches(game.FormatType, meta.Format) && FromMeta(meta, cards, playerClass, deckIds) is { } fallback
            && MulliganGuide.Compute(fallback, offered, deckIds, opponentClass, goingFirst, fallback.Name) is { } fromMeta)
        {
            return fromMeta;
        }

        var reason = deck is null ? Loc.T("Mulligan.NoStats.NoDeck")
            : guide is null && meta is null ? Loc.T("Mulligan.NoStats.NotYet")
            : Loc.T("Mulligan.NoStats.None");
        return NoNumbers(offered, goingFirst, reason);
    }

    /// <summary>The offered cards without numbers, and why.</summary>
    private static MulliganView NoNumbers(IReadOnlyList<OfferedCard> offered, bool? goingFirst, string reason) =>
        new(string.Empty, 0, offered.Select(o => new MulliganCardStat(o.EntityId, o.CardId, o.Name, null, null, null, 0)
        {
            Cost = o.Cost,
            ExpectedTurn = o.Cost,
            Call = MulliganCall.NoData,
        }).ToList(), string.Empty)
        {
            NoStats = reason,
            GoingFirst = goingFirst,
        };

    /// <summary>
    /// One line for hearthpulse.log while the mulligan is on: whether the strip has numbers to show, and why not
    /// ("mulligan strip without numbers: no keep stats for 3/3 cards"). The overlay shows the strip all through the mulligan.
    /// </summary>
    public static string Explain(GameState game, MulliganView? view, DeckDefinition? deck, bool hasGuide, ConstructedMeta? meta)
    {
        var hand = game.Controlled(game.LocalPlayerId, "HAND").Count(e => e.CardId.Length > 0);
        if (view is { NoStats: null, ArenaBasis: { } basis })
        {
            return $"mulligan strip shown: Arena card results ({basis}) for {view.Cards.Count(c => c.Delta is not null)}/{view.Cards.Count} cards";
        }

        if (MulliganArena.IsArena(game) && hand > 0)
        {
            return "mulligan strip without numbers: the Arena card results are not loaded or have nothing for this class";
        }

        if (view is { NoStats: null })
        {
            var withStats = view.Cards.Count(c => c.KeepRate is not null);
            return withStats > 0
                ? $"mulligan strip shown: {view.Context}, keep stats for {withStats}/{view.Cards.Count} cards ({view.Source})"
                : $"mulligan strip without numbers: no keep stats for {view.Cards.Count}/{view.Cards.Count} cards ({view.Context}, {view.Source})";
        }

        if (hand == 0)
        {
            return "mulligan strip without numbers: the opening hand is not in the log yet";
        }

        if (!hasGuide && meta is null)
        {
            return deck is null
                ? "mulligan strip without numbers: Decks.log named no deck for this game and the community stats are not loaded"
                : "mulligan strip without numbers: no API guide for this deck (yet) and the community stats are not loaded";
        }

        return meta is not null && !FormatMatches(game.FormatType, meta.Format)
            ? $"mulligan strip without numbers: no API guide for this deck and the community stats are for {meta.Format}, the game is {game.FormatType}"
            : "mulligan strip without numbers: neither the API guide nor the community stats have numbers for this deck's class";
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
        var className = Loc.IsRussian ? ConstructedMeta.ClassName(playerClass) : Title(playerClass);
        return new GuideArchetype(0, Loc.F("Mulligan.ClassPool", className), playerClass, pooled, [], meta.Source)
        {
            // Mixed sources (HSGuru win rates, HSReplay cards): the cards' own sample gives the baseline.
            BaselineWinRate = games > 0 && archetypes.All(a => a.MulliganBaseline is null) ? archetypes.Sum(a => a.WinRate * a.Games) / games : null,
        };
    }

    private static GuideCard Card(MetaMulliganCard m, CardDb cards) =>
        new(m.CardId, cards.Get(m.CardId)?.DbfId ?? 0, cards.Get(m.CardId)?.Cost ?? 0, m.Drawn, m.KeepRate, m.InHand, m.InHand > 0 ? m.InHandWinRate : null, null);

    /// <summary>
    /// True going first, false on the coin, null when the log does not say yet. Only the first player is tagged
    /// FIRST_PLAYER=1; the second is known by the opponent's tag or by the coin already in hand during the mulligan.
    /// </summary>
    internal static bool? GoingFirst(GameState game, CardDb cards)
    {
        if (game.LocalPlayerEntity?.GetString(GameTag.FirstPlayer) == "1")
        {
            return true;
        }

        var coin = cards.Get("GAME_005")?.Name;
        var hasCoin = game.Controlled(game.LocalPlayerId, "HAND").Any(e => e.CardId == "GAME_005" || (coin is not null && e.CardId.Length > 0 && cards.Get(e.CardId)?.Name == coin));
        return game.OpponentPlayerEntity?.GetString(GameTag.FirstPlayer) == "1" || hasCoin ? false : null;
    }

    private static bool FormatMatches(string formatType, string metaFormat) => formatType switch
    {
        "FT_STANDARD" => metaFormat == "standard",
        "FT_WILD" => metaFormat == "wild",
        _ => false,
    };

    private static string Title(string upper) => upper.Length == 0 ? upper : upper[0] + upper[1..].ToLowerInvariant();
}
