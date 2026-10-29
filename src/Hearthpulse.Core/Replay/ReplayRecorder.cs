using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// Records a Constructed-like game as the local player saw it, as the event lines of a replay file
/// (<c>docs/match-replay.md</c>). The hidden-information rule is applied to the events (<see cref="Events"/>, what the
/// game showed while it was played):
/// <list type="bullet">
/// <item>a card id is written only while the card is face up for the local player;</item>
/// <item>a deck is a number of cards and nothing else: no card in a deck has a replay id, so a card going into a deck
/// (a mulligan, a trade, a shuffle) cannot be told apart from the others when one is drawn;</item>
/// <item>a card that moves into or out of a hidden zone (a deck, the opponent's hand, an opponent's secret) gets a new
/// replay id, so it cannot be followed;</item>
/// <item>game entity ids are never written, and <c>CREATOR_DBID</c> is never read;</item>
/// <item>nothing is recorded after the game is over but the result.</item>
/// </list>
/// Only <see cref="Text"/>, called once the game is over, adds the post-game part (<see cref="PostGame"/>): which game
/// entity each opponent card back was and what the log revealed about it by the end of the game. The match page shows
/// it after the game; nothing seen during a game reads it.
/// A Battlegrounds game goes to <see cref="Battlegrounds"/> under its own rules; Mercenaries games are not recorded.
/// </summary>
public sealed class ReplayRecorder : IPowerListener
{
    /// <summary>A game stops recording after this many events; the file says it is truncated.</summary>
    public const int MaxEvents = 60_000;

    private const int MaxBlockDepth = 64;

    /// <summary>Tags of a face-up card the replay shows: stats, keywords, cost.</summary>
    private static readonly HashSet<string> CardTags = new(StringComparer.Ordinal)
    {
        GameTag.Atk, GameTag.Health, GameTag.Damage, GameTag.Armor, GameTag.Cost, "DURABILITY", GameTag.Taunt,
        GameTag.DivineShield, GameTag.Stealth, "FROZEN", "SILENCED", GameTag.Poisonous, GameTag.Windfury, GameTag.Reborn,
        "LIFESTEAL", "DORMANT", "IMMUNE", GameTag.Premium, "DEATHRATTLE",
    };

    /// <summary>Player tags the replay shows: mana crystals and overload (both players), the local mulligan and result.</summary>
    private static readonly HashSet<string> PlayerTags = new(StringComparer.Ordinal)
    {
        "RESOURCES", "RESOURCES_USED", "TEMP_RESOURCES", "OVERLOAD_LOCKED", "OVERLOAD_OWED",
    };

    private static readonly HashSet<string> LocalPlayerTags = new(StringComparer.Ordinal) { GameTag.MulliganState, GameTag.PlayState };

    /// <summary>Card types drawn on the table; everything else (enchantments, players, the game) is never written.</summary>
    private static readonly HashSet<string> TableTypes = new(StringComparer.Ordinal) { "MINION", "SPELL", "WEAPON", "HERO", "HERO_POWER", "LOCATION" };

    private readonly List<string> _events = new();
    private readonly Dictionary<int, Shown> _shown = new();
    private readonly List<Pending> _blocks = new();

    // The cards in each deck (local, opponent) by game entity id, kept only to count them: nothing per card is written.
    private readonly HashSet<int>[] _decks = [new(), new()];
    private readonly int[] _deckWritten = [-1, -1];

    // Where a card was when it last left the table or a deck (it may come back from SETASIDE), for a card back's origin.
    private readonly Dictionary<int, (string Zone, char Side)> _left = new();
    // Post-game part, kept aside from the events: opponent card backs by replay id -> game entity id, the opponent
    // entities that were hidden (hand, secret, deck), the first card id the log gave each entity, on which game turn and
    // whether it was created during the game then (later ids are transformations: python-hearthstone's initial_card_id
    // works the same way; a deck card transformed on the table gets a CREATOR it did not have), cards played from hand
    // and cards that were on the table.
    private readonly Dictionary<int, int> _backs = new();
    private readonly HashSet<int> _hiddenEntities = new();
    private readonly Dictionary<int, (string Card, int Turn, bool Created)> _known = new();
    private readonly HashSet<int> _played = new();
    private readonly HashSet<int> _wasInPlay = new();
    private readonly BattlegroundsReplayRecorder _battlegrounds = new();
    private GameState? _game;
    private int _nextId;
    private bool _synced;
    private bool _ignored;
    private bool _snapshot;
    private bool _ended;
    private bool _bg;

