using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Battlegrounds;

/// <summary>
/// Records the local player's Battlegrounds minions that died and that were summoned this game, for counters such as
/// Eternal Knight and Ancestral Automaton. In real logs a combat board is a set of copies (<c>SETASIDE -> PLAY</c>,
/// <c>COPIED_FROM_ENTITY_ID</c> set): copies that die count as deaths, but entering play as a copy is not a summon.
/// </summary>
public sealed class BattlegroundsCounterTracker : IPowerListener
{
    private const string CopiedFrom = "COPIED_FROM_ENTITY_ID";

    private readonly HashSet<int> _summonedIds = [];

    /// <summary>Card ids of the local player's minions that died (PLAY -> GRAVEYARD), in order.</summary>
    public List<string> Died { get; } = [];

    /// <summary>Card ids of the local player's minions that entered play for the first time, not as a combat copy.</summary>
    public List<string> Summoned { get; } = [];

    public void OnGameStarted(GameState game)
    {
        _summonedIds.Clear();
        Died.Clear();
        Summoned.Clear();
    }

    public void OnEntityCreated(GameState game, Entity entity)
    {
        if (entity.Zone == "PLAY")
        {
            NoteEnteredPlay(game, entity);
        }
    }

    public void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value)
    {
        if (tag != GameTag.Zone || !IsLocalMinion(game, entity))
        {
            return;
        }

        if (value == "PLAY" && previous != "PLAY")
        {
            NoteEnteredPlay(game, entity);
        }
        else if (previous == "PLAY" && value == "GRAVEYARD")
        {
            Died.Add(entity.CardId);
        }
    }

    private void NoteEnteredPlay(GameState game, Entity entity)
    {
        if (IsLocalMinion(game, entity) && entity.GetInt(CopiedFrom) == 0 && _summonedIds.Add(entity.Id))
        {
            Summoned.Add(entity.CardId);
        }
    }

    private static bool IsLocalMinion(GameState game, Entity entity) =>
        game.IsBattlegrounds && entity.IsMinion && entity.CardId.Length > 0 && game.LocalPlayerId != 0 && entity.Controller == game.LocalPlayerId;
}
