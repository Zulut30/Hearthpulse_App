using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Pool;

/// <summary>
/// One shared Constructed game, exactly what the pool server stores: its Discover-style picks, the format and whether
/// it was won. No deck, class, rank, opponent, player name or account.
/// </summary>
public sealed record ConstructedPoolGame(string GameId, string Format, bool Won, IReadOnlyList<ConstructedPick> Picks)
{
    /// <summary>Day the game was played, in days since 1970-01-01 (UTC).</summary>
    public int Day { get; init; } = PoolGame.Today;

    /// <summary>
    /// The record for a finished Ranked or Casual Standard/Wild game with at least one pick and a win or a loss; null
    /// for anything else (Battlegrounds, Arena, Brawl, Friendly, Twist, ties, games without picks).
    /// </summary>
    public static ConstructedPoolGame? From(GameOutcome outcome, string secret)
    {
        var format = CardPool.DiscoverPicks.FormatOf(outcome.Format);
        if (format is null || outcome.GameType is not ("GT_RANKED" or "GT_CASUAL") || outcome.Result is not ("Win" or "Loss")
            || outcome.ConstructedPicks.Count == 0 || string.IsNullOrEmpty(outcome.Key))
        {
            return null;
        }

        return new ConstructedPoolGame(PoolGame.Id("constructed|" + outcome.Key, secret), format, outcome.Result == "Win", outcome.ConstructedPicks);
    }
}
