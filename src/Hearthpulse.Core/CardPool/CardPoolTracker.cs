using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// Decides which generator's pool the overlay shows. In priority order: an open Discover choice from a known card,
/// a pool asked for by the UI (<see cref="Request"/>) and the generator under the mouse in your hand
/// (<see cref="SetHovered"/>). Cards on the board and cards just played show no pool.
/// Log events arrive on the log thread; hover and requests from other threads, hence the lock.
/// </summary>
public sealed class CardPoolTracker : IPowerListener
{
    /// <summary>
    /// How long a hovered card's pool stays after the mouse leaves it: long enough to reach the panel beside the card
    /// (resting on it holds the pool), short enough that it does not hang over the board after the card is left.
    /// </summary>
    public static readonly TimeSpan HoverLinger = TimeSpan.FromSeconds(0.8);

    private readonly object _gate = new();
    private string? _choiceSource;
    private int _choiceOptions;
    private (string Source, IReadOnlyList<string> Offered)? _openChoice;
    private string? _hovered;
    private string? _lingering;
    private DateTime _lingerUntil;
    private string? _requested;

    /// <summary>Set by the host once the catalog is loaded; without it nothing is shown.</summary>
    public CardPoolEngine? Engine { get; set; }

    public void OnGameStarted(GameState game)
    {
        lock (_gate)
        {
            _choiceSource = null;
            _openChoice = null;
            _hovered = null;
            _lingering = null;
            _requested = null;
        }
    }

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        // Mulligan and Battlegrounds picks are not card pools; Discover arrives as a GENERAL choice.
        if (choiceType != "GENERAL" || game.IsBattlegrounds)
        {
            return;
        }

