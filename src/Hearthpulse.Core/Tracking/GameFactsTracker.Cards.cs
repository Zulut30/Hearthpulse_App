using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Constructed card facts for the deck page: the local mulligan (offered, replaced, what came back), the deck cards
/// the player drew and played with their turn, and the opponent's deck cards that were played face up.
/// </summary>
/// <remarks>
/// Each event notes the entity with its card as it was then: some cards transform later (CHANGE_ENTITY), and the deck
/// page wants the card that came from the deck. Whose card it was is read when the game ends, because the local player
/// is often not known yet while the opening hand is dealt. Only what the opponent showed is kept (no hidden
/// information).
/// </remarks>
public sealed partial class GameFactsTracker
{
    private static readonly HashSet<string> NotPlayedCards = ["HERO_POWER", "ENCHANTMENT", "PLAYER", "GAME", ""];

    private readonly List<Noted> _mulliganOffered = new();
    private readonly HashSet<int> _mulliganReplaced = new();
    private readonly List<Noted> _mulliganReceived = new();
    private readonly List<Noted> _draws = new();
    private readonly List<Noted> _plays = new();

    /// <summary>An entity at the moment of an event: its card then, and whether it came from the starting deck.</summary>
    private readonly record struct Noted(int Id, int Controller, int Turn, string CardId, bool FromDeck, string CardType);

    /// <summary>The local mulligan, or null when the game had none (or the log did not show the choice).</summary>
    public MulliganFacts? Mulligan(GameState game)
    {
        if (_mulliganOffered.Count == 0)
        {
            return null;
        }

        return new MulliganFacts(
            CardIds(game, _mulliganOffered),
            CardIds(game, _mulliganOffered.Where(n => _mulliganReplaced.Contains(n.Id))),
            CardIds(game, _mulliganReceived.Where(n => n.Controller == game.LocalPlayerId)));
    }

    /// <summary>Deck cards the local player drew, with the player's turn (0 = the opening hand after the mulligan).</summary>
    public IReadOnlyList<CardTurn> Drawn(GameState game) => DeckCards(game, _draws, game.LocalPlayerId);

    /// <summary>Deck cards the local player played from hand, with the player's turn.</summary>
    public IReadOnlyList<CardTurn> Played(GameState game) => DeckCards(game, _plays, game.LocalPlayerId);

    /// <summary>Cards from the opponent's starting deck that the opponent played (shown face up), with their turn.</summary>
    public IReadOnlyList<CardTurn> OpponentPlayed(GameState game) => DeckCards(game, _plays, game.OpponentPlayerId);

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (tag != GameTag.Zone || game.IsBattlegrounds)
        {
            return;
        }

        var controller = entity.Controller;
        var opening = game.InOpeningHand(controller);
        if (previous == "DECK" && value == "HAND")
        {
            var draw = Note(entity, opening ? 0 : PlayerTurn(game.Turn));
            _draws.Add(draw);
            if (game.MulliganStateOf(controller) == "DEALING")
            {
                _mulliganReceived.Add(draw);
            }
        }
        else if (previous == "HAND" && value == "DECK" && opening)
        {
            // Sent back in the mulligan: it was not kept, so it was not drawn (a later draw of it counts again).
            _mulliganReplaced.Add(entity.Id);
            var at = _draws.FindLastIndex(d => d.Id == entity.Id && d.Turn == 0);
            if (at >= 0)
            {
                _draws.RemoveAt(at);
            }
        }
    }

    public void OnBlockStart(GameState game, string blockType, Entity? source)
    {
        if (blockType == "PLAY" && source is not null && !game.IsBattlegrounds)
        {
            _plays.Add(Note(source, PlayerTurn(game.Turn)));
        }
    }

    private void NoteMulliganOffer(GameState game, string choiceType, IReadOnlyList<Entity> options)
    {
        // The parser reports only the local player's choices. The coin is offered too but is not a deck card.
        if (game.IsBattlegrounds || choiceType != "MULLIGAN" || _mulliganOffered.Count > 0)
        {
            return;
        }

        _mulliganOffered.AddRange(options.Where(e => !e.IsHero && e.GetInt(GameTag.Creator) == 0).Select(e => Note(e, 0)));
    }

    private void ResetCards()
    {
        _mulliganOffered.Clear();
        _mulliganReplaced.Clear();
        _mulliganReceived.Clear();
        _draws.Clear();
        _plays.Clear();
    }

    private static Noted Note(Entity entity, int turn) =>
        new(entity.Id, entity.Controller, turn, entity.CardId, entity.IsOriginalDeckCard, entity.CardType);

    /// <summary>The card noted, or for a card hidden then (an opponent card shown later) the entity's card now.</summary>
    private static string CardOf(GameState game, Noted noted) =>
        noted.CardId.Length > 0 ? noted.CardId : game.Entities.GetValueOrDefault(noted.Id)?.CardId ?? string.Empty;

    private static List<CardTurn> DeckCards(GameState game, List<Noted> noted, int player) =>
        player == 0
            ? []
            // FromDeck leaves out the starting hero and anything created during the game.
            : noted.Where(n => n.Controller == player && n.FromDeck)
                .Select(n => (n.Turn, CardId: CardOf(game, n), Type: n.CardType.Length > 0 ? n.CardType : game.Entities.GetValueOrDefault(n.Id)?.CardType ?? string.Empty))
                .Where(n => n.CardId.Length > 0 && !NotPlayedCards.Contains(n.Type))
                .Select(n => new CardTurn(n.CardId, n.Turn))
                .ToList();

    private static List<string> CardIds(GameState game, IEnumerable<Noted> noted) =>
        noted.Select(n => CardOf(game, n)).Where(id => id.Length > 0).ToList();

    private static int PlayerTurn(int turn) => turn <= 0 ? 0 : (turn + 1) / 2;
}
