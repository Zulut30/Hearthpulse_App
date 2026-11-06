#if HEARTHPULSE_WPF
using Hearthpulse.App.Controls;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    private void RebuildBattlegroundsStats()
    {
        var (all, mode, period, sort, minGames, cards, meta, metaFailed) = (_allGames, BgMode, _bgPeriod, _bgSort, _bgMinGames, _cards, _meta, _bgMetaFailed);
        _statsBgMode = mode;
        _statsCards = cards;
        RunStatsBuild(0, () => BuildBattlegroundsStats(all, mode == "Duos", period, sort, minGames, cards, meta, metaFailed));
    }

    private Action BuildBattlegroundsStats(IReadOnlyList<GameRecord> all, bool duos, StatsPeriod period, BgHeroSort sort, int minGames, CardDb cards, BattlegroundsMeta? meta, bool metaFailed)
    {
        var games = StatsFilter.Apply(all.Where(g => BattlegroundsStats.IsGame(g, duos)), period, DateTime.Now);
        var summary = BattlegroundsStats.Summary(games, duos);
        var timed = Math.Max(1, games.Count(g => g.Duration > TimeSpan.Zero));
        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Stats.Bg.HighestMmr"), summary.HighestRating is { } top ? top.ToString("N0", CultureInfo.CurrentCulture) : "—",
                summary.NetRating is { } net ? Loc.F("Stats.Bg.NetDetail", Signed(net)) : Loc.T("Stats.Bg.RatingNotRead"), Math.Sign(summary.NetRating ?? 0)),
            new StatTile(Loc.T("Stats.Bg.AvgPlacement"), summary.AveragePlacement is { } avg ? avg.ToString("0.00", CultureInfo.CurrentCulture) : "—", string.Empty),
            new StatTile(Loc.T("Stats.TotalGames"), summary.Games.ToString("N0", CultureInfo.CurrentCulture), string.Empty),
            new StatTile(Loc.T("Stats.TimePlayed"), Hours(summary.TimePlayed), summary.Games > 0 ? Loc.F("Stats.Bg.MinPerGame", summary.TimePlayed.TotalMinutes / timed) : "—"),
        ];
        var total = Math.Max(1, summary.PlacementCounts.Sum());
        var shares = summary.PlacementCounts.Select(c => c / (double)total).ToList();
        var common = Math.Max(1, summary.PlacementCounts.DefaultIfEmpty(0).Max());
        var topPlaces = duos ? 2 : 4;
        List<BgPlaceRow> placeRows = summary.Games == 0
            ? []
            : summary.PlacementCounts.Select((count, i) => new BgPlaceRow(
                    Ordinal(i + 1),
                    shares[i].ToString("P0", CultureInfo.CurrentCulture),
                    count / (double)common,
                    i < topPlaces,
                    Loc.F("Stats.Bg.PlaceTip", Ordinal(i + 1), shares[i].ToString("P0", CultureInfo.CurrentCulture), count)))
                .ToList();

        var chart = BattlegroundsStats.RatingTimeline(games)
            .Select(p => (IChartValue)new BgGamePoint(
                p.Rating,
                p.Rating.ToString("N0", CultureInfo.CurrentCulture),
                p.Game.HeroCardId,
                cards.NameOf(p.Game.HeroCardId),
                Ordinal(p.Game.Placement),
                p.Game.Placement <= (duos ? 2 : 4) ? "Win" : "Loss",
                p.Delta is { } d ? Signed(d) : string.Empty,
                p.Delta ?? 0,
                When(p.Game.StartedAt),
                BattlegroundsStats.FinalBoard(p.Game)
                    .Select(m => new BoardMinionRow(m.CardId, cards.NameOf(m.CardId), m.Attack, m.Health, m.Golden))
                    .ToList()))
            .ToList();

        var heroes = BattlegroundsStats.Heroes(games, cards.BaseHeroId, duos,
            meta is null ? null : id => BattlegroundsStats.GlobalOf(meta.Hero(id, meta.WidestBracket)));
        var shown = BattlegroundsStats.Sort(heroes, sort, minGames);
        var maxPick = Math.Max(0.01, shown.Select(h => Math.Max(h.PickRate ?? 0, h.Global?.PickRate ?? 0)).DefaultIfEmpty(0).Max());
        var places = duos ? 4 : 8;
        var rows = shown.Select(h =>
            {
                var turns = Math.Max(h.CombatWinRate.Keys.DefaultIfEmpty(0).Max(), h.Global?.CombatWinRate?.Keys.DefaultIfEmpty(0).Max() ?? 0);
                turns = Math.Min(turns, 16);
                var counted = Math.Max(1, h.PlacementCounts.Sum());
                return new BgHeroPerfRow(
                    h.HeroCardId,
                    cards.NameOf(h.HeroCardId),
                    Loc.P("Stats.Games", h.Games),
                    h.NetRating is { } n ? Signed(n) : "—",
                    h.NetRating ?? 0,
                    Enumerable.Range(1, turns).Select(t => h.CombatWinRate.TryGetValue(t, out var v) ? v : (double?)null).ToList(),
                    h.Global?.CombatWinRate is { } gc ? Enumerable.Range(1, turns).Select(t => gc.TryGetValue(t, out var v) ? v : (double?)null).ToList() : null,
                    h.PlacementCounts.Select(c => c / (double)counted).ToList(),
                    h.Global?.PlacementShares,
                    h.AveragePlacement.ToString("0.00", CultureInfo.CurrentCulture),
                    1 - ((h.AveragePlacement - 1) / (places - 1)),
                    VsGlobal.Of(h.AveragePlacement, h.Global?.AveragePlacement, lowerIsBetter: true, "0.00"),
                    h.PickRate is { } pick ? pick.ToString("P0", CultureInfo.CurrentCulture) : "—",
                    (h.PickRate ?? 0) / maxPick,
                    VsGlobal.Of(h.PickRate, h.Global?.PickRate, lowerIsBetter: false, "0", percent: true));
            })
            .ToList();
        var note = duos
            ? Loc.T("Stats.Bg.NoteDuos")
            : meta is null
                ? Loc.T(metaFailed ? "Main.Bg.CommunityUnavailable" : "Stats.Bg.NoteLoading")
                : string.Empty;
        return () =>
        {
            BgStatTiles = tiles;
            BgPlaces = placeRows;
            BgRatingChart = chart;
            BgHeroPerf = rows;
            BgStatsNote = note;
        };
    }

    private void RebuildConstructedStats()
    {
        var (all, format, period, cards) = (_allGames, _ctFormat, _ctPeriod, _cards);
        var meta = ConstructedMetaFor(format);
        _statsCards = cards;
        RunStatsBuild(1, () => BuildConstructedStats(all, format, period, cards, meta));
    }

    private Action BuildConstructedStats(IReadOnlyList<GameRecord> all, string format, StatsPeriod period, CardDb cards, ConstructedMeta? meta)
    {
        var games = StatsFilter.Apply(all.Where(g => StatsFilter.IsConstructed(g, format)), period, DateTime.Now);
        var summary = ConstructedStats.Summary(games);
        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Stats.WinRate"), Percent(summary.Record.WinRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile(Loc.T("Stats.TotalGames"), summary.Record.Games.ToString("N0", CultureInfo.CurrentCulture), string.Empty),
            new StatTile(Loc.T("Stats.Ct.Rank"), summary.LatestRank ?? "—", summary.BestRank is { } best ? Loc.F("Stats.Ct.BestRank", best) : Loc.T("Stats.Ct.RankHidden")),
            new StatTile(Loc.T("Stats.TimePlayed"), Hours(summary.TimePlayed),
                summary.AverageDuration is { } avg ? Loc.F("Stats.Ct.AverageGame", avg.TotalMinutes, summary.AverageTurns) : "—"),
            new StatTile(Loc.T("Stats.Ct.OnPlay"), Percent(summary.OnPlay.WinRate), Loc.F("Stats.Ct.OnPlayDetail", summary.OnPlay.Wins, summary.OnPlay.Losses)),
            new StatTile(Loc.T("Stats.Ct.OnCoin"), Percent(summary.OnCoin.WinRate), Loc.F("Stats.Ct.OnCoinDetail", summary.OnCoin.Wins, summary.OnCoin.Losses)),
        ];

        var chart = ConstructedStats.RankTimeline(games)
            .Select(r => (IChartValue)new CtGamePoint(
                r.StarLevel + (r.LegendRank is { } l ? 1 - Math.Min(1, l / 10000.0) : 0),
                r.Label,
                r.Game.HeroCardId,
                r.Game.DeckName is { Length: > 0 } deck ? DeckName.Display(deck) : OverlayViewModel.ClassName(r.Game.PlayerClass),
                r.Game.Result,
                ResultText(r.Game.Result),
                string.IsNullOrEmpty(r.Game.OpponentClass)
                    ? Loc.P("Stats.Turns", r.Game.Turns)
                    : Loc.F("Stats.Ct.VsOpponent", OverlayViewModel.ClassName(r.Game.OpponentClass), Loc.P("Stats.Turns", r.Game.Turns)),
                When(r.Game.StartedAt)))
            .ToList();

        var classes = ConstructedStats.ByPlayerClass(games, meta).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        var opponents = ConstructedStats.ByOpponentClass(games).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        var archetypes = ConstructedStats.ByArchetype(games, g => ConstructedStats.ArchetypeOf(g.DeckCode, g.PlayerClass, meta, cards))
            .Select(g => Group(g, string.Empty, g.Label)).ToList();
        IReadOnlyList<StatGroupRow> lengths = games.Count == 0
            ? []
            : ConstructedStats.ByLength(games).Select(b => Group(new GroupStats(b.Label, Loc.F("Stats.Ct.LengthTurns", b.Label), b.Record), string.Empty, Loc.F("Stats.Ct.LengthTurns", b.Label))).ToList();
        var note = ConstructedNote(meta);
        return () =>
        {
            CtStatTiles = [.. tiles.Take(HeadlineTiles)];
            CtMoreTiles = [.. tiles.Skip(HeadlineTiles)];
            CtRankChart = chart;
            CtClasses = CtClassesSort.Apply(StatGroupRow.Marked(classes), (r, c) => r.SortValue(c));
            CtOpponents = CtOpponentsSort.Apply(StatGroupRow.Marked(opponents), (r, c) => r.SortValue(c));
            CtArchetypes = CtArchetypesSort.Apply(StatGroupRow.Marked(archetypes), (r, c) => r.SortValue(c));
            CtLengths = lengths;
            CtStatsNote = note;
        };
    }

    private void RebuildArenaStats()
    {
        var (all, period, cards, kind) = (_allGames, _arenaPeriod, _cards, _arenaKind);
        var arena = ArenaMetaFor(_arenaMeta, kind);
        RunStatsBuild(2, () => BuildArenaStats(all, period, cards, arena, kind));
    }

    private Action BuildArenaStats(IReadOnlyList<GameRecord> all, StatsPeriod period, CardDb cards, Core.Arena.ArenaMeta? arena, ArenaKind kind)
    {
        var now = DateTime.Now;
        var games = StatsFilter.Apply(all.Where(g => ArenaStats.Is(g, kind)), period, now);
        // Runs from every Arena game, then the ones with a game in the period: a run across the period's start stays
        // whole, so its key (by its first game when it has no deck id) and its rewards are the same in every period.
        var start = StatsFilter.Start(period, now);
        var runs = ArenaStats.Runs(StatsFilter.Apply(all.Where(g => ArenaStats.Is(g, kind)), StatsPeriod.All, now))
            .Where(r => start is null || r.Ended >= start)
            .ToList();
        var summary = ArenaStats.Summary(runs);
        var rewards = _history.ArenaRewards();
        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Stats.Arena.Runs"), summary.Runs.ToString("N0", CultureInfo.CurrentCulture), Loc.F("Stats.Arena.Finished", runs.Count(r => r.Finished))),
            new StatTile(Loc.T("Stats.Arena.AvgWins"), summary.AverageWins is { } avg ? avg.ToString("0.0", CultureInfo.CurrentCulture) : "—", Loc.T("Stats.Arena.PerFinishedRun")),
            new StatTile(Loc.T("Stats.Arena.BestRun"), runs.Count > 0 ? Loc.P("Stats.Arena.Wins", summary.BestWins) : "—", Loc.T("Stats.Arena.MostWins")),
            new StatTile(Loc.T("Stats.WinRate"), Percent(summary.Record.WinRate), $"{summary.Record.Wins}–{summary.Record.Losses}"),
            new StatTile(Loc.T("Stats.TimePlayed"), Hours(summary.TimePlayed), Loc.P("Stats.Games", summary.Record.Games)),
            RewardsTile(runs, rewards),
        ];
        var runRows = runs.AsEnumerable().Reverse().Select(r =>
            {
                var (deckScore, deckTier) = RunDeckScore(r, cards, arena);
                return new ArenaRunRow(
                    OverlayViewModel.ClassName(r.PlayerClass),
                    ClassHero(r.PlayerClass),
                    $"{r.Wins}–{r.Losses}",
                    r.Wins / (double)ArenaStats.MaxWins,
                    When(r.Started),
                    r.Finished ? Loc.P("Stats.Games", r.Games.Count) : Loc.F("Stats.Arena.InProgress", Loc.P("Stats.Games", r.Games.Count)),
                    r.Finished,
                    deckScore,
                    deckTier)
                {
                    Key = r.Key,
                    Rewards = rewards.GetValueOrDefault(r.Key),
                    RewardParts = RewardParts(rewards.GetValueOrDefault(r.Key)),
                    RewardsTip = rewards.GetValueOrDefault(r.Key) is { } given
                        ? Loc.F("Stats.Arena.Rewards.EditTip", RewardsText(given))
                        : Loc.T("Stats.Arena.Rewards.AddTip"),
                    EditRewards = EditArenaRewards,
                };
            })
            .ToList();
        var applyDeck = BuildArenaDeck(runs, cards, arena);
        var classes = ArenaStats.ByClass(runs)
            .Select(c => new StatGroupRow(
                OverlayViewModel.ClassName(c.PlayerClass),
                ClassHero(c.PlayerClass),
                Loc.P("Stats.Arena.RunCount", c.Runs) + (c.AverageWins is { } w ? Loc.F("Stats.Arena.AvgWinsSuffix", w) : Loc.T("Stats.Arena.NoneFinished")),
                $"{c.Record.Wins}–{c.Record.Losses}",
                Percent(c.Record.WinRate),
                c.Record.WinRate ?? 0,
                VsGlobal.None,
                c.Record.Games))
            .ToList();
        var opponents = ConstructedStats.ByOpponentClass(games).Select(g => Group(g, ClassHero(g.Key), OverlayViewModel.ClassName(g.Key))).ToList();
        return () =>
        {
            ArenaStatTiles = [.. tiles.Take(HeadlineTiles)];
            ArenaMoreTiles = [.. tiles.Skip(HeadlineTiles)];
            ArenaRuns = RebuiltArenaRuns(runRows);
            ArenaClasses = ArenaClassesSort.Apply(StatGroupRow.Marked(classes), (r, c) => r.SortValue(c));
            ArenaOpponents = ArenaOpponentsSort.Apply(StatGroupRow.Marked(opponents), (r, c) => r.SortValue(c));
            applyDeck(this);
        };
    }
}
