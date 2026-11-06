using System.Globalization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Controls;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Controls;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.UI.Overlay;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Decks;
#else
namespace Hearthpulse.UI.Main.Decks;
#endif

public sealed partial class DeckDetailsViewModel
{
    private Action Build(DeckFamily family, IReadOnlyList<DeckFamily> families, IReadOnlyList<MergeSuggestion> suggestions, ConstructedMeta? meta,
        CardDb cards, string? matchup, string sortColumn, bool descending)
    {
        // The family's games again with their card columns (lists read the history without them).
        var all = _owner.History.WithCards(family.Games);
        var games = DeckDetails.Filter(all, matchup, meta, _owner.ArchetypeNames);
        var summary = DeckDetails.Summary(games);
        var deckRate = summary.Record.WinRate;
        var format = FormatName(family.Format);
        var metaFits = ArchetypeGuess.FitsFormat(meta, family.Format) ? meta : null;
        var archetype = ConstructedStats.ArchetypeOf(family.Main.DeckCode, family.PlayerClass, metaFits, cards);

        var archetypeLine = archetype is null ? string.Empty : Loc.F("Main.Meta.DeckInMeta", archetype.Name, Percent(archetype.WinRate));
        IReadOnlyList<string> facts = new[]
        {
            OverlayViewModel.ClassName(family.PlayerClass),
            format,
            family.Versions.Count > 1 ? Loc.P("Deck.VersionCount", family.Versions.Count) : null,
        }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        var lastPlayed = Loc.F("Deck.LastPlayed", MainViewModel.When(family.LastPlayed));
        var winRecord = summary.Record.Games == 0
            ? Loc.T("Deck.WinRate.NoGames")
            : $"{summary.Record.Wins}–{summary.Record.Losses} · {Loc.P("Deck.Games", summary.Record.Games)}";
        var winSign = deckRate is { } dr ? MetaTiers.WinRateSign(dr) : 0;
        var winScale = deckRate is { } sr ? Math.Clamp((sr - ScaleLow) / (ScaleHigh - ScaleLow), 0, 1) : -1;

        IReadOnlyList<StatTile> tiles =
        [
            new StatTile(Loc.T("Deck.Tile.Games"), summary.Record.Games.ToString("N0", CultureInfo.CurrentCulture),
                all.Count != games.Count ? Loc.F("Deck.Tile.GamesOf", all.Count) : Loc.T("Deck.Tile.WithDeck")),
            new StatTile(Loc.T("Deck.Tile.AvgGame"), summary.AverageDuration is { } avg ? Loc.F("Deck.Minutes", avg.TotalMinutes) : "—",
                summary.AverageTurns is { } turns ? Loc.F("Deck.Tile.AvgTurns", turns) : "—"),
            new StatTile(Loc.T("Deck.Tile.OnPlay"), Percent(summary.OnPlay.WinRate), Loc.F("Deck.Tile.OnPlayDetail", summary.OnPlay.Wins, summary.OnPlay.Losses)),
            new StatTile(Loc.T("Deck.Tile.OnCoin"), Percent(summary.OnCoin.WinRate), Loc.F("Deck.Tile.OnCoinDetail", summary.OnCoin.Wins, summary.OnCoin.Losses)),
            archetype is not null && deckRate is { } rate
                ? new StatTile(Loc.T("Deck.Tile.VsMeta"), Signed((rate - archetype.WinRate) * 100), $"{archetype.Name} {Percent(archetype.WinRate)} · {metaFits!.Source}",
                    Math.Sign(Math.Round((rate - archetype.WinRate) * 100)))
                : new StatTile(Loc.T("Deck.Tile.VsMeta"), "—", metaFits is null ? Loc.T("Deck.Tile.MetaNotLoaded") : Loc.T("Deck.Tile.NoArchetype")),
        ];

        var chart = DeckDetails.WinRateTimeline(games)
            .Select(p => (IChartValue)new DeckGamePoint(
                p.WinRate * 100,
                Percent(p.WinRate),
                p.Game.Result,
                Opponent(p.Game, meta, _owner.ArchetypeNames),
                GameDetail(p.Game),
                MainViewModel.When(p.Game.StartedAt),
                Loc.P("Deck.Chart.SoFar", p.SoFar.Games, p.SoFar.Wins, p.SoFar.Losses),
                p.Game.OpponentHeroCardId))
            .ToList();
        var lengths = ConstructedStats.ByLength(games);

        // Matchups count every game of the deck: they are the filter, not filtered.
        var classes = DeckDetails.ByOpponentClass(all);
        var archetypes = DeckDetails.ByOpponentArchetype(all, meta, _owner.ArchetypeNames);
        // By the clicked column; ties keep the most played (then the best) first.
        List<(DeckMatchup Matchup, DeckMatchupRow Row)> Sorted(IEnumerable<DeckMatchup> list) => TableSort.Apply(
                list.OrderByDescending(m => m.Record.Games).ThenByDescending(m => m.Record.WinRate ?? -1).Select(m => (m, MatchupRow(m, matchup))),
                (r, c) => r.Item2.SortValue(c), sortColumn, descending)
            .ToList();
        var classRows = Sorted(classes);
        var classMarks = TableSort.BestAndWorst(classRows, r => r.Row.SortValue("WinRate") as double?, r => !r.Row.Few);
        var matchupRows = new List<DeckMatchupRow>();
        for (var i = 0; i < classRows.Count; i++)
        {
            var opponent = classRows[i].Matchup;
            matchupRows.Add(classRows[i].Row with { WinMark = classMarks[i] });
            matchupRows.AddRange(Sorted(archetypes.Where(a => a.OpponentClass == opponent.OpponentClass)).Select(a => a.Row));
        }

        var filterText = matchup is null
            ? string.Empty
            : Loc.F("Deck.Filter", matchupRows.FirstOrDefault(m => m.Key == matchup)?.Label ?? matchup, games.Count);

        // Personal mulligan and card numbers, next to the archetype's community mulligan when there is one.
        var metaCards = archetype?.Mulligan.ToDictionary(c => c.CardId, StringComparer.Ordinal) ?? new Dictionary<string, MetaMulliganCard>();
        var cardStats = DeckDetails.Cards(family, games, cards);
        var cardRows = cardStats.Select(c =>
            {
                var card = cards.Get(c.DbfId);
                var m = metaCards.GetValueOrDefault(c.CardId);
                var numbers = new DeckCardNumbers(
                    c.Offered,
                    c.Offered > 0 ? c.KeepRate : null,
                    c.WhenKept.WinRate,
                    c.WhenReplaced.WinRate,
                    m?.KeepRate,
                    m?.InHandWinRate,
                    c.WhenDrawn.Games,
                    c.WhenDrawn.WinRate,
                    c.WhenPlayed.Games,
                    c.WhenPlayed.WinRate,
                    c.AveragePlayTurn);
                return new DeckCardStatRow(
                    c.CardId,
                    card?.Name ?? c.CardId,
                    card?.Cost ?? 0,
                    c.Copies > 0 ? $"×{c.Copies}" : Loc.T("Deck.Mulligan.OtherVersion"),
                    c.Offered < DeckDetails.SmallSample,
                    c.WhenDrawn.Games < DeckDetails.SmallSample,
                    c.Offered.ToString(CultureInfo.CurrentCulture),
                    Percent(c.KeepRate),
                    Percent(c.WhenKept.WinRate),
                    Sign(c.WhenKept.WinRate, deckRate),
                    Percent(c.WhenReplaced.WinRate),
                    Sign(c.WhenReplaced.WinRate, deckRate),
                    m is null ? "—" : Percent(m.KeepRate),
                    m is null ? "—" : Percent(m.InHandWinRate),
                    c.WhenDrawn.Games.ToString(CultureInfo.CurrentCulture),
                    Percent(c.WhenDrawn.WinRate),
                    Sign(c.WhenDrawn.WinRate, deckRate),
                    c.WhenPlayed.Games.ToString(CultureInfo.CurrentCulture),
                    Percent(c.WhenPlayed.WinRate),
                    Sign(c.WhenPlayed.WinRate, deckRate),
                    c.AveragePlayTurn is { } t ? t.ToString("0.0", CultureInfo.CurrentCulture) : "—") { Numbers = numbers };
            })
            .ToList();
        cardRows = Marked(cardRows);
        // The main version's list, as the tracker shows it, and its curve (cost 7 and up share the last bar).
        var deckList = cardStats.Where(c => c.Copies > 0)
            .Select(c => (Stat: c, Card: cards.Get(c.DbfId)))
            .OrderBy(c => c.Card?.Cost ?? 0)
            .ThenBy(c => c.Card?.Name ?? c.Stat.CardId, StringComparer.CurrentCulture)
            .Select(c => new DeckListRow(
                c.Stat.CardId,
                c.Card?.Name ?? c.Stat.CardId,
                c.Card?.Cost ?? 0,
                c.Card?.Rarity ?? string.Empty,
                c.Card?.IsLegendary == true ? "★" : c.Stat.Copies > 1 ? c.Stat.Copies.ToString(CultureInfo.CurrentCulture) : string.Empty))
            .ToList();
        var copies = cardStats.Where(c => c.Copies > 0).Select(c => (Cost: cards.Get(c.DbfId)?.Cost ?? 0, c.Copies)).ToList();
        var perCost = Enumerable.Range(0, CurveBuckets).Select(b => copies.Where(c => Math.Min(c.Cost, CurveBuckets - 1) == b).Sum(c => c.Copies)).ToList();
        var tallest = Math.Max(1, perCost.Max());
        List<ManaCurveBar> curve = deckList.Count == 0
            ? []
            : perCost.Select((count, cost) => new ManaCurveBar(
                    cost == CurveBuckets - 1 ? $"{cost}+" : cost.ToString(CultureInfo.CurrentCulture),
                    count,
                    (double)count / tallest))
                .ToList();
        var total = copies.Sum(c => c.Copies);
        var deckListNote = total == 0
            ? string.Empty
            : Loc.F("Deck.List.Note", total, (copies.Sum(c => c.Cost * c.Copies) / (double)total).ToString("0.0", CultureInfo.CurrentCulture));

        var dataGames = DeckDetails.CardDataGames(games);
        var mulliganNote = (dataGames == 0
                ? Loc.T("Deck.Mulligan.NoData")
                : Loc.P("Deck.Mulligan.From", games.Count, dataGames, DeckDetails.SmallSample))
            + " " + (archetype is not null && metaCards.Count > 0
                ? Loc.F("Deck.Mulligan.Meta", archetype.Name, metaFits!.Source)
                : metaFits is null ? Loc.T("Deck.Mulligan.NeedsMeta") : Loc.T("Deck.Mulligan.NoMetaData"));
        var cardsNote = dataGames == 0
            ? Loc.T("Deck.Cards.NoData")
            : Loc.F("Deck.Cards.Note", Percent(deckRate));

        var main = family.Main;
        var versionRows = family.Versions.Select(v =>
            {
                var record = matchup is null ? v.Record : WinLoss.Of(games.Where(g => DeckFamilies.VersionKey(g) == v.Key));
                var deltas = v == main ? [] : DeckFamilies.Diff(main, v);
                return new DeckVersionRow(
                    v.Key,
                    v.Name,
                    v == main,
                    DeckVersionsText.VersionDetail(record.Games, MainViewModel.When(v.FirstPlayed), MainViewModel.When(v.LastPlayed)),
                    DeckVersionsText.Record(record),
                    Percent(record.WinRate),
                    record.WinRate ?? 0,
                    record.Games,
                    record.Games < DeckDetails.SmallSample,
                    Changes(deltas),
                    DiffRows(deltas, cards),
                    false,
                    family.Versions.Count > 1);
            })
            .ToList();

        var suggestionRows = suggestions
            .Where(s => s.First.Key == family.Key || s.Second.Key == family.Key)
            .Select(s =>
            {
                var other = s.First.Key == family.Key ? s.Second : s.First;
                var deltas = DeckFamilies.Diff(main, other.Main);
                return new DeckSuggestionRow(family.Key, other.Key, other.Name, DeckVersionsText.SuggestionDetail(s.Overlap, s.SameDeckId, s.SimilarNames, other.Record))
                {
                    Changes = Changes(deltas),
                    Diff = DiffRows(deltas, cards),
                };
            })
            .ToList();
        var dismissed = _owner.History.DismissedDeckPairs();

        var recent = games.AsEnumerable().Reverse().Take(12).Select(g => new DeckGameRow(
                g.Key,
                MainViewModel.When(g.StartedAt),
                g.Result,
                Opponent(g, meta, _owner.ArchetypeNames),
                g.OpponentHeroCardId,
                GameDetail(g)))
            .ToList();

        return () =>
        {
            Name = family.Name;
            HeroCardId = MainViewModel.ClassHero(family.PlayerClass);
            Facts = facts;
            LastPlayed = lastPlayed;
            WinRateText = Percent(deckRate);
            WinRecord = winRecord;
            WinSign = winSign;
            WinScale = winScale;
            Curve = curve;
            DeckList = deckList;
            DeckListNote = deckListNote;
            ArchetypeLine = archetypeLine;
            FilterText = filterText;
            Tiles = tiles;
            Chart = chart;
            LengthWins = lengths.Select(l => l.Record.Wins).ToList();
            LengthLosses = lengths.Select(l => l.Record.Losses).ToList();
            LengthLabels = lengths.Select(l => l.Label).ToList();
            Matchups = matchupRows;
            Raise(nameof(SortedByWinRate));
            CardRows = cardRows;
            MulliganRows = MulliganSort.Apply(cardRows, (r, c) => r.SortValue(c));
            PerformanceRows = CardsSort.Apply(cardRows, (r, c) => r.SortValue(c));
            MulliganNote = mulliganNote;
            CardsNote = cardsNote;
            ShowOpenDiffs(versionRows, suggestionRows);
            _dismissed = dismissed;
            RefreshCandidates();
            Games = recent;
        };
    }

