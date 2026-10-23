using System.Globalization;
using System.Text.RegularExpressions;

namespace Hearthlens.Core.Power;

/// <summary>Receives semantic events while Power.log is replayed into a <see cref="GameState"/>.</summary>
public interface IPowerListener
{
    void OnGameStarted(GameState game) { }

    void OnEntityCreated(GameState game, Entity entity) { }

    void OnEntityRevealed(GameState game, Entity entity) { }

    void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value) { }

    void OnBlockStart(GameState game, string blockType, Entity? source) { }

    void OnBlockEnd(GameState game) { }

    /// <param name="source">The card that offers the choice (hero power, spell, triple reward…), when known.</param>
    void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options) { }

    /// <summary>The local player picked from the open choice.</summary>
    void OnChoiceMade(GameState game) { }

    void OnGameEnded(GameState game) { }
}

/// <summary>
/// Streaming parser for the GameState.DebugPrint* lines of Power.log. Feed lines in order;
/// it rebuilds the full entity graph and notifies listeners.
/// </summary>
public sealed partial class PowerParser
{
    private const int MaxBlockDepth = 64;

    private readonly List<IPowerListener> _listeners = new();
    private DateOnly _date;
    private TimeSpan _lastTime = TimeSpan.MinValue;

    private Entity? _tagTarget;
    private bool _tagTargetIsNew;
    private bool _tagTargetRevealed;

    private string? _choiceType;
    private bool _choiceIsLocal;
    private Entity? _choiceSource;
    private readonly List<Entity> _choiceOptions = new();

    public PowerParser(DateOnly date)
    {
        _date = date;
    }

    public GameState? Game { get; private set; }

    public DateTime LastTimestamp { get; private set; }

    public void AddListener(IPowerListener listener) => _listeners.Add(listener);

    public void Feed(string line)
    {
        var match = LineRegex().Match(line);
        if (!match.Success)
        {
            TrackCombatOnScreen(line);
            return;
        }

        UpdateClock(match.Groups["time"].ValueSpan);
        var method = match.Groups["method"].Value;
        var body = match.Groups["body"].Value.Trim();

        if (method != "DebugPrintEntityChoices")
        {
            FlushChoices();
        }

        switch (method)
        {
            case "DebugPrintPower":
                HandlePower(body);
                break;
            case "DebugPrintGame":
                FlushTagTarget();
                HandleGameInfo(body);
                break;
            case "DebugPrintEntityChoices":
                FlushTagTarget();
                HandleChoices(body);
                break;
            case "DebugPrintEntitiesChosen":
                FlushTagTarget();
                HandleChosen(body);
                break;
        }

        if (Game is not null)
        {
            Game.LastActivity = LastTimestamp;
        }
    }

    /// <summary>Completes pending multi-line constructs (call after a batch of lines).</summary>
    public void Flush()
    {
        FlushTagTarget();
        FlushChoices();
    }

    /// <summary>
    /// Drops the current game without ending it. Used when the same game is replayed from its CREATE_GAME line
    /// (hard refresh), so the replayed lines rebuild the state instead of being mixed with the old one.
    /// </summary>
    public void ResetGame()
    {
        Flush();
        Game = null;
        _tagTarget = null;
        _choiceType = null;
        _choiceOptions.Clear();
    }

    /// <summary>Ends the current game if it never reached STATE=COMPLETE (log ended / client closed).</summary>
    public void AbandonCurrentGame()
    {
        Flush();
        if (Game is { IsComplete: false } game)
        {
            game.EndedAt = LastTimestamp;
            foreach (var listener in _listeners) listener.OnGameEnded(game);
        }
    }

    private const string OnScreenBoardState = "Entity=GameEntity tag=BOARD_VISUAL_STATE value=";

