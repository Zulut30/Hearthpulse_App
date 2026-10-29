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
public sealed class BattlegroundsReplayRecorder
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

    // Bob's offer when the combat started: not the opponent's, whatever the game does with it during the fight.
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

    // How the combat ends: the hero attack after the fight is its damage (its block may be nested in the game's own).
    private int _localHeroAtStart;
    private Entity? _opponentHero;
    private HeroAttack? _heroAttack;
    private string? _fightResult;
    private int _taken;
    private int _dealt;

    /// <summary>The events recorded for the current game, in order.</summary>
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
            var defender = attacker.Side == 'L' ? _opponentHero : game.LocalHero;
            _heroAttack = new HeroAttack(attacker.Side == 'L', defender, Durability(defender), depth);
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
    /// option), so the <c>C</c> line is written at the pick with the cards offered then. A choice never picked is no step.
    /// </summary>
    internal void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        _choice = _ended || options.Count == 0 ? null : new OpenChoice(choiceType, source, options.ToArray());
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

    private void OnEntity(GameState game, Entity entity)
    {
        if (_ended)
        {
            return;
        }

        NoteHero(entity);
        Sync(game, entity);
    }

    private void OnGameTag(GameState game, string tag, string value)
    {
        switch (tag)
        {
            case GameTag.Turn when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var turn) && turn % 2 == 1:
                // An odd game turn is a tavern: a combat still open never got its end signal.
                EndCombat(game);
                Tavern(game);
                break;
            case GameTag.BoardVisualState when value == "2":
            case GameTag.BaconInCombatPhase when value == "1":
                BeginCombat(game);
                break;
            case GameTag.BoardVisualState when value == "1":
            case GameTag.BaconInCombatPhase when value == "0":
                EndCombat(game);
                break;
            case GameTag.Step when value != "MAIN_END":
                // Printed at the log's top level (MAIN_END sometimes inside a block).
                TopLevel(game);
                if (value == "MAIN_START" && _combat)
                {
                    // Both boards are set up: they belong to the combat's first step.
                    Flush();
                }

                break;
            case "PROPOSED_DEFENDER" when _step is { Kind: "ATTACK", Target: 0 } attack
                                          && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var defender):
                attack.Target = defender;
                break;
        }
    }

    private void OnPlayerTag(GameState game, PlayerSlot player, string tag, string value)
    {
        if (player.PlayerId != game.LocalPlayerId)
        {
            return;
        }

        if (PlayerTags.Contains(tag))
        {
            PendingTag(-1, tag, $"V L {tag} {Token(value)}");
            _leadersDirty |= tag == GameTag.PlayerTechLevel;
        }
        else if (tag == GameTag.PlayState && value is "WON" or "LOST" or "TIED" or "CONCEDED")
        {
            Finish(game);
        }
    }

    /// <summary>The game is over for the player: the open combat ends, nothing more is written but the place.</summary>
    private void Finish(GameState game)
    {
        if (_ended)
        {
            return;
        }

        EndStep(game);
        EndCombat(game);
        Flush();
        _ended = true;
    }

    private static bool Finished(GameState game) =>
        game.LocalPlayerEntity?.GetString(GameTag.PlayState) is "WON" or "LOST" or "TIED" or "CONCEDED";

    private static string ResultLine(GameState game) =>
        string.Create(CultureInfo.InvariantCulture, $"R {game.LocalHero?.GetInt(GameTag.PlayerLeaderboardPlace) ?? 0}");

    private static bool InCombat(GameState game) =>
        game.GameEntity is { } entity && (entity.GetInt(GameTag.BaconInCombatPhase) == 1 || entity.GetInt(GameTag.BoardVisualState) == 2);

    private void Tavern(GameState game)
    {
        if (_tavernTurn == game.BattlegroundsTurn)
        {
            // Already open (recording started on the TURN line itself).
            return;
        }

        _tavernTurn = game.BattlegroundsTurn;
        EndStep(game);
        Marker(string.Create(CultureInfo.InvariantCulture, $"T {game.BattlegroundsTurn} {Seconds(game)}"));
    }

    private void BeginCombat(GameState game)
    {
        if (_combat || _ended)
        {
            return;
        }

        EndStep(game);
        Marker(string.Create(CultureInfo.InvariantCulture, $"F {game.BattlegroundsTurn} {Seconds(game)}"));
        _combat = true;
        _combatSteps = 0;
        _localHeroAtStart = Durability(game.LocalHero);
        _opponentHero = null;
        _heroAttack = null;
        _fightResult = null;
        _taken = _dealt = 0;

        // Bob's offer is not on screen in combat.
        foreach (var (entityId, shown) in _shown.Where(s => s.Value.Side == 'B').ToList())
        {
            _offer.Add(entityId);
            Remove(shown, entityId, died: false);
        }
    }

    /// <summary>
    /// Ends the open combat: the end line comes before what the game cleared up after the fight, so the combat's last
    /// step shows the boards as the fight left them.
    /// </summary>
    private void EndCombat(GameState game)
    {
        if (!_combat)
        {
            return;
        }

        EndStep(game);
        // Without a hero attack nobody was left standing (a tie), unless our hero lost health some other way.
        var taken = _fightResult is null ? Math.Max(0, _localHeroAtStart - Durability(game.LocalHero)) : _taken;
        var result = _fightResult ?? (taken > 0 ? "LOSS" : "TIE");
        WriteLeadersNow(game);
        Write(string.Create(CultureInfo.InvariantCulture, $"f {result} {taken} {_dealt}"));
        _combat = false;
        Flush();
        foreach (var (entityId, shown) in _shown.Where(s => s.Value.Side == 'O').ToList())
        {
            Remove(shown, entityId, died: false);
        }

        _opponentHero = null;
        _offer.Clear();
    }

    private static int Durability(Entity? hero) => hero is null ? 0 : hero.Health + hero.GetInt(GameTag.Armor);

    /// <summary>The step a top-level block is, or null when it is none (its changes belong to the step before).</summary>
    private Step? StepOf(GameState game, string blockType, Entity? source)
    {
        if (_combat && blockType == "DEATHS")
        {
            // The game's own block, the one that names who died.
            return new Step("DEATHS", null, lazy: true);
        }

        if (source is null || source.CardType is "GAME" or "PLAYER" or "ENCHANTMENT")
        {
            return null;
        }

        var ours = source.Controller == game.LocalPlayerId;
        if (_combat)
        {
            var seen = _shown.TryGetValue(source.Id, out var shown) ? shown : null;
            return blockType switch
            {
                "ATTACK" when seen is not null => new Step("ATTACK", source, lazy: false),
                "TRIGGER" or "POWER" when TableTypes.Contains(source.CardType) && (seen is not null || _ids.ContainsKey(source.Id))
                                          && (ours || source.Controller == game.OpponentPlayerId) => new Step("TRIGGER", source, lazy: true),
                _ => null,
            };
        }

        return (blockType, source.CardId) switch
        {
            ("PLAY", "TB_BaconShop_DragBuy" or "TB_BaconShop_DragBuy_Spell") => new Step("BUY", source, lazy: false),
            ("PLAY", "TB_BaconShop_DragSell") => new Step("SELL", source, lazy: false),
            ("PLAY", "TB_BaconShop_8p_Reroll_Button") => new Step("REFRESH", null, lazy: false),
            ("PLAY", "TB_BaconShopLockAll_Button") => new Step("FREEZE", null, lazy: false),
            ("TRIGGER", "TB_BaconShopLockAll_Button") => new Step("FREEZE", null, lazy: true),
            ("PLAY", var id) when id.StartsWith("TB_BaconShopTechUp", StringComparison.Ordinal) => new Step("UPGRADE", null, lazy: false),
            ("PLAY", _) when ours && source.Zone == "HAND" => new Step("PLAY", source, lazy: false),
            ("PLAY", _) when ours && source.CardType == "HERO_POWER" => new Step("POWER", source, lazy: false),
            // A minion of ours on the table used (an activated power, often aimed at a card in hand).
            ("PLAY", _) when ours && source.Zone == "PLAY" && source.IsMinion => new Step("USE", source, lazy: false),
            ("MOVE_MINION", _) when ours && source.IsMinion => new Step("MOVE", source, lazy: false),
            ("TRIGGER" or "POWER", _) when ours && _shown.ContainsKey(source.Id) && TableTypes.Contains(source.CardType) => new Step("TRIGGER", source, lazy: true),
            _ => null,
        };
    }

    private void WriteStep(GameState game, Step step)
    {
        if (_combat && ++_combatSteps > MaxCombatSteps)
        {
            // The fight goes on in the state lines; only its steps stop.
            Truncated = true;
            return;
        }

        // Bought, sold and aimed cards are the source button's CARD_TARGET; a played card's target is its own.
        var target = step.Kind is "BUY" or "SELL" or "PLAY" or "POWER" or "USE" ? step.Source?.GetInt("CARD_TARGET") ?? 0 : step.Target;
        var line = step.Kind switch
        {
            "BUY" or "SELL" => $"M {step.Kind} {IdOf(target)}",
            "REFRESH" or "FREEZE" or "UPGRADE" or "DEATHS" => $"M {step.Kind} -",
            "PLAY" or "POWER" or "USE" or "ATTACK" => $"M {step.Kind} {IdOf(step.Source?.Id ?? 0)} {IdOf(target)}",
            _ => $"M {step.Kind} {IdOf(step.Source?.Id ?? 0)}",
        };
        WriteLeaders(game);
        Write(line);
        Flush();
    }

    /// <summary>A step line: what is pending belongs to the step before it.</summary>
    private void Marker(string line)
    {
        if (_game is { } game)
        {
            WriteLeaders(game);
        }

        Flush();
        Write(line);
        _looseMove = false;
    }

    /// <summary>One of our minions moved outside a block (no MOVE_MINION): a move step, one per run of such lines.</summary>
    private void LooseMove(GameState game, Entity entity)
    {
        // A minion entering play gets its slot after its zone: only a minion leaving a slot it had moves.
        if (_combat || _looseMove || !_shown.TryGetValue(entity.Id, out var shown) || shown is not { Side: 'L', Zone: "PLAY", Type: "MINION" }
            || shown.Position == 0 || shown.Position == entity.ZonePosition || entity.Zone != "PLAY")
        {
            return;
        }

        Marker($"M MOVE {shown.Id}");
        _looseMove = true;
    }

    /// <summary>Brings one entity's replay state up to date with the game, under the rule of what the player sees.</summary>
    private void Sync(GameState game, Entity entity)
    {
        if (entity.Id == game.GameEntityId || entity.CardType is "PLAYER" or "GAME" or "ENCHANTMENT")
        {
            return;
        }

        _shown.TryGetValue(entity.Id, out var shown);
        var side = SideOf(game, entity);
        if (side == '\0')
        {
            if (shown is not null)
            {
                Remove(shown, entity.Id, died: shown.Zone == "PLAY" && entity.Zone == "GRAVEYARD" && shown.Type is "MINION" or "HERO");
            }

            return;
        }

        var zone = entity.Zone;
        var position = entity.ZonePosition;
        if (shown is not null && shown.Side == side && shown.Zone == zone && shown.Card == entity.CardId && shown.Type == entity.CardType)
        {
            if (shown.Position != position)
            {
                shown.Position = position;
                PendingTag(shown.Id, "Z", string.Create(CultureInfo.InvariantCulture, $"Z {shown.Id} {position}"));
            }

            return;
        }

        // Every card shown is face up: one moving (bought, played, transformed) keeps its id.
        shown ??= new Shown(++_nextId);
        shown.Side = side;
        shown.Zone = zone;
        shown.Position = position;
        shown.Card = entity.CardId;
        shown.Type = entity.CardType;
        _shown[entity.Id] = shown;
        _ids[entity.Id] = shown.Id;
        if (side == 'O' && shown.Type == "HERO")
        {
            _opponentHero = entity;
        }

        var line = new StringBuilder(64);
        line.Append(CultureInfo.InvariantCulture, $"E {shown.Id} {side} {zone} {position} {entity.CardId} {Token(entity.CardType)}");
        foreach (var tag in CardTagOrder)
        {
            if (entity.GetString(tag) is { Length: > 0 } value && value != "0")
            {
                line.Append(' ').Append(tag).Append('=').Append(Token(value));
            }
        }

        PendingCard(shown.Id, line.ToString(), full: true);
    }

    /// <summary>Which side shows the entity now: ours (L), Bob's offer (B), the combat opponent's (O), or none.</summary>
    private char SideOf(GameState game, Entity entity)
    {
        var card = entity.CardId;
        if (card.Length == 0 || card == PlaceholderHero || TrinketSlots.Contains(card))
        {
            return '\0';
        }

        var zone = entity.Zone;
        var type = entity.CardType;
        if (entity.Controller == game.LocalPlayerId)
        {
            return zone switch
            {
                "PLAY" when TableTypes.Contains(type) => 'L',
                "HAND" when CardTypes.Contains(type) => 'L',
                "SECRET" when entity.Has("QUEST") => 'L',
                _ => '\0',
            };
        }

        if (entity.Controller != game.OpponentPlayerId || zone != "PLAY")
        {
            return '\0';
        }

        // The innkeeper's slot holds Bob's offer in the tavern and the combat opponent in combat; a hero there is the
        // opponent's only with a leaderboard slot (Bob has none).
        return _combat
            ? TableTypes.Contains(type) && (type != "HERO" || entity.GetInt(GameTag.PlayerId) > 0) && !_offer.Contains(entity.Id) ? 'O' : '\0'
            : CardTypes.Contains(type) ? 'B' : '\0';
    }

    private void Remove(Shown shown, int entityId, bool died)
    {
        _shown.Remove(entityId);
        _died |= died;
        PendingCard(shown.Id, string.Create(CultureInfo.InvariantCulture, $"{(died ? 'Y' : 'X')} {shown.Id}"), full: false);
    }

    private void NoteHero(Entity entity)
    {
        if (entity.CardType == "HERO" && entity.GetInt(GameTag.PlayerId) > 0)
        {
            _heroes.Add(entity.Id);
            _leadersDirty = true;
        }
    }

    /// <summary>
    /// The leaderboard rows that changed: per player slot, the hero the leaderboard shows (the oldest hero entity of the
    /// slot set aside, or our hero; combat copies are newer), its place, health, armor and tavern tier.
    /// </summary>
    private void WriteLeaders(GameState game)
    {
        if (!_leadersDirty)
        {
            return;
        }

        _leadersDirty = false;
        foreach (var (slot, line) in LeaderLines(game))
        {
            _leaders[slot] = line;
            PendingTag(-100 - slot, "L", line);
        }
    }

    /// <summary>
    /// Writes the leaderboard as it is now ahead of a step line that takes the table at once (<c>f</c>, <c>R</c>): a
    /// pending row of the same slot is older and is dropped.
    /// </summary>
    private void WriteLeadersNow(GameState game)
    {
        _leadersDirty = false;
        foreach (var (slot, line) in LeaderLines(game))
        {
            if (_tagLines.Remove((-100 - slot, "L"), out var index))
            {
                _pending[index] = null;
            }

            _leaders[slot] = line;
            Write(line);
        }
    }

    /// <summary>The rows that differ from the ones written.</summary>
    private List<(int Slot, string Line)> LeaderLines(GameState game)
    {
        var local = game.LocalHero;
        var rows = new SortedDictionary<int, Entity>();
        foreach (var id in _heroes)
        {
            if (game.Entities.GetValueOrDefault(id) is not { } hero || hero.CardId.Length == 0 || hero.CardId == PlaceholderHero
                || hero.GetInt(GameTag.PlayerLeaderboardPlace) <= 0 || (hero.Zone != "SETASIDE" && hero != local))
            {
                continue;
            }

            var slot = hero.GetInt(GameTag.PlayerId);
            if (!rows.TryGetValue(slot, out var other) || hero.Id < other.Id)
            {
                rows[slot] = hero;
            }
        }

        var lines = new List<(int Slot, string Line)>();
        foreach (var (slot, hero) in rows)
        {
            var mine = hero == local;
            var tier = hero.GetInt(GameTag.PlayerTechLevel) is > 0 and var t ? t : mine ? game.LocalPlayerEntity?.GetInt(GameTag.PlayerTechLevel) ?? 0 : 0;
            var line = string.Create(CultureInfo.InvariantCulture,
                $"L {slot} {(mine ? 'L' : '-')} {hero.CardId} {hero.GetInt(GameTag.PlayerLeaderboardPlace)} {hero.Health} {hero.GetInt(GameTag.Armor)} {tier}");
            if (_leaders.GetValueOrDefault(slot) != line)
            {
                lines.Add((slot, line));
            }
        }

        return lines;
    }

    private string IdOf(int entityId) =>
        entityId > 0 && _ids.TryGetValue(entityId, out var id) ? id.ToString(CultureInfo.InvariantCulture) : "-";

    private List<string> Ours(params string[] zoneAndTypes)
    {
        var zone = zoneAndTypes[0];
        return _shown.Values
            .Where(s => s.Side == 'L' && s.Zone == zone && (zoneAndTypes.Length == 1 || zoneAndTypes.Skip(1).Contains(s.Type)))
            .OrderBy(s => s.Position).ThenBy(s => s.Id)
            .Select(s => s.Card)
            .ToList();
    }

    private static int Seconds(GameState game) => Math.Max(0, (int)(game.LastActivity - game.StartedAt).TotalSeconds);

    private bool Pending(string line)
    {
        if (_events.Count + _pending.Count >= MaxEvents)
        {
            Truncated = true;
            return false;
        }

        _pending.Add(line);
        return true;
    }

    /// <summary>
    /// A card line: <c>E</c> (<paramref name="full"/>: every tag and the slot, so it replaces the card's pending lines
    /// before it), <c>X</c> or <c>Y</c>. Later tag lines of the card come after it.
    /// </summary>
    private void PendingCard(int id, string line, bool full)
    {
        if (!_cardPending.TryGetValue(id, out var lines))
        {
            _cardPending[id] = lines = new List<int>();
        }

        if (full)
        {
            foreach (var index in lines)
            {
                _pending[index] = null;
            }

            lines.Clear();
        }

        if (Pending(line))
        {
            _cardLines[id] = _pending.Count - 1;
            lines.Add(_pending.Count - 1);
        }
    }

    /// <summary>A tag line: replaces the card's pending line of the same tag unless a card line came since.</summary>
    private void PendingTag(int id, string tag, string line)
    {
        if (_tagLines.TryGetValue((id, tag), out var index) && index > _cardLines.GetValueOrDefault(id, -1) && _pending[index] is not null)
        {
            _pending[index] = line;
            return;
        }

        if (Pending(line))
        {
            _tagLines[(id, tag)] = _pending.Count - 1;
            if (_cardPending.TryGetValue(id, out var lines))
            {
                lines.Add(_pending.Count - 1);
            }
        }
    }

    private void Flush()
    {
        foreach (var line in _pending)
        {
            if (line is not null)
            {
                Write(line);
            }
        }

        _pending.Clear();
        _tagLines.Clear();
        _cardLines.Clear();
        _cardPending.Clear();
        _died = false;
    }

    private void Write(string line)
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

        public char Side { get; set; }

        public string Zone { get; set; } = string.Empty;

        public int Position { get; set; }

        public string Card { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;
    }

    /// <summary>Our choice offered and not picked yet: its option entities, read again at the pick.</summary>
    private sealed record OpenChoice(string Type, Entity? Source, IReadOnlyList<Entity> Options);

    /// <summary>A hero attacking after the fight: the defender's health + armor before it.</summary>
    private sealed record HeroAttack(bool Ours, Entity? Defender, int Before, int Depth);

    /// <summary>An open top-level block that is a step; a lazy one is written only if it changed what is shown.</summary>
    private sealed class Step(string kind, Entity? source, bool lazy)
    {
        public string Kind { get; } = kind;

        public Entity? Source { get; } = source;

        public bool Lazy { get; } = lazy;

        /// <summary>The attack's defender (game entity id), once known.</summary>
        public int Target { get; set; }

        /// <summary>The parser's block depth the step's block started at: it ends when the depth is back there.</summary>
        public int Depth { get; set; }
    }
}
