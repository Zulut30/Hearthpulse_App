using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Constructed card facts for the deck page: the local mulligan (offered, replaced, what came back), the deck cards
/// the player drew and played with their turn, and the opponent's deck cards that were played face up.
/// </summary>
/// <remarks>
/// Entities are noted by id and read out when the game ends: the local player is often not known yet while the
/// opening hand is dealt, and an opponent card's id is shown only once it is played. Only what the opponent showed is
/// kept (no hidden information).
/// </remarks>
public sealed partial class GameFactsTracker
{
    private static readonly HashSet<string> NotPlayedCards = ["HERO_POWER", "ENCHANTMENT", "PLAYER", "GAME", ""];

    private readonly List<int> _mulliganOffered = new();
    private readonly HashSet<int> _mulliganReplaced = new();
    private readonly List<int> _mulliganReceived = new();
    private readonly List<(int Id, int Controller, int Turn)> _draws = new();
    private readonly List<(int Id, int Controller, int Turn)> _plays = new();

    /// <summary>The local mulligan, or null when the game had none (or the log did not show the choice).</summary>
    public MulliganFacts? Mulligan(GameState game)
    {
        if (_mulliganOffered.Count == 0)
        {
            return null;
        }

        var replaced = _mulliganOffered.Where(_mulliganReplaced.Contains).ToList();
        return new MulliganFacts(
            CardIds(game, _mulliganOffered),
            CardIds(game, replaced),
            CardIds(game, _mulliganReceived.Where(id => game.Entities.GetValueOrDefault(id)?.Controller == game.LocalPlayerId)));
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
            _draws.Add((entity.Id, controller, opening ? 0 : PlayerTurn(game.Turn)));
            if (game.MulliganStateOf(controller) == "DEALING")
            {
                _mulliganReceived.Add(entity.Id);
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
            _plays.Add((source.Id, source.Controller, PlayerTurn(game.Turn)));
        }
    }

    private void NoteMulliganOffer(GameState game, string choiceType, IReadOnlyList<Entity> options)
    {
        // The parser reports only the local player's choices. The coin is offered too but is not a deck card.
        if (game.IsBattlegrounds || choiceType != "MULLIGAN" || _mulliganOffered.Count > 0)
        {
            return;
        }

        _mulliganOffered.AddRange(options.Where(e => !e.IsHero && e.GetInt(GameTag.Creator) == 0).Select(e => e.Id));
    }

    private void ResetCards()
    {
        _mulliganOffered.Clear();
        _mulliganReplaced.Clear();
        _mulliganReceived.Clear();
        _draws.Clear();
        _plays.Clear();
    }

    private static List<CardTurn> DeckCards(GameState game, List<(int Id, int Controller, int Turn)> noted, int player) =>
        player == 0
            ? []
            : noted.Where(n => n.Controller == player)
                .Select(n => (Entity: game.Entities.GetValueOrDefault(n.Id), n.Turn))
                .Where(n => n.Entity is { CardId.Length: > 0 } e && e.IsOriginalDeckCard && !NotPlayedCards.Contains(e.CardType)
                            && !(e.IsHero && e.InitialZone == "PLAY"))
                .Select(n => new CardTurn(n.Entity!.CardId, n.Turn))
                .ToList();

    private static List<string> CardIds(GameState game, IEnumerable<int> ids) =>
        ids.Select(id => game.Entities.GetValueOrDefault(id)?.CardId).OfType<string>().Where(id => id.Length > 0).ToList();

    private static int PlayerTurn(int turn) => turn <= 0 ? 0 : (turn + 1) / 2;
}