    /// <summary>The events recorded for the current game, in order.</summary>
    public IReadOnlyList<string> Events => _bg ? _battlegrounds.Events : _events;

    /// <summary>True when the game hit <see cref="MaxEvents"/> and the rest was not recorded.</summary>
    public bool Truncated
    {
        get => _bg ? _battlegrounds.Truncated : _truncated;
        private set => _truncated = value;
    }

    private bool _truncated;

    /// <summary>
    /// True once anything was recorded for the current game (a Constructed-like or Battlegrounds game with a known local
    /// player).
    /// </summary>
    public bool HasGame => _synced && !_ignored && Events.Count > 0;

    /// <summary>True when the current game is a Battlegrounds game, recorded by <see cref="Battlegrounds"/>.</summary>
    public bool IsBattlegrounds => _bg;

    /// <summary>The Battlegrounds recording of the current game (empty for other games).</summary>
    public BattlegroundsReplayRecorder Battlegrounds => _battlegrounds;

    /// <summary>
    /// The replay file text for the current game: the events, then the post-game part. <paramref name="postGame"/> is
    /// false for a game that did not end (abandoned when the client left): it may go on after a reconnect, so its file
    /// holds only what was seen.
    /// </summary>
    public string Text(ReplayHeader header, bool postGame = true) =>
        ReplayFile.Write(header with { Truncated = Truncated, Events = _events.Count }, postGame ? _events.Concat(PostGame()) : _events);


    /// <summary>
    /// What the log revealed by the end of the game about the opponent's hidden cards (<c>N</c> and <c>K</c> lines). Only
    /// for the file of a finished game: the match page uses it to show those cards face up on the turns they were hidden.
    /// </summary>
    public IEnumerable<string> PostGame()
    {
        foreach (var (replayId, entityId) in _backs.OrderBy(b => b.Key))
        {
            yield return string.Create(CultureInfo.InvariantCulture, $"N {replayId} {entityId}");
        }

        foreach (var entityId in _hiddenEntities.Order())
        {
            var entity = _game?.Entities.GetValueOrDefault(entityId);
            var (card, turn, created) = _known.TryGetValue(entityId, out var known) ? known : (entity?.CardId ?? string.Empty, _game?.Turn ?? 0, Created(entity));
            if (card.Length == 0)
            {
                continue;
            }

            var how = _played.Contains(entityId) ? "played"
                : _wasInPlay.Contains(entityId) ? "summoned"
                : entity?.Zone == "GRAVEYARD" ? "burned"
                : "shown";
            var from = entity?.GetInt("TRANSFORMED_FROM_CARD") ?? 0;
            yield return string.Create(CultureInfo.InvariantCulture, $"K {entityId} {card} {turn} {how} {(created ? 1 : 0)}{(from > 0 ? $" {from}" : string.Empty)}");
        }
    }

    public void OnGameStarted(GameState game)
    {
        _game = game;
        _events.Clear();
        _shown.Clear();
        _blocks.Clear();
        _decks[0].Clear();
        _decks[1].Clear();
        _deckWritten[0] = _deckWritten[1] = -1;
        _left.Clear();
        _backs.Clear();
        _hiddenEntities.Clear();
        _known.Clear();
        _played.Clear();
        _wasInPlay.Clear();
        _nextId = 0;
        _synced = false;
        _ignored = false;
        _ended = false;
        _bg = false;
        _battlegrounds.Reset();
        Truncated = false;
    }