    /// <summary>Follows the board state the client is showing (2 = combat) from its PowerTaskList replay.</summary>
    private void TrackCombatOnScreen(string line)
    {
        if (Game is null)
        {
            return;
        }

        var at = line.IndexOf(OnScreenBoardState, StringComparison.Ordinal);
        if (at < 0 || !line.Contains("PowerTaskList.", StringComparison.Ordinal))
        {
            return;
        }

        var onScreen = line.AsSpan(at + OnScreenBoardState.Length).Trim() is "2";
        Game.CombatReplaySeen = true;
        if (onScreen && !Game.CombatOnScreen)
        {
            Game.CombatOnScreenSince = Environment.TickCount64;
        }

        Game.CombatOnScreen = onScreen;
    }

    private void UpdateClock(ReadOnlySpan<char> text)
    {
        if (!TimeSpan.TryParseExact(text.Length > 16 ? text[..16] : text, @"hh\:mm\:ss\.FFFFFFF", CultureInfo.InvariantCulture, out var time))
        {
            return;
        }

        if (_lastTime != TimeSpan.MinValue && time < _lastTime - TimeSpan.FromHours(12))
        {
            _date = _date.AddDays(1);
        }

        _lastTime = time;
        LastTimestamp = _date.ToDateTime(TimeOnly.FromTimeSpan(time));
    }

