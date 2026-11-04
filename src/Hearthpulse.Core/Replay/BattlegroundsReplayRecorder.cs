using System.Globalization;
using System.Text;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Replay;

/// <summary>
/// Records a Battlegrounds game step by step as the local player saw it, as the event lines of a format 4 file
/// (<c>docs/match-replay.md</c>, "Battlegrounds: as seen"). <see cref="ReplayRecorder"/> hands it the game's events once
/// the game is known to be Battlegrounds and the local player is known; it works on the log thread only.
/// <list type="bullet">
/// <item>Bob's offer is written only in the tavern; the combat opponent's hero, hero powers, trinkets and board only
/// during the combat with it. Nobody's hand but ours, no other player's tavern, no card set aside but the options of our
/// own choices (as card ids of the <c>C</c> line).</item>
/// <item>No names, BattleTags, account ids or ratings: cards by card id, the leaderboard by hero card id.</item>
/// <item>A step is one action in the tavern (a block of ours: buy, sell, play, hero power, refresh, upgrade, freeze, move,
/// or a trigger of a card on our side) or one block in combat (an attack, a deaths block, a card's trigger). Blocks of
/// the game, a player or an enchantment are no step; nested blocks belong to the step around them. A step writes what
/// the block changed in the end, not every tag change.</item>
/// </list>
/// </summary>
public sealed partial class BattlegroundsReplayRecorder
{
    /// <summary>A game stops recording after this many events; the file says it is truncated.</summary>
    public const int MaxEvents = 100_000;

    /// <summary>A combat stops adding steps after this many; its end still shows, and the file says it is truncated.</summary>
    public const int MaxCombatSteps = 800;

    private const string PlaceholderHero = "TB_BaconShop_HERO_PH";

    /// <summary>Tags of a shown card: stats, golden, keywords, frozen, tavern tier.</summary>
    private static readonly HashSet<string> CardTags = new(StringComparer.Ordinal)
    {
        GameTag.Atk, GameTag.Health, GameTag.Damage, GameTag.Armor, GameTag.Premium, GameTag.Taunt, GameTag.DivineShield,
        GameTag.Reborn, GameTag.Poisonous, GameTag.Venomous, GameTag.Windfury, GameTag.MegaWindfury, GameTag.Stealth,
        "FROZEN", GameTag.TechLevel,
    };

    /// <summary><see cref="CardTags"/> in the order a card line writes them.</summary>
    private static readonly string[] CardTagOrder = CardTags.Order(StringComparer.Ordinal).ToArray();

    /// <summary>Our player tags the replay shows: gold and the tavern tier.</summary>
    private static readonly HashSet<string> PlayerTags = new(StringComparer.Ordinal)
    {
        "RESOURCES", "RESOURCES_USED", "TEMP_RESOURCES", GameTag.PlayerTechLevel,
    };

    /// <summary>Card types shown in play on our side and the combat opponent's.</summary>
    private static readonly HashSet<string> TableTypes = new(StringComparer.Ordinal)
    {
        "HERO", "HERO_POWER", "MINION", "BATTLEGROUND_TRINKET", "BATTLEGROUND_QUEST_REWARD",
    };

    /// <summary>Card types of our hand and of Bob's offer.</summary>
    private static readonly HashSet<string> CardTypes = new(StringComparer.Ordinal) { "MINION", "BATTLEGROUND_SPELL", "SPELL" };

    /// <summary>Empty trinket slots: in play before a trinket is picked, not a card the player has.</summary>
    private static readonly HashSet<string> TrinketSlots = new(StringComparer.Ordinal) { "BG30_Trinket_1st", "BG30_Trinket_2nd" };

    /// <summary>Hero tags the leaderboard shows.</summary>
    private static readonly HashSet<string> LeaderTags = new(StringComparer.Ordinal)
    {
        GameTag.PlayerId, GameTag.PlayerLeaderboardPlace, GameTag.Health, GameTag.Damage, GameTag.Armor, GameTag.PlayerTechLevel, GameTag.Zone,
    };

