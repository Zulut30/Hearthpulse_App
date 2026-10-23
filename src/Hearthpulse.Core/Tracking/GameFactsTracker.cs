using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Facts about a game that the final game state no longer shows but its history row keeps: so far the heroes
/// offered at the Battlegrounds hero pick (the unpicked ones leave the hand once a hero is chosen).
/// </summary>
public sealed class GameFactsTracker : IPowerListener
{
    private readonly List<string> _offeredHeroes = new();
    private bool _picking;

    /// <summary>Hero card ids offered to the local player at the hero pick, in offer order (rerolled heroes appended).</summary>
    public IReadOnlyList<string> OfferedHeroes => _offeredHeroes;

    public void OnGameStarted(GameState game)
    {
        _offeredHeroes.Clear();
        _picking = false;
    }

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        // The hero pick is a MULLIGAN choice of hero cards (the parser only reports the local player's choices).
        if (!game.IsBattlegrounds || choiceType != "MULLIGAN" || options.Count == 0
            || !options.All(e => e.IsHero && e.CardId.Length > 0))
        {
            return;
        }

        _picking = true;
        foreach (var hero in options.OrderBy(e => e.ZonePosition))
        {
            Note(hero.CardId);
        }
    }

    public void OnChoiceMade(GameState game) => _picking = false;

    // A hero reroll turns an offered hero in hand into another one (CHANGE_ENTITY, reported as revealed) without a new
    // choice block, so a hero card showing up in the local hand while the pick is open is offered too.
    // Inferred from the log grammar; no kept log has a reroll to check it against yet.
    public void OnEntityRevealed(GameState game, Entity entity) => NoteRerolled(game, entity);

    public void OnEntityCreated(GameState game, Entity entity) => NoteRerolled(game, entity);

    private void NoteRerolled(GameState game, Entity entity)
    {
        if (_picking && entity.IsHero && entity.InHand && entity.CardId.Length > 0 && entity.Controller == game.LocalPlayerId)
        {
            Note(entity.CardId);
        }
    }

    private void Note(string cardId)
    {
        if (!_offeredHeroes.Contains(cardId))
        {
            _offeredHeroes.Add(cardId);
        }
    }
}
