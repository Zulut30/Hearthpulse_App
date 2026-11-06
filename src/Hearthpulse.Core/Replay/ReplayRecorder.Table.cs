using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

public sealed partial class ReplayRecorder
{
    /// <summary>The game is recorded and still going: after the game over step only the result is written.</summary>
    private bool Live(GameState game) => Ready(game) && !_ended;

    /// <summary>
    /// True when this game is recorded and the local player is known. The first time, writes the whole visible table
    /// (a game picked up mid-way starts from what is on it).
    /// </summary>
    private bool Ready(GameState game)
    {
        if (_synced)
        {
            return !_ignored;
        }

        if (_ignored || !ReferenceEquals(game, _game) || game.LocalPlayerId == 0 || game.GameType.Length == 0)
        {
            return false;
        }

        _synced = true;
        if (game.IsBattlegrounds)
        {
            _bg = true;
            _battlegrounds.Start(game);
            return true;
        }

        if (!game.IsConstructedLike || !LocalPlayerHoldsUp(game))
        {
            _ignored = true;
            return false;
        }

        if (game.Turn > 0)
        {
            EmitTurn(game, game.Turn);
        }

        foreach (var (playerId, slot) in game.Players.OrderBy(p => p.Key))
        {
            var side = playerId == game.LocalPlayerId ? 'L' : 'O';
            var entity = game.Entities.GetValueOrDefault(slot.EntityId);
            foreach (var tag in PlayerTags.Concat(side == 'L' ? LocalPlayerTags : []).Order(StringComparer.Ordinal))
            {
                if (entity?.GetString(tag) is { Length: > 0 } value && value != "0")
                {
                    Emit($"V {side} {tag} {Token(value)}");
                }
            }
        }

        _snapshot = true;
        foreach (var entity in game.Entities.Values.OrderBy(e => e.Id).ToList())
        {
            Sync(game, entity, created: false);
        }

        _snapshot = false;
        WriteDeckCounts();
        return true;
    }

    /// <summary>
    /// Guards the filter itself: everything hinges on which player is local. The client knows the cards of its own
    /// starting hand and not the other's; when the player taken for the opponent holds named starting cards and the
    /// "local" one holds none, the local player was resolved wrongly and the game is not recorded at all.
    /// </summary>
    private static bool LocalPlayerHoldsUp(GameState game)
    {
        static int Named(GameState game, int player) => game.Controlled(player, "HAND")
            .Count(e => e.CardId.Length > 0 && e.CreatedTurn == 0 && e.GetInt(GameTag.Creator) == 0 && e.GetInt("COIN_CARD") == 0);

        var opponent = game.OpponentPlayerId;
        if (opponent == 0 || Named(game, opponent) < 2 || Named(game, game.LocalPlayerId) > 0)
        {
            return true;
        }

        Diag.Info("Replay not recorded: the opponent's starting hand looks known, so the local player may be wrong.");
        return false;
    }