    /// <summary>Every tag the recording reads on a card or a player; any other tag change is passed over at once.</summary>
    private static readonly HashSet<string> Read = new(StringComparer.Ordinal)
    {
        GameTag.Zone, GameTag.Controller, GameTag.ZonePosition, GameTag.CardType, GameTag.PlayerId, GameTag.Defending,
        GameTag.PlayerLeaderboardPlace, GameTag.PlayerTechLevel, GameTag.PlayState, "RESOURCES", "RESOURCES_USED", "TEMP_RESOURCES",
        GameTag.Atk, GameTag.Health, GameTag.Damage, GameTag.Armor, GameTag.Premium, GameTag.Taunt, GameTag.DivineShield,
        GameTag.Reborn, GameTag.Poisonous, GameTag.Venomous, GameTag.Windfury, GameTag.MegaWindfury, GameTag.Stealth,
        "FROZEN", GameTag.TechLevel,
    };

    private readonly List<string> _events = new();

    // Lines since the last step line, written when the next one comes. A tag line is kept once per card and tag, with its
    // last value: until the next step nothing shows the values in between.
    private readonly List<string?> _pending = new();
    private readonly Dictionary<(int Id, string Tag), int> _tagLines = new();
    private readonly Dictionary<int, int> _cardLines = new();
    private readonly Dictionary<int, List<int>> _cardPending = new();
    private readonly List<string> _deferred = new();
    private readonly Dictionary<int, Shown> _shown = new();

    // Entity id -> its last replay id, also once it left view: a dead minion's deathrattle names it, a sale names the
    // minion sold.
    private readonly Dictionary<int, int> _ids = new();
    private readonly HashSet<int> _heroes = new();
    private readonly Dictionary<int, string> _leaders = new();

    // Bob's offer when the combat started: not the opponent's, whatever the game does with it during the fight; what is
    // still in Bob's play after it (frozen) is the next tavern's offer again.
    private readonly HashSet<int> _offer = new();
    private GameState? _game;
    private Step? _step;
    private OpenChoice? _choice;

    // The parser's block depth that is the log's top level. Real logs leave some blocks without their BLOCK_END (a play
    // with a choice or a minion's activated power), so the parser's stack keeps them: the base moves up past them where
    // the log is at its top level for sure (a player's play or move, a game step other than MAIN_END).
    private int _base;
    private int _nextId;
    private bool _combat;
    private int _combatSteps;
    private int _tavernTurn;
    private bool _leadersDirty;
    private bool _ended;
    private bool _resultWritten;
    private bool _looseMove;
    private bool _died;

    // The recording began during a combat (a reconnect): Bob's offer was never seen leaving, only a frozen card stays.
    private bool _joinedCombat;

    // How the combat ends: the hero attack after the fight is its damage (its block may be nested in the game's own).
    private int _localHeroAtStart;
    private Entity? _opponentHero;

    /// <summary>Duos: our hero and our team when the combat started (<see cref="PartnerOnTable"/>).</summary>
    private int _ownHeroId;

    private int _ownTeam;
    private HeroAttack? _heroAttack;
    private string? _fightResult;
    private int _taken;
    private int _dealt;

    /// <summary>
    /// The events recorded for the current game, in order, the current step's lines included. Builds a list while a step
    /// is open: for saving and tests, not for frequent calls.
    /// </summary>
    public IReadOnlyList<string> Events => _pending.Count == 0 ? _events : _events.Concat(_pending.OfType<string>()).ToList();

    /// <summary>True when the game hit <see cref="MaxEvents"/> or a combat hit <see cref="MaxCombatSteps"/>.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Our hero powers now, as card ids.</summary>
    public IReadOnlyList<string> HeroPowers => Ours("PLAY", "HERO_POWER");

    /// <summary>Our trinkets, quest and quest reward now, as card ids.</summary>
    public IReadOnlyList<string> Trinkets => Ours("PLAY", "BATTLEGROUND_TRINKET", "BATTLEGROUND_QUEST_REWARD").Concat(Ours("SECRET")).ToList();

    /// <summary>Our hand now, as card ids, left to right.</summary>
    public IReadOnlyList<string> Hand => Ours("HAND");

    /// <summary>
    /// The file text of the current game: the events, the final place once the game is over for the player, then the
    /// rounds and lobby.
    /// </summary>
    public string Text(ReplayHeader header, BattlegroundsReplayData data)
    {
        // Pending lines are written at game end; a game saved before it (left mid-game) keeps them too.
        var events = _events.Concat(_pending.OfType<string>()).ToList();
        if (!_resultWritten && _game is not null && Finished(_game))
        {
            // Saved before the game's end: the leaderboard as it is now (later rows win), then the place.
            events.AddRange(LeaderLines(_game).Select(l => l.Line));
            events.Add(ResultLine(_game));
        }

        return ReplayFile.WriteBattlegrounds(header with { Truncated = Truncated }, data, events);
    }

