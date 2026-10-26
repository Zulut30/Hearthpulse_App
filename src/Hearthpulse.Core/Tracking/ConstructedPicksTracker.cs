using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>One Discover-style pick of the local player in a Constructed game: what <paramref name="Source"/> offered and what was taken.</summary>
/// <param name="Source">The card id the log names as the choice's source (the generator or its helper).</param>
public sealed record ConstructedPick(int Turn, string Source, IReadOnlyList<string> Offered, IReadOnlyList<string> Picked);

/// <summary>
/// Remembers the local player's Discover, Adapt, Choose One and similar picks in a Constructed game, for the stats pool
/// (<see cref="Pool.ConstructedPoolGame"/>). Only choices the parser reports as the local player's, whose options are
/// on the player's screen; the opponent's choices never reach listeners. Runs on the log thread.
/// </summary>
public sealed class ConstructedPicksTracker : IPowerListener
{
    /// <summary>Picks kept per game; the pool takes as many.</summary>
    public const int MaxPicks = 60;

    /// <summary>The most options one choice may have to count (Discover offers 3, some cards 4 or 5).</summary>
    public const int MaxOptions = 8;

    private readonly List<ConstructedPick> _picks = [];
    private IReadOnlyList<Entity> _open = [];
    private int _openIndex = -1;

    /// <summary>The finished picks of the current (or just ended) game, oldest first.</summary>
    public IReadOnlyList<ConstructedPick> Picks => _picks.Where(p => p.Picked.Count > 0).ToList();

    public void OnGameStarted(GameState game)
    {
        _picks.Clear();
        Close();
    }

    public void OnChoices(GameState game, string choiceType, Entity? source, IReadOnlyList<Entity> options)
    {
        Close();
        if (game.IsBattlegrounds || choiceType != "GENERAL" || _picks.Count >= MaxPicks
            || source is not { CardId.Length: > 0 } || source.Controller != game.LocalPlayerId
            || options.Count is < 2 or > MaxOptions || options.Any(o => o.CardId.Length == 0))
        {
            return;
        }

        _picks.Add(new ConstructedPick(game.Turn, source.CardId, options.Select(o => o.CardId).ToList(), []));
        _openIndex = _picks.Count - 1;
        _open = options.ToArray();
    }

    public void OnChosen(GameState game, Entity chosen)
    {
        if (_openIndex < 0 || !_open.Any(o => o.Id == chosen.Id) || chosen.CardId.Length == 0)
        {
            return;
        }

        var pick = _picks[_openIndex];
        if (!pick.Picked.Contains(chosen.CardId, StringComparer.Ordinal))
        {
            _picks[_openIndex] = pick with { Picked = pick.Picked.Append(chosen.CardId).ToList() };
        }
    }

    // OnChoiceMade comes before the picked cards (OnChosen), so the choice stays open until the next one or the next game;
    // only its own options can be taken.

    private void Close()
    {
        _open = [];
        _openIndex = -1;
    }
}
