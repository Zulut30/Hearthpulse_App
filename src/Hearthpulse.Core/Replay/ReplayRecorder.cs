using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// Records a Constructed-like game as the local player saw it, as the event lines of a replay file
/// (<c>docs/match-replay.md</c>). The hidden-information rule is applied here, so the file never holds more than the
/// player could see: a card id is written only while the card is face up for the local player, a card that moves into
/// or out of a hidden zone (either deck, the opponent's hand, an opponent's secret) gets a new replay id so it cannot be
/// followed, game entity ids are never written, and <c>CREATOR_DBID</c> is never read.
/// Battlegrounds and Mercenaries games are not recorded here (see <see cref="BattlegroundsReplayData"/>).
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
    private GameState? _game;
    private int _nextId;
    private bool _synced;
    private bool _ignored;

    /// <summary>The events recorded for the current game, in order.</summary>
    public IReadOnlyList<string> Events => _events;

    /// <summary>True when the game hit <see cref="MaxEvents"/> and the rest was not recorded.</summary>
    public bool Truncated { get; private set; }

    /// <summary>True once anything was recorded for the current game (a Constructed-like game with a known local player).</summary>
    public bool HasGame => _synced && _events.Count > 0;

    /// <summary>The replay file text for the current game.</summary>
    public string Text(ReplayHeader header) => ReplayFile.Write(header with { Truncated = Truncated, Events = _events.Count }, _events);

    public void OnGameStarted(GameState game)
    {
        _game = game;
        _events.Clear();
        _shown.Clear();
        _blocks.Clear();
        _nextId = 0;
        _synced = false;
        _ignored = false;
        Truncated = false;
    }

    public void OnEntityCreated(GameState game, Entity entity)
    {
        if (Ready(game))
        {
            Sync(game, entity, created: true);
        }
    }

    public void OnEntityRevealed(GameState game, Entity entity)
    {
        if (Ready(game))
        {
            Sync(game, entity, created: false);
        }
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (!Ready(game))
        {
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
        if (!Ready(game))
        {
            return;
        }

        if (_blocks.Count >= MaxBlockDepth)
        {
            _blocks.Clear();
        }

        var pending = new Pending(blockType, source, source?.GetInt("CARD_TARGET") ?? 0);
        _blocks.Add(pending);
        if (blockType == "PLAY" && source is { Zone: not "HAND" })
        {
            EmitPlay(game, pending);
        }
    }

    public void OnBlockEnd(GameState game)
    {
        if (!Ready(game) || _blocks.Count == 0)
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
            EmitPlay(game, block);
        }
        else if (block.Type == "ATTACK")
        {
            EmitAttack(game, block, game.GameEntity?.GetInt("PROPOSED_DEFENDER") ?? 0);
        }
    }

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        // The parser reports only the local player's choices, whose options the player sees.
        if (!Ready(game))
        {
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
        if (Ready(game) && chosen.CardId.Length > 0)
        {
            Emit($"c {chosen.CardId}");
        }
    }

    public void OnGameEnded(GameState game)
    {
        if (Ready(game) && game.LocalPlayerEntity?.GetString(GameTag.PlayState) is { Length: > 0 } result)
        {
            Emit($"R {Token(result)}");
        }
    }

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
        if (!game.IsConstructedLike)
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

        foreach (var entity in game.Entities.Values.OrderBy(e => e.Id).ToList())
        {
            Sync(game, entity, created: false);
        }

        return true;
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
        }
        else if (tag == "PROPOSED_DEFENDER" && _blocks.LastOrDefault(b => b.Type == "ATTACK") is { Emitted: false } attack
                 && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var defender) && defender > 0)
        {
            EmitAttack(game, attack, defender);
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

        _shown.TryGetValue(entity.Id, out var shown);
        var zone = entity.Zone;
        var controller = entity.Controller;
        if (zone is not ("PLAY" or "GRAVEYARD" or "HAND" or "DECK" or "SECRET") || controller == 0 || !game.Players.ContainsKey(controller))
        {
            Remove(shown, entity.Id);
            return;
        }

        var side = controller == game.LocalPlayerId ? 'L' : 'O';
        var hidden = zone == "DECK"
                     || (side == 'O' && zone == "HAND")
                     || (side == 'O' && zone == "SECRET" && entity.Has(GameTag.Secret));
        if (!hidden && !TableTypes.Contains(entity.CardType))
        {
            // Not drawable yet (a card whose type arrives on a later line) or never (a Battlegrounds-only type).
            Remove(shown, entity.Id);
            return;
        }

        var card = hidden || entity.CardId.Length == 0 ? "-" : entity.CardId;
        var position = zone == "DECK" ? 0 : entity.ZonePosition;
        if (shown is not null)
        {
            if (shown.Zone == zone && shown.Side == side && shown.Hidden == hidden)
            {
                if (shown.Card != card)
                {
                    // Transformed or revealed in place (face up only: a hidden card's id is always "-").
                    shown.Card = card;
                    shown.Position = position;
                    EmitEntity(game, entity, shown, origin: null);
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
                EmitEntity(game, entity, shown, origin: null);
                AfterMove(game, entity);
                return;
            }
        }

        // New on the table, or crossing a hidden zone: a new replay id, so nothing links it to what it was.
        var from = shown?.Zone;
        Remove(shown, entity.Id);
        var fresh = new Shown(++_nextId) { Zone = zone, Side = side, Position = position, Card = card, Hidden = hidden };
        _shown[entity.Id] = fresh;
        var origin = side == 'O' && zone == "HAND" ? OriginOf(game, entity, from, created) : null;
        EmitEntity(game, entity, fresh, origin);
        AfterMove(game, entity);
    }

    /// <summary>How a card back got into the opponent's hand, as the player saw it happen.</summary>
    private static string OriginOf(GameState game, Entity entity, string? from, bool created)
    {
        var opening = game.InOpeningHand(entity.Controller);
        // The Coin is handed to the second player on screen at the end of the mulligan.
        if (opening && (entity.CardId == "GAME_005" || entity.GetInt("COIN_CARD") == 1))
        {
            return "coin";
        }

        return from switch
        {
            "DECK" => opening ? "start" : "drawn",
            "PLAY" or "GRAVEYARD" or "SECRET" => "returned",
            "HAND" => "stolen",
            // Picked up mid-deal (the local player became known after it): the opening hand.
            null when !created && opening => "start",
            _ => "created",
        };
    }

    private void AfterMove(GameState game, Entity entity)
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
                EmitPlay(game, block);
            }
        }
    }

    private void EmitEntity(GameState game, Entity entity, Shown shown, string? origin)
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
        Emit(string.Create(CultureInfo.InvariantCulture, $"X {shown.Id}"));
    }

    private void EmitPlay(GameState game, Pending block)
    {
        if (block.Source is not { } source || !_shown.TryGetValue(source.Id, out var shown))
        {
            return;
        }

        block.Emitted = true;
        Emit($"P {shown.Id} {FaceUpId(block.Target)}");
    }

    private void EmitAttack(GameState game, Pending block, int defender)
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