    internal void Reset()
    {
        _events.Clear();
        _pending.Clear();
        _tagLines.Clear();
        _cardLines.Clear();
        _cardPending.Clear();
        _deferred.Clear();
        _shown.Clear();
        _offer.Clear();
        _ids.Clear();
        _heroes.Clear();
        _leaders.Clear();
        _game = null;
        _step = null;
        _choice = null;
        _base = 0;
        _nextId = 0;
        _combat = false;
        _combatSteps = 0;
        _tavernTurn = 0;
        _leadersDirty = false;
        _ended = false;
        _resultWritten = false;
        _looseMove = false;
        _died = false;
        _joinedCombat = false;
        _opponentHero = null;
        _heroAttack = null;
        _fightResult = null;
        Truncated = false;
    }

    /// <summary>The game is a Battlegrounds game with a known local player: writes what is on screen now.</summary>
    internal void Start(GameState game)
    {
        _game = game;
        if (game.Turn > 0)
        {
            if (InCombat(game))
            {
                BeginCombat(game);
                _joinedCombat = true;
            }
            else
            {
                Tavern(game);
            }
        }

        if (game.LocalPlayerEntity is { } player)
        {
            foreach (var tag in PlayerTags.Order(StringComparer.Ordinal))
            {
                if (player.GetString(tag) is { Length: > 0 } value && value != "0")
                {
                    PendingTag(-1, tag, $"V L {tag} {Token(value)}");
                }
            }
        }

        foreach (var entity in game.Entities.Values.OrderBy(e => e.Id).ToList())
        {
            NoteHero(entity);
            Sync(game, entity);
        }
    }

    internal void OnEntityCreated(GameState game, Entity entity) => OnEntity(game, entity);

    internal void OnEntityRevealed(GameState game, Entity entity) => OnEntity(game, entity);

    internal void OnTagChanged(GameState game, Entity entity, string tag, string value)
    {
        if (_ended)
        {
            // Only the leaderboard still counts: the game sets our final place after PLAYSTATE.
            if (LeaderTags.Contains(tag) && entity.CardType == "HERO")
            {
                NoteHero(entity);
            }

            return;
        }

        if (entity.Id == game.GameEntityId)
        {
            OnGameTag(game, tag, value);
            return;
        }

        if (!Read.Contains(tag))
        {
            return;
        }

        if (entity.CardType == "PLAYER" && game.PlayerByEntity(entity.Id) is { } player)
        {
            OnPlayerTag(game, player, tag, value);
            return;
        }

        if (LeaderTags.Contains(tag) && entity.CardType == "HERO")
        {
            NoteHero(entity);
        }

        if (tag == GameTag.Defending && value == "1" && _step is { Kind: "ATTACK", Target: 0 } attack)
        {
            attack.Target = entity.Id;
        }

        if (tag is GameTag.Zone or GameTag.Controller or GameTag.ZonePosition or GameTag.CardType or GameTag.PlayerId)
        {
            if (tag == GameTag.ZonePosition && game.OpenBlocks.Count <= _base)
            {
                LooseMove(game, entity);
            }

            Sync(game, entity);
            return;
        }

        if (CardTags.Contains(tag) && _shown.TryGetValue(entity.Id, out var shown))
        {
            PendingTag(shown.Id, tag, $"V {shown.Id} {tag} {Token(value)}");
        }
    }

    internal void OnBlockStart(GameState game, string blockType, Entity? source)
    {
        if (_ended)
        {
            return;
        }

        var depth = game.OpenBlocks.Count;
        if (blockType is "PLAY" or "MOVE_MINION")
        {
            // A player's play or move is never nested.
            TopLevel(game);
        }

        if (_combat && blockType == "ATTACK" && source is { CardType: "HERO" } hero && _shown.TryGetValue(hero.Id, out var attacker))
        {
            // Our Duos partner's hero attacking wins the fight for our team too.
            var ours = attacker.Side is 'L' or 'P';
            var defender = ours ? _opponentHero : game.LocalHero;
            _heroAttack = new HeroAttack(ours, defender, Durability(defender), depth);
        }

        if (depth > _base)
        {
            // A nested block is part of the step around it.
            return;
        }

        EndStep(game);
        _step = StepOf(game, blockType, source);
        if (_step is not null)
        {
            _step.Depth = depth;
            // What came before belongs to the step before.
            Flush();
        }
    }