    /// <summary>
    /// The cards with their best and worst win rates flagged, per column, among the cards seen often enough
    /// (<see cref="DeckDetails.SmallSample"/> mulligans for the mulligan columns, draws for the card columns).
    /// </summary>
    private static List<DeckCardStatRow> Marked(List<DeckCardStatRow> rows)
    {
        var kept = TableSort.BestAndWorst(rows, r => r.Numbers.KeptWinRate, r => !r.FewMulligan);
        var replaced = TableSort.BestAndWorst(rows, r => r.Numbers.ReplacedWinRate, r => !r.FewMulligan);
        var drawn = TableSort.BestAndWorst(rows, r => r.Numbers.DrawnWinRate, r => !r.FewDrawn);
        var played = TableSort.BestAndWorst(rows, r => r.Numbers.PlayedWinRate, r => !r.FewDrawn);
        return rows.Select((r, i) => r with { KeptMark = kept[i], ReplacedMark = replaced[i], DrawnMark = drawn[i], PlayedMark = played[i] }).ToList();
    }

    /// <summary>Card differences as tiles: added cards first, then the ones taken out.</summary>
    private static List<CardDiffRow> DiffRows(IReadOnlyList<CardDelta> deltas, CardDb cards) => deltas
        .Select(d => new CardDiffRow(
            cards.Get(d.DbfId)?.Id ?? string.Empty,
            cards.Get(d.DbfId)?.Name ?? d.DbfId.ToString(CultureInfo.InvariantCulture),
            $"{(d.Delta > 0 ? "+" : "−")}{Math.Abs(d.Delta)}",
            Math.Sign(d.Delta)))
        .ToList();