        var generator = GeneratorOf(game, source);
        // The options of the player's own choice, which are on the player's screen: what the pick hint is about.
        var open = source is { CardId.Length: > 0 } && source.Controller == game.LocalPlayerId && options.Count > 1 && options.All(o => o.CardId.Length > 0)
            ? (source.CardId, (IReadOnlyList<string>)options.Select(o => o.CardId).ToList())
            : ((string, IReadOnlyList<string>)?)null;
        lock (_gate)
        {
            _choiceSource = generator;
            _choiceOptions = options.Count;
            _openChoice = open;
        }
    }

    /// <summary>The source card id and offered card ids of the player's open choice, or null when none is open.</summary>
    public (string Source, IReadOnlyList<string> Offered)? OpenChoice
    {
        get
        {
            lock (_gate)
            {
                return _openChoice;
            }
        }
    }

    /// <summary>
    /// The card whose pool a choice comes from. The log names the entity that opened it, which can be a helper of the
    /// played card (an enchantment, or a token such as EDR_102t of EDR_102): then the enclosing blocks are asked,
    /// innermost first, for that card. A choice from an unrelated card (Dredge...) never borrows an outer block's pool.
    /// </summary>
    public string? GeneratorOf(GameState game, Entity? source)
    {
        if (source is { CardId.Length: > 0 } && (Engine is null || Engine.Parse(source.CardId).Status != PoolParseStatus.NotGenerator))
        {
            return source.CardId;
        }

        for (var i = game.OpenBlocks.Count - 1; i >= 0; i--)
        {
            if (game.OpenBlocks[i].Source is { CardId.Length: > 0 } outer && IsHelperOf(source, outer)
                && Engine?.Parse(outer.CardId).Status is PoolParseStatus.Pool or PoolParseStatus.Uncomputed)
            {
                return outer.CardId;
            }
        }

        return source is { CardId.Length: > 0 } ? source.CardId : null;
    }

    public void OnChoiceMade(GameState game)
    {
        lock (_gate)
        {
            _choiceSource = null;
            _openChoice = null;
        }
    }

    public void OnGameEnded(GameState game) => OnGameStarted(game);

    private static bool IsHelperOf(Entity? source, Entity outer) =>
        source is null || source.CardType == "ENCHANTMENT"
        || source.CardId.Length > outer.CardId.Length && source.CardId.StartsWith(outer.CardId, StringComparison.Ordinal);

    /// <summary>Show the pool of <paramref name="cardId"/> until called again with null (deck/hand row hover).</summary>
    public void Request(string? cardId)
    {
        lock (_gate)
        {
            _requested = cardId is { Length: > 0 } ? cardId : null;
        }
    }

    /// <summary>The card under the mouse (from game memory), or null. Only cards in your hand count.</summary>
    /// <param name="utcNow">Now (UTC); a card the mouse left stays for <see cref="HoverLinger"/>.</param>
    /// <returns>Whether what is shown may have changed (a new card, or the linger ran out).</returns>
    public bool SetHovered(string? cardId, DateTime utcNow)
    {
        lock (_gate)
        {
            if (_hovered == cardId)
            {
                if (_lingering is not null && utcNow > _lingerUntil)
                {
                    _lingering = null;
                    return true;
                }

                return false;
            }

            _lingering = cardId is null ? _hovered : null;
            _lingerUntil = utcNow + HoverLinger;
            _hovered = cardId;
            return true;
        }
    }

    /// <summary>
    /// Whether a card in your hand has a pool the overlay would show on hover. Only then is the card under the mouse worth
    /// reading from the game (a Discover choice comes from the log).
    /// </summary>
    public bool HandHasPool(GameState game, CardDb cards)
    {
        if (Engine is not { } engine || game.IsBattlegrounds)
        {
            return false;
        }

        var context = ContextOf(game, cards);
        foreach (var card in game.Controlled(game.LocalPlayerId, "HAND"))
        {
            if (card.CardId.Length > 0 && engine.Build(card.CardId, context, PoolTrigger.Hover) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The card under the mouse (or still lingering after it) when it is in your hand; null otherwise.</summary>
    public string? HoveredInHand(GameState game)
    {
        string? hovered;
        lock (_gate)
        {
            hovered = _hovered ?? (_lingering is not null && DateTime.UtcNow <= _lingerUntil ? _lingering : null);
        }

        return hovered is not null && InLocalHand(game, hovered) ? hovered : null;
    }

    /// <summary>Where a card in your hand sits (the leftmost copy), for a panel beside it; null when it is not there.</summary>
    public static CardPoolAnchor? HandAnchor(GameState game, string cardId) =>
        Slot(game.Controlled(game.LocalPlayerId, "HAND").ToList(), cardId) is { } h ? new CardPoolAnchor(PoolAnchorZone.Hand, h.Index, h.Count) : null;

    /// <summary>The pool to show now, or null.</summary>
    public CardPoolView? BuildView(GameState game, CardDb cards)
    {
        if (Engine is not { } engine || game.IsBattlegrounds)
        {
            return null;
        }

        string? choice, requested, hovered;
        lock (_gate)
        {
            choice = _choiceSource;
            requested = _requested;
            hovered = _hovered ?? (_lingering is not null && DateTime.UtcNow <= _lingerUntil ? _lingering : null);
        }

        var context = ContextOf(game, cards);
        if (choice is not null && engine.Build(choice, context, PoolTrigger.Choice) is { } fromChoice)
        {
            return fromChoice;
        }

        if (requested is not null && engine.Build(requested, context, PoolTrigger.Request) is { } fromRequest)
        {
            return fromRequest;
        }

        return hovered is not null && InLocalHand(game, hovered) ? engine.Build(hovered, context, PoolTrigger.Hover) : null;
    }

    /// <summary>Game type, format and both classes of a game (classes from the heroes' cards).</summary>
    public static PoolContext ContextOf(GameState game, CardDb cards) => new(
        game.FormatType,
        ClassOf(game.LocalHero, cards),
        ClassOf(game.OpponentHero, cards))
    {
        GameType = game.GameType,
    };

    private static string? ClassOf(Entity? hero, CardDb cards) =>
        hero is null ? null
        : cards.Get(hero.CardId)?.CardClass is { Length: > 0 } c && c != "NEUTRAL" ? c
        : hero.GetString(GameTag.Class) is { Length: > 0 } tag && tag != "NEUTRAL" && tag != "INVALID" ? tag
        : null;

    /// <summary>
    /// Where the card of a pool sits, so the panel can stand next to it: the Discover options or the hovered card in your
    /// hand. Null (the panel's own spot) for a requested pool. Two copies of a hovered card: the leftmost one.
    /// </summary>
    public CardPoolAnchor? AnchorOf(GameState game, CardPoolView? view)
    {
        int options;
        lock (_gate)
        {
            options = _choiceOptions;
        }

        if (view is null || view.Trigger == PoolTrigger.Request)
        {
            return null;
        }

        if (view.Trigger == PoolTrigger.Choice)
        {
            return new CardPoolAnchor(PoolAnchorZone.Choice, 0, Math.Clamp(options, 1, 5));
        }

        return HandAnchor(game, view.SourceCardId);
    }

    private static (int Index, int Count)? Slot(List<Entity> row, string cardId)
    {
        row.Sort((a, b) => a.ZonePosition.CompareTo(b.ZonePosition));
        var index = row.FindIndex(e => e.CardId == cardId);
        return index < 0 ? null : (index, row.Count);
    }

    private static bool InLocalHand(GameState game, string cardId) =>
        game.Controlled(game.LocalPlayerId, "HAND").Any(e => e.CardId == cardId);
}
