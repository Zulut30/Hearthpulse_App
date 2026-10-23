using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Stats;

/// <summary>Header numbers of a deck page.</summary>
public sealed record DeckSummary(WinLoss Record, TimeSpan? AverageDuration, double? AverageTurns, WinLoss OnPlay, WinLoss OnCoin);

/// <summary>A game on the deck's win-rate line: the win rate of all its games up to and including this one.</summary>
public sealed record DeckWinRatePoint(GameRecord Game, double WinRate, WinLoss SoFar);

/// <summary>Results against an opponent class, or against one archetype of it (<see cref="Archetype"/> set).</summary>
public sealed record DeckMatchup(string Key, string OpponentClass, string? Archetype, WinLoss Record);

/// <summary>
/// How one card of the deck did, counted per game (two copies in one game count once). Mulligan numbers come from games
/// that saved the mulligan; draw and play numbers from games that saved the cards drawn.
/// </summary>
/// <param name="Copies">Copies in the deck's main version (0: only in other versions).</param>
/// <param name="Offered">Games the card was in the opening hand before the mulligan.</param>
/// <param name="WhenKept">Games it was offered and at least one copy was kept.</param>
/// <param name="WhenReplaced">Games it was offered and every copy was sent back.</param>
/// <param name="WhenDrawn">Games it was drawn at any point (a kept opening card counts).</param>
/// <param name="WhenPlayed">Games it was played from hand.</param>
/// <param name="AveragePlayTurn">The player's turn of its first play, averaged over those games.</param>
public sealed record DeckCardStats(
    int DbfId,
    string CardId,
    int Copies,
    int Offered,
    WinLoss WhenKept,
    WinLoss WhenReplaced,
    WinLoss WhenDrawn,
    WinLoss WhenPlayed,
    double? AveragePlayTurn)
{
    /// <summary>Kept over offered, null when never offered.</summary>
    public double? KeepRate => Offered == 0 ? null : (double)WhenKept.Games / Offered;
}

/// <summary>Deck page aggregates: pure functions over a deck family's games.</summary>
public static class DeckDetails
{
    /// <summary>Fewer games than this make a number "few games": shown muted.</summary>
    public const int SmallSample = 5;

    public static DeckSummary Summary(IReadOnlyList<GameRecord> games)
    {
        var timed = games.Where(g => g.Duration > TimeSpan.Zero).ToList();
        return new DeckSummary(
            WinLoss.Of(games),
            timed.Count > 0 ? TimeSpan.FromTicks((long)timed.Average(g => g.Duration.Ticks)) : null,
            games.Count > 0 ? games.Average(g => g.Turns) : null,
            WinLoss.Of(games.Where(g => g.WentFirst == true)),
            WinLoss.Of(games.Where(g => g.WentFirst == false)));
    }

    /// <summary>Running win rate after each decided game, oldest first (ties and unknown results are skipped).</summary>
    public static IReadOnlyList<DeckWinRatePoint> WinRateTimeline(IReadOnlyList<GameRecord> games)
    {
        var points = new List<DeckWinRatePoint>();
        int count = 0, wins = 0, losses = 0;
        foreach (var game in games.OrderBy(g => g.StartedAt))
        {
            count++;
            if (game.Result == "Win") wins++;
            else if (game.Result == "Loss") losses++;
            else continue;

            points.Add(new DeckWinRatePoint(game, (double)wins / (wins + losses), new WinLoss(count, wins, losses)));
        }

        return points;
    }

    /// <summary>
    /// The opponent's archetype in a game: with a meta of the game's format, the one its saved cards clearly point to
    /// there (every game named by the same meta, so matchups do not mix sources or patches); without one, the archetype
    /// saved with the game. Null when it cannot be told.
    /// </summary>
    public static string? OpponentArchetype(GameRecord game, ConstructedMeta? meta) =>
        ArchetypeGuess.FitsFormat(meta, game.Format) && game.OpponentCards is not null
            ? ArchetypeGuess.FromSeenCards(meta, game.OpponentClass, GameCards.Turns(game.OpponentCards).Select(c => c.CardId))?.Name
            : game.OpponentArchetype is { Length: > 0 } saved ? saved : null;

    /// <summary>Results per opponent class, most played first.</summary>
    public static IReadOnlyList<DeckMatchup> ByOpponentClass(IReadOnlyList<GameRecord> games) =>
        games.Where(g => g.OpponentClass.Length > 0)
            .GroupBy(g => g.OpponentClass)
            .Select(g => new DeckMatchup(g.Key, g.Key, null, WinLoss.Of(g)))
            .OrderByDescending(m => m.Record.Games)
            .ToList();