    internal void OnBlockEnd(GameState game)
    {
        var depth = game.OpenBlocks.Count;
        _base = Math.Min(_base, depth);
        if (_heroAttack is { } attack && depth <= attack.Depth)
        {
            _heroAttack = null;
            var damage = Math.Max(0, attack.Before - Durability(attack.Defender));
            _fightResult = attack.Ours ? "WIN" : "LOSS";
            if (attack.Ours)
            {
                _dealt += damage;
            }
            else
            {
                _taken += damage;
            }
        }

        if (!_ended && _step is { } step && depth <= step.Depth)
        {
            EndStep(game);
        }
    }

    /// <summary>
    /// Our choice is kept open until we pick: the options can change meanwhile (a hero reroll is a CHANGE_ENTITY of the
    /// option), so the <c>C</c> line is written at the pick with the cards offered then. A choice the log never shows us
    /// picking (the turn ran out) is written without a pick when the next choice comes, the combat starts or the game ends.
    /// </summary>
    internal void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        WriteOpenChoice(game);
        _choice = _ended || options.Count == 0 ? null : new OpenChoice(choiceType, source, options.ToArray());
    }

    private void WriteOpenChoice(GameState game)
    {
        if (_choice is { } open && !_ended)
        {
            _choice = null;
            WriteChoice(game, open);
        }
    }

    internal void OnChosen(GameState game, Entity chosen)
    {
        if (_ended || chosen.CardId.Length == 0)
        {
            return;
        }

        if (_choice is { } choice)
        {
            _choice = null;
            WriteChoice(game, choice);
        }

        var line = $"c {chosen.CardId}";
        if (_deferred.Count > 0)
        {
            _deferred.Add(line);
        }
        else
        {
            Pending(line);
        }
    }

    private void WriteChoice(GameState game, OpenChoice choice)
    {
        var ids = choice.Options.Select(o => o.CardId).Where(id => id.Length > 0).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var kind = choice.Type == "MULLIGAN" ? "HERO"
            : choice.Options.All(o => o.CardType == "BATTLEGROUND_TRINKET") ? "TRINKET"
            : choice.Options.All(o => o.Has("QUEST")) ? "QUEST"
            : "DISCOVER";
        var from = choice.Source is { CardId.Length: > 0 } s && s.Controller == game.LocalPlayerId ? s.CardId : "-";
        var line = $"C {kind} {from} {string.Join(',', ids)}";
        if (_step is not null)
        {
            // Picked inside a step (a Discover spell): the choice is the step after it.
            _deferred.Add(line);
        }
        else
        {
            Marker(line);
        }
    }

    internal void OnGameEnded(GameState game)
    {
        WriteOpenChoice(game);
        if (!Finished(game))
        {
            // Left mid-game: what was seen is kept as it is, an open combat without an end.
            if (!_ended)
            {
                EndStep(game);
            }

            Flush();
            _ended = true;
            return;
        }

        Finish(game);
        if (!_resultWritten)
        {
            _resultWritten = true;
            WriteLeadersNow(game);
            Write(ResultLine(game));
        }
    }

    /// <summary>The log is at its top level: a step still open lost its BLOCK_END and ends here.</summary>
    private void TopLevel(GameState game)
    {
        EndStep(game);
        _base = game.OpenBlocks.Count;
    }

    private void EndStep(GameState game)
    {
        if (_step is not { } step)
        {
            return;
        }

        _step = null;

        // A trigger that changed nothing the player sees is no step, nor a deaths block where nothing shown died.
        if (!step.Lazy || (step.Kind == "DEATHS" ? _died : _pending.Count > 0))
        {
            WriteStep(game, step);
        }

        if (_deferred.Count > 0)
        {
            // A choice offered inside the step: the step after it.
            Flush();
            foreach (var line in _deferred)
            {
                Write(line);
            }

            _deferred.Clear();
        }
    }
}