    private void HandlePower(string body)
    {
        if (body.StartsWith("tag=", StringComparison.Ordinal))
        {
            var tag = TagValueRegex().Match(body);
            if (tag.Success && _tagTarget is not null)
            {
                ApplyTag(_tagTarget, tag.Groups["tag"].Value, tag.Groups["value"].Value, silent: _tagTargetIsNew);
            }

            return;
        }

        FlushTagTarget();

        if (body.StartsWith("TAG_CHANGE ", StringComparison.Ordinal))
        {
            var m = TagChangeRegex().Match(body);
            if (m.Success && Game is not null)
            {
                var entity = Resolve(m.Groups["entity"].Value);
                if (entity is not null)
                {
                    ApplyTag(entity, m.Groups["tag"].Value, m.Groups["value"].Value, silent: false);
                }
            }

            return;
        }

        if (body.StartsWith("BLOCK_START ", StringComparison.Ordinal))
        {
            var m = BlockStartRegex().Match(body);
            if (m.Success && Game is { } game)
            {
                var source = Resolve(m.Groups["entity"].Value);
                var type = m.Groups["type"].Value;
                foreach (var listener in _listeners) listener.OnBlockStart(game, type, source);
                // Pushed after notifying, so listeners see the blocks around the one starting.
                if (game.BlockStack.Count >= MaxBlockDepth)
                {
                    // Unbalanced log (e.g. read from the middle of a game): start over rather than grow forever.
                    game.BlockStack.Clear();
                }

                game.BlockStack.Add(new PowerBlock(type, source));
            }

            return;
        }

        if (body.StartsWith("BLOCK_END", StringComparison.Ordinal))
        {
            if (Game is { } game)
            {
                if (game.BlockStack.Count > 0)
                {
                    game.BlockStack.RemoveAt(game.BlockStack.Count - 1);
                }

                foreach (var listener in _listeners) listener.OnBlockEnd(game);
            }

            return;
        }

        if (body.StartsWith("FULL_ENTITY - ", StringComparison.Ordinal))
        {
            HandleFullEntity(body);
            return;
        }

        if (body.StartsWith("SHOW_ENTITY - ", StringComparison.Ordinal) || body.StartsWith("CHANGE_ENTITY - ", StringComparison.Ordinal))
        {
            var m = ShowEntityRegex().Match(body);
            if (m.Success && Game is not null && Resolve(m.Groups["entity"].Value) is { } entity)
            {
                entity.CardId = m.Groups["card"].Value;
                entity.WasRevealed = true;
                _tagTarget = entity;
                _tagTargetIsNew = false;
                _tagTargetRevealed = true;
            }

            return;
        }

        if (body.StartsWith("HIDE_ENTITY - ", StringComparison.Ordinal))
        {
            var m = HideEntityRegex().Match(body);
            if (m.Success && Game is not null && Resolve(m.Groups["entity"].Value) is { } entity)
            {
                ApplyTag(entity, m.Groups["tag"].Value, m.Groups["value"].Value, silent: false);
            }

            return;
        }

        if (body == "CREATE_GAME")
        {
            StartGame();
            return;
        }

        if (body.StartsWith("GameEntity EntityID=", StringComparison.Ordinal) && Game is { } g1)
        {
            // Only the leading digits: text the client may append after the id must not throw.
            var digits = body.AsSpan("GameEntity EntityID=".Length);
            var end = 0;
            while (end < digits.Length && char.IsAsciiDigit(digits[end]))
            {
                end++;
            }

            if (!int.TryParse(digits[..end], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return;
            }

            g1.GameEntityId = id;
            var entity = g1.GetOrCreate(id);
            entity.Name = "GameEntity";
            BeginCreation(entity);
            return;
        }

        if (body.StartsWith("Player EntityID=", StringComparison.Ordinal) && Game is { } g2)
        {
            var m = PlayerRegex().Match(body);
            if (m.Success)
            {
                var entityId = int.Parse(m.Groups["entity"].Value, CultureInfo.InvariantCulture);
                var playerId = int.Parse(m.Groups["player"].Value, CultureInfo.InvariantCulture);
                g2.Players[playerId] = new PlayerSlot
                {
                    PlayerId = playerId,
                    EntityId = entityId,
                    AccountHi = ulong.Parse(m.Groups["hi"].Value, CultureInfo.InvariantCulture),
                    AccountLo = ulong.Parse(m.Groups["lo"].Value, CultureInfo.InvariantCulture),
                };
                BeginCreation(g2.GetOrCreate(entityId));
            }
        }
    }

    private void HandleFullEntity(string body)
    {
        if (Game is not { } game)
        {
            return;
        }

        var creating = FullEntityCreateRegex().Match(body);
        if (creating.Success)
        {
            var id = int.Parse(creating.Groups["id"].Value, CultureInfo.InvariantCulture);
            var isNew = !game.Entities.ContainsKey(id);
            var entity = game.GetOrCreate(id);
            entity.CardId = creating.Groups["card"].Value;
            if (isNew)
            {
                BeginCreation(entity);
            }
            else
            {
                _tagTarget = entity;
                _tagTargetIsNew = false;
            }

            return;
        }

        var updating = ShowEntityRegex().Match(body);
        if (updating.Success && Resolve(updating.Groups["entity"].Value) is { } existing)
        {
            existing.CardId = updating.Groups["card"].Value;
            _tagTarget = existing;
            _tagTargetIsNew = false;
        }
    }

    private void BeginCreation(Entity entity)
    {
        _tagTarget = entity;
        _tagTargetIsNew = true;
        _tagTargetRevealed = false;
    }

    private void FlushTagTarget()
    {
        if (_tagTarget is null || Game is not { } game)
        {
            _tagTarget = null;
            return;
        }

        var entity = _tagTarget;
        var wasNew = _tagTargetIsNew;
        var revealed = _tagTargetRevealed;
        _tagTarget = null;
        _tagTargetIsNew = false;
        _tagTargetRevealed = false;

        if (wasNew)
        {
            entity.CreatedTurn = game.Turn;
            entity.InitialZone = entity.Zone;
            entity.InitialController = entity.Controller;
            if (entity.InHand)
            {
                entity.EnteredHandTurn = game.Turn;
            }

            game.TryResolveLocalPlayer();
            foreach (var listener in _listeners) listener.OnEntityCreated(game, entity);
        }
        else if (revealed)
        {
            game.TryResolveLocalPlayer();
            foreach (var listener in _listeners) listener.OnEntityRevealed(game, entity);
        }
    }

    private void ApplyTag(Entity entity, string tag, string value, bool silent)
    {
        var game = Game!;
        var previous = entity.SetTag(tag, value);
        if (silent || previous == value)
        {
            return;
        }

        if (tag == GameTag.Zone)
        {
            if (value == "HAND")
            {
                entity.EnteredHandTurn = game.Turn;
            }
            else if (previous == "HAND")
            {
                entity.LeftHandTurn = game.Turn;
                if (value == "DECK" && game.Turn == 0)
                {
                    entity.Mulliganed = true;
                }
            }
        }

        foreach (var listener in _listeners) listener.OnTagChanged(game, entity, tag, previous, value);

        if (tag == GameTag.State && value == "COMPLETE" && entity.Id == game.GameEntityId && !game.IsComplete)
        {
            game.EndedAt = LastTimestamp;
            foreach (var listener in _listeners) listener.OnGameEnded(game);
        }
    }

    private void StartGame()
    {
        if (Game is { IsComplete: false })
        {
            AbandonCurrentGame();
        }

        Game = new GameState { StartedAt = LastTimestamp, LastActivity = LastTimestamp };
        foreach (var listener in _listeners) listener.OnGameStarted(Game);
    }

    private void HandleGameInfo(string body)
    {
        if (Game is not { } game)
        {
            return;
        }

        var separator = body.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var key = body[..separator];
        var value = body[(separator + 1)..];
        switch (key)
        {
            case "GameType":
                game.GameType = value;
                game.TryResolveLocalPlayer();
                break;
            case "FormatType":
                game.FormatType = value;
                break;
            case "ScenarioID":
                game.ScenarioId = ParseInt(value);
                break;
            case "BuildNumber":
                game.BuildNumber = ParseInt(value);
                break;
            case "PlayerID":
                var m = PlayerNameRegex().Match(body);
                if (m.Success && game.Players.TryGetValue(ParseInt(m.Groups["id"].Value), out var slot))
                {
                    slot.Name = m.Groups["name"].Value;
                }

                break;
        }
    }

    private void HandleChoices(string body)
    {
        if (Game is not { } game)
        {
            return;
        }

        var header = ChoicesHeaderRegex().Match(body);
        if (header.Success)
        {
            FlushChoices();
            var player = header.Groups["player"].Value;
            game.SetLocalPlayerByName(player);
            _choiceType = header.Groups["type"].Value;
            _choiceIsLocal = game.LocalPlayer?.Name == player;
            _choiceSource = null;
            return;
        }

        if (_choiceType is not null && body.StartsWith("Source=", StringComparison.Ordinal))
        {
            _choiceSource = Resolve(body["Source=".Length..].Trim());
            return;
        }

        var option = ChoiceEntityRegex().Match(body);
        if (option.Success && _choiceType is not null && Resolve(option.Groups["entity"].Value) is { } entity)
        {
            _choiceOptions.Add(entity);
        }
    }

    private void FlushChoices()
    {
        if (_choiceType is null || Game is not { } game)
        {
            _choiceType = null;
            _choiceOptions.Clear();
            return;
        }

        var type = _choiceType;
        var options = _choiceOptions.ToArray();
        var local = _choiceIsLocal;
        var source = _choiceSource;
        _choiceType = null;
        _choiceSource = null;
        _choiceOptions.Clear();
        if (local)
        {
            foreach (var listener in _listeners) listener.OnChoices(game, type, source, options);
        }
    }

    private void HandleChosen(string body)
    {
        var header = ChosenHeaderRegex().Match(body);
        if (Game is { } game && header.Success && game.LocalPlayer?.Name == header.Groups["player"].Value)
        {
            foreach (var listener in _listeners) listener.OnChoiceMade(game);
        }
    }

    /// <summary>Resolves an entity reference: numeric id, [entityName=… id=N …], GameEntity or a player name.</summary>
    private Entity? Resolve(string reference)
    {
        var game = Game;
        if (game is null)
        {
            return null;
        }

        if (int.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return game.GetOrCreate(id);
        }

        if (reference.StartsWith('['))
        {
            var m = EntityRefRegex().Match(reference);
            if (!m.Success)
            {
                return null;
            }

            var entity = game.GetOrCreate(ParseInt(m.Groups["id"].Value));
            var cardId = m.Groups["card"].Value;
            if (cardId.Length > 0 && entity.CardId.Length == 0)
            {
                entity.CardId = cardId;
            }

            var name = m.Groups["name"].Value;
            if (name.Length > 0 && !name.StartsWith("UNKNOWN ENTITY", StringComparison.Ordinal))
            {
                entity.Name = name;
            }

            return entity;
        }

        if (reference == "GameEntity")
        {
            return game.GameEntityId == 0 ? null : game.GetOrCreate(game.GameEntityId);
        }

        var player = game.Players.Values.FirstOrDefault(p => p.Name == reference)
            ?? AdoptUnknownPlayerName(game, reference);
        return player is null ? null : game.GetOrCreate(player.EntityId);
    }

    private static PlayerSlot? AdoptUnknownPlayerName(GameState game, string name)
    {
        // Opponent names can be hidden at game start, and the Battlegrounds dummy player is
        // renamed after each opponent; an unseen name always belongs to the non-local player.
        var candidate = game.Players.Values.FirstOrDefault(p => p.Name is "" or GameState.UnknownPlayerName)
            ?? (game.LocalPlayerId != 0 ? game.OpponentPlayer : null);
        if (candidate is not null && candidate.PlayerId != game.LocalPlayerId)
        {
            candidate.Name = name;
            return candidate;
        }

        return null;
    }

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;

    [GeneratedRegex(@"^[DWEI] (?<time>\d{2}:\d{2}:\d{2}\.\d+) GameState\.(?<method>DebugPrint\w+)\(\) - (?<body>.*)$")]
    private static partial Regex LineRegex();

    [GeneratedRegex(@"^tag=(?<tag>\S+) value=(?<value>\S*)")]
    private static partial Regex TagValueRegex();

    [GeneratedRegex(@"^TAG_CHANGE Entity=(?<entity>.+?) tag=(?<tag>\S+) value=(?<value>\S*)")]
    private static partial Regex TagChangeRegex();

    [GeneratedRegex(@"^BLOCK_START BlockType=(?<type>\w+) Entity=(?<entity>.+?) EffectCardId=")]
    private static partial Regex BlockStartRegex();

    [GeneratedRegex(@"^FULL_ENTITY - Creating ID=(?<id>\d+) CardID=(?<card>\S*)")]
    private static partial Regex FullEntityCreateRegex();

    [GeneratedRegex(@"^\w+_ENTITY - Updating (?:Entity=)?(?<entity>.+?) CardID=(?<card>\S*)\s*$")]
    private static partial Regex ShowEntityRegex();

    [GeneratedRegex(@"^HIDE_ENTITY - Entity=(?<entity>.+?) tag=(?<tag>\S+) value=(?<value>\S*)")]
    private static partial Regex HideEntityRegex();

    [GeneratedRegex(@"^Player EntityID=(?<entity>\d+) PlayerID=(?<player>\d+) GameAccountId=\[hi=(?<hi>\d+) lo=(?<lo>\d+)\]")]
    private static partial Regex PlayerRegex();

    [GeneratedRegex(@"^PlayerID=(?<id>\d+), PlayerName=(?<name>.*)$")]
    private static partial Regex PlayerNameRegex();

    [GeneratedRegex(@"^id=\d+ Player=(?<player>.+?) TaskList=\S* ChoiceType=(?<type>\w+)")]
    private static partial Regex ChoicesHeaderRegex();

    [GeneratedRegex(@"^Entities\[\d+\]=(?<entity>.+)$")]
    private static partial Regex ChoiceEntityRegex();

    [GeneratedRegex(@"^id=\d+ Player=(?<player>.+?) EntitiesCount=")]
    private static partial Regex ChosenHeaderRegex();

    [GeneratedRegex(@"^\[entityName=(?<name>.*?) id=(?<id>\d+) zone=\w+ zonePos=\d+ cardId=(?<card>\S*) player=\d+\]$")]
    private static partial Regex EntityRefRegex();
}