    /// <summary>"+2 / −2 cards", empty when the lists are the same (or unknown).</summary>
    private static string Changes(IReadOnlyList<CardDelta> deltas) => deltas.Count == 0
        ? string.Empty
        : DeckVersionsText.CardChanges(deltas.Where(d => d.Delta > 0).Sum(d => d.Delta), deltas.Where(d => d.Delta < 0).Sum(d => -d.Delta));

    private static DeckMatchupRow MatchupRow(DeckMatchup m, string? selected) => new(
        m.Key,
        m.Archetype ?? OverlayViewModel.ClassName(m.OpponentClass),
        m.Archetype is null ? MainViewModel.ClassHero(m.OpponentClass) : string.Empty,
        Loc.P("Deck.Games", m.Record.Games),
        $"{m.Record.Wins}–{m.Record.Losses}",
        Percent(m.Record.WinRate),
        m.Record.WinRate ?? 0,
        m.Archetype is not null,
        m.Key == selected,
        m.Record.Games < DeckDetails.SmallSample,
        m.Record.Games);

    private static string Opponent(GameRecord g, ConstructedMeta? meta, ArchetypeTranslations names)
    {
        var name = string.IsNullOrEmpty(g.OpponentClass) ? Loc.T("Deck.Game.UnknownOpponent") : OverlayViewModel.ClassName(g.OpponentClass);
        return Loc.F("Deck.Game.Vs", DeckDetails.OpponentArchetype(g, meta, names) is { } archetype ? archetype : name);
    }

