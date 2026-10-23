using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Facts about a game that the final game state no longer shows but its history row keeps: so far the heroes
/// offered at the Battlegrounds hero pick (the unpicked ones leave the hand once a hero is chosen).
/// </summary>
public sealed class GameFactsTracker : IPowerListener
{
    private readonly List<string> _offeredHeroes = new();

    /// <summary>Hero card ids offered to the local player at the hero pick, in offer order (rerolls added).</summary>
    public IReadOnlyList<string> OfferedHeroes => _offeredHeroes;

    public void OnGameStarted(GameState game) => _offeredHeroes.Clear();

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        // The hero pick is a MULLIGAN choice of hero cards (the parser only reports the local player's choices).
        if (!game.IsBattlegrounds || choiceType != "MULLIGAN" || options.Count == 0
            || !options.All(e => e.IsHero && e.CardId.Length > 0))
        {
            return;
        }

        foreach (var hero in options.OrderBy(e => e.ZonePosition))
        {
            if (!_offeredHeroes.Contains(hero.CardId))
            {
                _offeredHeroes.Add(hero.CardId);
            }
        }
    }
}
