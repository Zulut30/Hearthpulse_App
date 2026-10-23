namespace Hearthlens.Core.Power;

/// <summary>An open BLOCK_START … BLOCK_END section of Power.log.</summary>
public sealed record PowerBlock(string Type, Entity? Source);

public sealed class PlayerSlot
{
    public int PlayerId { get; init; }

    public int EntityId { get; init; }

    public string Name { get; set; } = string.Empty;

    public ulong AccountHi { get; init; }

    public ulong AccountLo { get; init; }

    public bool HasAccount => AccountHi != 0 || AccountLo != 0;
}

/// <summary>Complete state of one game as reconstructed from Power.log.</summary>
public sealed class GameState
{
    public const string UnknownPlayerName = "UNKNOWN HUMAN PLAYER";

    public Dictionary<int, Entity> Entities { get; } = new();

    public Dictionary<int, PlayerSlot> Players { get; } = new();

    public int GameEntityId { get; internal set; }

    public string GameType { get; internal set; } = string.Empty;

    public string FormatType { get; internal set; } = string.Empty;

    public int ScenarioId { get; internal set; }

    public int BuildNumber { get; internal set; }

    public DateTime StartedAt { get; internal set; }

    public DateTime LastActivity { get; internal set; }

    /// <summary>
    /// True while the client is still playing a Battlegrounds combat on screen. The server resolves the
    /// whole fight in GameState lines within a second or two; the client replays it (PowerTaskList lines)
    /// over the next ~25 seconds.
    /// </summary>
    public bool CombatOnScreen { get; internal set; }

    /// <summary>
    /// When <see cref="CombatOnScreen"/> last became true (Environment.TickCount64). Power.log replay lines are
    /// not guaranteed on every client build; a latch older than the combat screen timeout is treated as stale.
    /// </summary>
    public long CombatOnScreenSince { get; internal set; }

    /// <summary>True once the client's combat replay (PowerTaskList board state) has been seen in this game.</summary>
    public bool CombatReplaySeen { get; internal set; }

    /// <summary>
    /// Power.log blocks enclosing the line being processed, outermost first. During <see cref="IPowerListener.OnBlockStart"/>
    /// and <see cref="IPowerListener.OnBlockEnd"/> it holds the blocks around the one starting or ending.
    /// </summary>
    public IReadOnlyList<PowerBlock> OpenBlocks => BlockStack;

    internal List<PowerBlock> BlockStack { get; } = new();

    public DateTime? EndedAt { get; internal set; }

    public bool IsComplete => EndedAt is not null;

    public int LocalPlayerId { get; internal set; }

    public Entity? GameEntity => Entities.GetValueOrDefault(GameEntityId);

    public int Turn => GameEntity?.GetInt(GameTag.Turn) ?? 0;

    public bool IsBattlegrounds => GameType.Contains("BATTLEGROUNDS", StringComparison.Ordinal);

    public bool IsDuos => GameType.Contains("DUO", StringComparison.Ordinal);

    public bool IsMercenaries => GameType.Contains("MERCENARIES", StringComparison.Ordinal);

    public bool IsConstructedLike => !IsBattlegrounds && !IsMercenaries;

    public PlayerSlot? LocalPlayer => Players.GetValueOrDefault(LocalPlayerId);

    public PlayerSlot? OpponentPlayer =>
        LocalPlayerId == 0 ? null : Players.Values.FirstOrDefault(p => p.PlayerId != LocalPlayerId);

    public int OpponentPlayerId => OpponentPlayer?.PlayerId ?? 0;

    public Entity? LocalPlayerEntity => LocalPlayer is { } p ? Entities.GetValueOrDefault(p.EntityId) : null;

    public Entity? OpponentPlayerEntity => OpponentPlayer is { } p ? Entities.GetValueOrDefault(p.EntityId) : null;

    public Entity? LocalHero => HeroOf(LocalPlayerEntity);

    public Entity? OpponentHero => HeroOf(OpponentPlayerEntity);

    /// <summary>Battlegrounds turn number as shown in game (shop + combat = one turn).</summary>
    public int BattlegroundsTurn => (Turn + 1) / 2;

    public Entity? HeroOf(Entity? player) =>
        player is null ? null : Entities.GetValueOrDefault(player.GetInt(GameTag.HeroEntity));

    public IEnumerable<Entity> Controlled(int playerId, string zone) =>
        Entities.Values.Where(e => e.Controller == playerId && e.Zone == zone);

    public Entity GetOrCreate(int id)
    {
        if (!Entities.TryGetValue(id, out var entity))
        {
            entity = new Entity(id) { CreatedTurn = Turn };
            Entities[id] = entity;
        }

        return entity;
    }

    public PlayerSlot? PlayerByEntity(int entityId) => Players.Values.FirstOrDefault(p => p.EntityId == entityId);

    internal void TryResolveLocalPlayer()
    {
        if (LocalPlayerId != 0 || Players.Count < 2)
        {
            return;
        }

        if (IsBattlegrounds)
        {
            var real = Players.Values.Where(p =>
                p.HasAccount && !(Entities.GetValueOrDefault(p.EntityId)?.Has(GameTag.BaconDummyPlayer) ?? false)).ToList();
            if (real.Count == 1)
            {
                LocalPlayerId = real[0].PlayerId;
                return;
            }
        }

        // Only the local player's starting hand is revealed to us.
        var known = Entities.Values.FirstOrDefault(e =>
            e.InHand && e.CardId.Length > 0 && e.CardId != "GAME_005" && e.GetInt(GameTag.Creator) == 0
            && e.CardType != "HERO" && Players.ContainsKey(e.Controller));
        if (known is not null)
        {
            LocalPlayerId = known.Controller;
        }
    }

    internal void SetLocalPlayerByName(string name)
    {
        if (LocalPlayerId != 0)
        {
            return;
        }

        var match = Players.Values.FirstOrDefault(p => p.Name == name);
        if (match is not null)
        {
            LocalPlayerId = match.PlayerId;
        }
    }
}