    private static string GameDetail(GameRecord g) => string.Join(" · ", new[]
    {
        Loc.P("Deck.Game.Turns", g.Turns),
        g.Duration > TimeSpan.Zero ? Loc.F("Deck.Minutes", g.Duration.TotalMinutes) : null,
        g.WentFirst switch { true => Loc.T("Deck.Game.WentFirst"), false => Loc.T("Deck.Game.OnCoin"), _ => null },
        Core.Memory.RankInfo.Localize(g.Rank),
    }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>A history result ("Win", "Loss"...) as History shows it; the colour keeps the stored value.</summary>
    internal static string ResultText(string result) => result switch
    {
        "Win" => Loc.T("Main.Result.Win"),
        "Loss" => Loc.T("Main.Result.Loss"),
        "Tie" => Loc.T("Main.Result.Tie"),
        GameRecord.AbandonedResult => Loc.T("Main.Result.Abandoned"),
        "Unknown" => Loc.T("Main.Result.Unknown"),
        _ => result,
    };

    /// <summary>+1 when a card's win rate is 5 points or more above the deck's, -1 when as far below.</summary>
    private static int Sign(double? rate, double? deck) =>
        rate is { } r && deck is { } d ? (r - d >= 0.05 ? 1 : d - r >= 0.05 ? -1 : 0) : 0;

    private static string Percent(double? value) => value is { } v ? v.ToString("P0", CultureInfo.CurrentCulture) : "—";

    private static string Signed(double points)
    {
        // In the culture's own percent pattern ("+3%", "+3 %"), like every other percentage on the page.
        var rounded = Math.Round(points);
        var size = (Math.Abs(rounded) / 100).ToString("P0", CultureInfo.CurrentCulture);
        return rounded > 0 ? "+" + size : rounded < 0 ? "−" + size : "±" + size;
    }
}