    private void OnGameTag(GameState game, string tag, string value)
    {
        if (tag == GameTag.Turn && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turn))
        {
            EmitTurn(game, turn);
        }
        else if (tag == GameTag.Step && value is "BEGIN_MULLIGAN" or "MAIN_READY" or "FINAL_GAMEOVER")
        {
            Emit($"S {value}");
            _ended |= value == "FINAL_GAMEOVER";
        }
        else if (tag == "PROPOSED_DEFENDER" && _blocks.LastOrDefault(b => b.Type == "ATTACK") is { Emitted: false } attack
                 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var defender) && defender > 0)
        {
            EmitAttack(attack, defender);
        }
    }

    private void EmitTurn(GameState game, int turn)
    {
        var current = game.Players.Values.FirstOrDefault(p => game.Entities.GetValueOrDefault(p.EntityId)?.GetInt("CURRENT_PLAYER") == 1);
        var side = current is null ? '-' : current.PlayerId == game.LocalPlayerId ? 'L' : 'O';
        var seconds = Math.Max(0, (int)(game.LastActivity - game.StartedAt).TotalSeconds);
        Emit(string.Create(CultureInfo.InvariantCulture, $"T {turn} {side} {seconds}"));
    }

    /// <summary>Brings one entity's replay state up to date with the game, under the hidden-information rule.</summary>
    private void Sync(GameState game, Entity entity, bool created)
    {
        if (entity.Id == game.GameEntityId || entity.CardType is "PLAYER" or "GAME" or "ENCHANTMENT" || game.PlayerByEntity(entity.Id) is not null)
        {
            return;
        }

        Learn(game, entity);
        _shown.TryGetValue(entity.Id, out var shown);
        var zone = entity.Zone;
        var controller = entity.Controller;
        if (zone is not ("PLAY" or "GRAVEYARD" or "HAND" or "DECK" or "SECRET") || controller == 0 || !game.Players.ContainsKey(controller))
        {
            LeaveDeck(entity.Id);
            Remove(shown, entity.Id);
            WriteDeckCounts();
            return;
        }

        var side = controller == game.LocalPlayerId ? 'L' : 'O';
        if (zone == "DECK")
        {
            // A deck is a count: the card's table or hand id ends here and nothing about it goes into the deck.
            Remove(shown, entity.Id);
            EnterDeck(entity.Id, side);
            if (side == 'O')
            {
                _hiddenEntities.Add(entity.Id);
            }

            WriteDeckCounts();
            return;
        }

        LeaveDeck(entity.Id);
        // An opponent's secret-zone card is a card back unless it is a quest (shown face up). Not keyed on SECRET=1:
        // that tag can arrive after ZONE=SECRET.
        var hidden = (side == 'O' && zone == "HAND") || (side == 'O' && zone == "SECRET" && !entity.Has("QUEST"));
        if (hidden)
        {
            _hiddenEntities.Add(entity.Id);
        }
        else if (zone == "PLAY")
        {
            _wasInPlay.Add(entity.Id);
        }
        if (!hidden && !TableTypes.Contains(entity.CardType))
        {
            // Not drawable yet (a card whose type arrives on a later line) or never (a Battlegrounds-only type).
            Remove(shown, entity.Id);
            WriteDeckCounts();
            return;
        }

        var card = hidden || entity.CardId.Length == 0 ? "-" : entity.CardId;
        var position = entity.ZonePosition;
        if (shown is not null)
        {
            if (shown.Zone == zone && shown.Side == side && shown.Hidden == hidden)
            {
                if (shown.Card != card)
                {
                    // Transformed or revealed in place (face up only: a hidden card's id is always "-").
                    shown.Card = card;
                    shown.Position = position;
                    EmitEntity(entity, shown, origin: null);
                }
                else if (shown.Position != position)
                {
                    shown.Position = position;
                    Emit(string.Create(CultureInfo.InvariantCulture, $"Z {shown.Id} {position}"));
                }

                return;
            }

            if (!shown.Hidden && !hidden)
            {
                // Face up to face up (hand to play, play to graveyard, a steal): the same card on screen.
                shown.Zone = zone;
                shown.Side = side;
                shown.Position = position;
                shown.Card = card;
                EmitEntity(entity, shown, origin: null);
                AfterMove(entity);
                return;
            }
        }

        // New on the table, or crossing a hidden zone: a new replay id, so nothing links it to what it was.
        (string Zone, char Side)? from = shown is not null ? (shown.Zone, shown.Side) : _left.TryGetValue(entity.Id, out var left) ? left : null;
        Remove(shown, entity.Id);
        _left.Remove(entity.Id);
        WriteDeckCounts();
        var fresh = new Shown(++_nextId) { Zone = zone, Side = side, Position = position, Card = card, Hidden = hidden };
        _shown[entity.Id] = fresh;
        if (hidden)
        {
            _backs[fresh.Id] = entity.Id;
        }

        var origin = side == 'O' && zone == "HAND" ? OriginOf(game, entity, from, created, _snapshot) : null;
        EmitEntity(entity, fresh, origin);
        AfterMove(entity);
    }

    /// <summary>
    /// Notes the first card id the log gives an entity, for the post-game part only (never written as an event), and
    /// whether it was created: kept up to date while the entity still is that card (a reveal's tags come one by one), then
    /// left as it was (a transformation on the table brings a CREATOR the deck card never had).
    /// </summary>
    private void Learn(GameState game, Entity entity)
    {
        if (entity.CardId.Length == 0)
        {
            return;
        }

        if (!_known.TryGetValue(entity.Id, out var known))
        {
            _known[entity.Id] = (entity.CardId, game.Turn, Created(entity));
        }
        else if (known.Card == entity.CardId)
        {
            _known[entity.Id] = known with { Created = Created(entity) };
        }
    }

    /// <summary>Created during the game (not a deck card), or the Coin.</summary>
    private static bool Created(Entity? entity) => entity is { IsOriginalDeckCard: false } || entity?.GetInt("COIN_CARD") == 1;

    /// <summary>How a card back got into the opponent's hand, as the player saw it happen.</summary>
    private static string OriginOf(GameState game, Entity entity, (string Zone, char Side)? from, bool created, bool snapshot)
    {
        var opening = game.InOpeningHand(entity.Controller);
        // The Coin is handed to the second player on screen at the end of the mulligan.
        if (opening && entity.GetInt("COIN_CARD") == 1)
        {
            return "coin";
        }

        return from?.Zone switch
        {
            "DECK" => opening ? "start" : "drawn",
            "PLAY" or "GRAVEYARD" or "SECRET" => "returned",
            "HAND" when from.Value.Side == 'L' => "stolen",
            // Back in the same hand after a detour (set aside and returned): its story is not known.
            "HAND" => "unknown",
            // Already in hand when recording began (picked up mid-deal or mid-game).
            null when snapshot && !created => opening ? "start" : "unknown",
            _ => "created",
        };
    }

    private void EnterDeck(int entityId, char side)
    {
        _decks[side == 'L' ? 0 : 1].Add(entityId);
        _decks[side == 'L' ? 1 : 0].Remove(entityId);
    }

    private void LeaveDeck(int entityId)
    {
        for (var i = 0; i < 2; i++)
        {
            if (_decks[i].Remove(entityId))
            {
                _left[entityId] = ("DECK", i == 0 ? 'L' : 'O');
            }
        }
    }

    /// <summary>Writes each deck's size when it changed: <c>D L 24</c>.</summary>
    private void WriteDeckCounts()
    {
        if (_snapshot)
        {
            return;
        }

        for (var i = 0; i < 2; i++)
        {
            if (_decks[i].Count != _deckWritten[i])
            {
                _deckWritten[i] = _decks[i].Count;
                Emit(string.Create(CultureInfo.InvariantCulture, $"D {(i == 0 ? 'L' : 'O')} {_decks[i].Count}"));
            }
        }
    }
}
