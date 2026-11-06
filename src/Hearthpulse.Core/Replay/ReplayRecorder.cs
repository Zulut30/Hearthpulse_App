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
public sealed partial class ReplayRecorder : IPowerListener
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
}
