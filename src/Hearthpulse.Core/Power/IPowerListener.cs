namespace Hearthpulse.Core.Power;

/// <summary>Receives semantic events while Power.log is replayed into a <see cref="GameState"/>.</summary>
public interface IPowerListener
{
    void OnGameStarted(GameState game) { }

    void OnEntityCreated(GameState game, Entity entity) { }

    void OnEntityRevealed(GameState game, Entity entity) { }

    /// <summary>
    /// HIDE_ENTITY: the client stopped showing the card (a shown card shuffled back, a card returned to a hand). The
    /// entity keeps its last known card id, which the player may remember but which no longer says what it is later.
    /// </summary>
    void OnEntityHidden(GameState game, Entity entity) { }

    void OnTagChanged(GameState game, Entity entity, string tag, string previous, string value) { }

    void OnBlockStart(GameState game, string blockType, Entity? source) { }

    void OnBlockEnd(GameState game) { }

    /// <param name="source">The card that offers the choice (hero power, spell, triple reward…), when known.</param>
    void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options) { }

    /// <summary>The local player picked from the open choice.</summary>
    void OnChoiceMade(GameState game) { }

    /// <summary>One card the local player picked in the choice that just ended.</summary>
    void OnChosen(GameState game, Entity chosen) { }

    void OnGameEnded(GameState game) { }
}