    public void OnEntityCreated(GameState game, Entity entity)
    {
        if (!Live(game))
        {
            return;
        }

        if (_bg)
        {
            _battlegrounds.OnEntityCreated(game, entity);
            return;
        }

        Sync(game, entity, created: true);
    }

    public void OnEntityRevealed(GameState game, Entity entity)
    {
        if (!Live(game))
        {
            return;
        }

        if (_bg)
        {
            _battlegrounds.OnEntityRevealed(game, entity);
            return;
        }

        Sync(game, entity, created: false);
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (!Live(game))
        {
            return;
        }

        if (_bg)
        {
            _battlegrounds.OnTagChanged(game, entity, tag, value);
            return;
        }

        if (entity.Id == game.GameEntityId)
        {
            OnGameTag(game, tag, value);
            return;
        }

        if (game.PlayerByEntity(entity.Id) is { } player)
        {
            var local = player.PlayerId == game.LocalPlayerId;
            if (PlayerTags.Contains(tag) || (local && LocalPlayerTags.Contains(tag)))
            {
                Emit($"V {(local ? 'L' : 'O')} {tag} {Token(value)}");
            }

            return;
        }

        if (tag is GameTag.Zone or GameTag.Controller or GameTag.ZonePosition or GameTag.CardType or GameTag.Secret)
        {
            Sync(game, entity, created: false);
            return;
        }

        if (CardTags.Contains(tag) && _shown.TryGetValue(entity.Id, out var shown) && !shown.Hidden)
        {
            Emit($"V {shown.Id} {tag} {Token(value)}");
        }
    }

    public void OnBlockStart(GameState game, string blockType, Entity? source)
    {
        if (!Live(game))
        {
            return;
        }

        if (_bg)
        {
            _battlegrounds.OnBlockStart(game, blockType, source);
            return;
        }

        if (_blocks.Count >= MaxBlockDepth)
        {
            _blocks.Clear();
        }

        var pending = new Pending(blockType, source, source?.GetInt("CARD_TARGET") ?? 0);
        _blocks.Add(pending);
        if (blockType != "PLAY")
        {
            return;
        }

        if (source is { Zone: "HAND" })
        {
            _played.Add(source.Id);
        }

        // A step boundary: what follows (the card leaving the hand, its effects) belongs to this play.
        Emit("B");
        if (source is { Zone: not "HAND" })
        {
            // Already on the table: a hero power or a location being used, not a card played from hand.
            EmitPlay(pending, used: true);
        }
    }

    public void OnBlockEnd(GameState game)
    {
        if (_bg && Live(game))
        {
            _battlegrounds.OnBlockEnd(game);
            return;
        }

        if (!Live(game) || _blocks.Count == 0)
        {
            return;
        }

        var block = _blocks[^1];
        _blocks.RemoveAt(_blocks.Count - 1);
        if (block.Emitted)
        {
            return;
        }

        if (block.Type == "PLAY")
        {
            EmitPlay(block);
        }
        else if (block.Type == "ATTACK")
        {
            EmitAttack(block, game.GameEntity?.GetInt("PROPOSED_DEFENDER") ?? 0);
        }
    }

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        // The parser reports only the local player's choices, whose options the player sees.
        if (!Live(game))
        {
            return;
        }

        if (_bg)
        {
            _battlegrounds.OnChoices(game, choiceType, source, options);
            return;
        }

        // The mulligan offers the chooser's own hand. Options held by the other player mean the local player was taken
        // wrongly: the whole filter would be reversed, so nothing of this game is kept.
        if (choiceType == "MULLIGAN" && options.Any(o => o.Controller != 0 && o.Controller != game.LocalPlayerId))
        {
            Diag.Info("Replay not recorded: the mulligan offer belongs to the player taken for the opponent.");
            _ignored = true;
            _events.Clear();
            return;
        }