    /// <summary>Results per opponent archetype where it was told (<see cref="OpponentArchetype"/>), most played first.</summary>
    public static IReadOnlyList<DeckMatchup> ByOpponentArchetype(IReadOnlyList<GameRecord> games, ConstructedMeta? meta) =>
        games.Select(g => (Game: g, Archetype: OpponentArchetype(g, meta)))
            .Where(x => x.Archetype is not null && x.Game.OpponentClass.Length > 0)
            .GroupBy(x => (x.Game.OpponentClass, x.Archetype!))
            .Select(g => new DeckMatchup($"{g.Key.OpponentClass}|{g.Key.Item2}", g.Key.OpponentClass, g.Key.Item2, WinLoss.Of(g.Select(x => x.Game))))
            .OrderByDescending(m => m.Record.Games)
            .ToList();

    /// <summary>The games of one matchup (<see cref="DeckMatchup.Key"/>), or all of them for null.</summary>
    public static IReadOnlyList<GameRecord> Filter(IReadOnlyList<GameRecord> games, string? matchupKey, ConstructedMeta? meta)
    {
        if (string.IsNullOrEmpty(matchupKey))
        {
            return games;
        }

        var split = matchupKey.IndexOf('|', StringComparison.Ordinal);
        if (split < 0)
        {
            return games.Where(g => g.OpponentClass == matchupKey).ToList();
        }

        var (opponentClass, archetype) = (matchupKey[..split], matchupKey[(split + 1)..]);
        return games.Where(g => g.OpponentClass == opponentClass && OpponentArchetype(g, meta) == archetype).ToList();
    }

    /// <summary>
    /// Per-card numbers for every card of the family's versions (the main version's cards first, by cost), from the
    /// games that saved card data.
    /// </summary>
    public static IReadOnlyList<DeckCardStats> Cards(DeckFamily family, IReadOnlyList<GameRecord> games, CardDb cards)
    {
        var dbfs = family.Main.Cards.Keys.Concat(family.Versions.SelectMany(v => v.Cards.Keys)).Distinct().ToList();
        var mulligans = games.Select(g => (Game: g, Mulligan: GameCards.Mulligan(g.Mulligan))).Where(x => x.Mulligan is not null).ToList();
        var tracked = games.Where(g => g.CardsDrawn is not null || g.Mulligan is not null)
            .Select(g => (Game: g, Drawn: GameCards.Turns(g.CardsDrawn), Played: GameCards.Turns(g.CardsPlayed)))
            .ToList();

        var list = new List<DeckCardStats>();
        foreach (var dbf in dbfs)
        {
            if (cards.Get(dbf) is not { } card)
            {
                continue;
            }

            var id = card.Id;
            var offered = mulligans.Where(x => x.Mulligan!.Offered.Contains(id)).ToList();
            var kept = offered.Where(x => x.Mulligan!.Kept.Contains(id)).Select(x => x.Game).ToList();
            var replaced = offered.Where(x => !x.Mulligan!.Kept.Contains(id)).Select(x => x.Game).ToList();
            var drawn = tracked.Where(x => x.Drawn.Any(c => c.CardId == id)).Select(x => x.Game).ToList();
            var played = tracked.Select(x => (x.Game, First: x.Played.Where(c => c.CardId == id).Select(c => (int?)c.Turn).Min()))
                .Where(x => x.First is not null)
                .ToList();
            list.Add(new DeckCardStats(
                dbf,
                id,
                family.Main.Cards.GetValueOrDefault(dbf),
                offered.Count,
                WinLoss.Of(kept),
                WinLoss.Of(replaced),
                WinLoss.Of(drawn),
                WinLoss.Of(played.Select(x => x.Game)),
                played.Count > 0 ? played.Average(x => x.First!.Value) : null));
        }

        return list
            .OrderBy(c => c.Copies == 0)
            .ThenBy(c => cards.Get(c.DbfId)?.Cost ?? 0)
            .ThenBy(c => cards.Get(c.DbfId)?.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Games that saved card data (mulligan or draws): the sample behind <see cref="Cards"/>.</summary>
    public static int CardDataGames(IReadOnlyList<GameRecord> games) => games.Count(g => g.CardsDrawn is not null || g.Mulligan is not null);
}
