using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.CardPool;

/// <summary>
/// Decides which generator's pool the overlay shows. In priority order: an open Discover choice from a known card,
/// a pool asked for by the UI (<see cref="Request"/>), the generator under the mouse in your hand
/// (<see cref="SetHovered"/>), and a generator you just played (for <see cref="PlayedFor"/>).
/// Log events arrive on the log thread; hover and requests from other threads, hence the lock.
/// </summary>
public sealed class CardPoolTracker : IPowerListener
{
    /// <summary>How long the pool of a played generator stays up.</summary>
    public static readonly TimeSpan PlayedFor = TimeSpan.FromSeconds(6);

    /// <summary>How long a hovered card's pool stays after the mouse leaves it, so the panel can be reached.</summary>
    public static readonly TimeSpan HoverLinger = TimeSpan.FromSeconds(1.5);

    private readonly object _gate = new();
    private string? _choiceSource;
    private string? _played;
    private DateTime _playedAt;
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
            _played = null;
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
        lock (_gate)
        {
            _choiceSource = generator;
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
        }
    }

    public void OnBlockStart(GameState game, string blockType, Entity? source)
    {
        if (blockType != "PLAY" || game.IsBattlegrounds || source is not { CardId.Length: > 0 } || source.Controller != game.LocalPlayerId
            || Engine?.Parse(source.CardId).Status is not (PoolParseStatus.Pool or PoolParseStatus.Uncomputed))
        {
            return;
        }

        lock (_gate)
        {
            _played = source.CardId;
            _playedAt = game.LastActivity;
        }
    }

    public void OnGameEnded(GameState game) => OnGameStarted(game);

    /// <summary>
    /// True once when the "just played" pool has run out (<see cref="PlayedFor"/>), so the host can publish again even
    /// when the log is quiet.
    /// </summary>
    public bool PlayedExpired(DateTime now)
    {
        lock (_gate)
        {
            if (_played is null || now - _playedAt <= PlayedFor)
            {
                return false;
            }

            _played = null;
            return true;
        }
    }

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

    /// <summary>The pool to show now, or null.</summary>
    /// <param name="now">Local time, compared with log timestamps for the "just played" pool.</param>
    public CardPoolView? BuildView(GameState game, CardDb cards, DateTime now)
    {
        if (Engine is not { } engine || game.IsBattlegrounds)
        {
            return null;
        }

        string? choice, requested, hovered, played;
        lock (_gate)
        {
            choice = _choiceSource;
            requested = _requested;
            hovered = _hovered ?? (_lingering is not null && DateTime.UtcNow <= _lingerUntil ? _lingering : null);
            played = _played is not null && now - _playedAt <= PlayedFor ? _played : null;
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

        if (hovered is not null && InLocalHand(game, hovered) && engine.Build(hovered, context, PoolTrigger.Hover) is { } fromHover)
        {
            return fromHover;
        }

        return played is not null ? engine.Build(played, context, PoolTrigger.Played) : null;
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

    private static bool InLocalHand(GameState game, string cardId) =>
        game.Controlled(game.LocalPlayerId, "HAND").Any(e => e.CardId == cardId);
}