        var ids = options.Select(o => o.CardId).Where(id => id.Length > 0).ToList();
        if (ids.Count > 0)
        {
            Emit($"C {Token(choiceType)} {FaceUpCard(source)} {string.Join(',', ids)}");
        }
    }

    public void OnChosen(GameState game, Entity chosen)
    {
        if (_bg && Live(game))
        {
            _battlegrounds.OnChosen(game, chosen);
        }
        else if (Live(game) && chosen.CardId.Length > 0)
        {
            Emit($"c {chosen.CardId}");
        }
    }

    public void OnGameEnded(GameState game)
    {
        if (_bg && Live(game))
        {
            _battlegrounds.OnGameEnded(game);
        }
        else if (Ready(game) && !_bg && game.LocalPlayerEntity?.GetString(GameTag.PlayState) is { Length: > 0 } result)
        {
            Emit($"R {Token(result)}");
        }

        _ended = true;
    }

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

    private void AfterMove(Entity entity)
    {
        // A card played from hand is announced once it has left the hand (face up, or as a secret's card back).
        if (entity.Zone == "HAND")
        {
            return;
        }

        foreach (var block in _blocks)
        {
            if (block is { Type: "PLAY", Emitted: false } && block.Source?.Id == entity.Id)
            {
                EmitPlay(block);
            }
        }
    }

    private void EmitEntity(Entity entity, Shown shown, string? origin)
    {
        var line = new StringBuilder(64);
        line.Append(CultureInfo.InvariantCulture, $"E {shown.Id} {shown.Side} {shown.Zone} {shown.Position} {shown.Card} ");
        line.Append(shown.Hidden ? "-" : Token(entity.CardType));
        if (!shown.Hidden)
        {
            foreach (var (tag, value) in entity.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (CardTags.Contains(tag) && value.Length > 0 && value != "0")
                {
                    line.Append(' ').Append(tag).Append('=').Append(Token(value));
                }
            }
        }
        else if (origin is not null)
        {
            line.Append(" ORIGIN=").Append(origin);
        }

        Emit(line.ToString());
    }

    private void Remove(Shown? shown, int entityId)
    {
        if (shown is null)
        {
            return;
        }

        _shown.Remove(entityId);
        _left[entityId] = (shown.Zone, shown.Side);
        Emit(string.Create(CultureInfo.InvariantCulture, $"X {shown.Id}"));
    }

    private void EmitPlay(Pending block, bool used = false)
    {
        if (block.Source is not { } source || !_shown.TryGetValue(source.Id, out var shown))
        {
            return;
        }

        block.Emitted = true;
        Emit($"{(used ? 'U' : 'P')} {shown.Id} {FaceUpId(block.Target)}");
    }

    private void EmitAttack(Pending block, int defender)
    {
        if (block.Source is not { } source || !_shown.TryGetValue(source.Id, out var attacker))
        {
            return;
        }

        block.Emitted = true;
        Emit($"A {attacker.Id} {FaceUpId(defender)}");
    }

    /// <summary>The replay id of a face-up card, else "-".</summary>
    private string FaceUpId(int entityId) =>
        entityId > 0 && _shown.TryGetValue(entityId, out var shown) && !shown.Hidden
            ? shown.Id.ToString(CultureInfo.InvariantCulture)
            : "-";

    private string FaceUpCard(Entity? entity) =>
        entity is not null && _shown.TryGetValue(entity.Id, out var shown) && !shown.Hidden && shown.Card != "-" ? shown.Card : "-";

    private void Emit(string line)
    {
        if (_events.Count >= MaxEvents)
        {
            Truncated = true;
            return;
        }

        _events.Add(line);
    }

    /// <summary>A value as one space-free token.</summary>
    private static string Token(string value) => value.Length == 0 ? "-" : value.Replace(' ', '_');

    private sealed class Shown(int id)
    {
        public int Id { get; } = id;

        public string Zone { get; set; } = string.Empty;

        public char Side { get; set; }

        public int Position { get; set; }

        public string Card { get; set; } = "-";

        public bool Hidden { get; set; }
    }

    private sealed class Pending(string type, Entity? source, int target)
    {
        public string Type { get; } = type;

        public Entity? Source { get; } = source;

        public int Target { get; } = target;

        public bool Emitted { get; set; }
    }
}
