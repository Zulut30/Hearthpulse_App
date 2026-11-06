using System.Globalization;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Share;
using Hearthpulse.Core.Stats;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Decks;
using Hearthpulse.App.Main.Share;
#else
using Hearthpulse.UI.Main.Decks;
using Hearthpulse.UI.Main.Share;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// Share pictures (TASK-2026-11-07-growth-app): the session recap (Overview), the deck picture (deck and match pages).
/// Each call reads the history once and returns the window's model; the window draws and saves it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>How many recent games the recap looks through for the latest session.</summary>
    private const int RecapGames = 600;

    /// <summary>The share window of the latest session's recap (an empty card says how to get one).</summary>
    public ShareViewModel SessionRecapShare()
    {
        var games = _history.Recent(RecapGames);
        var recap = SessionRecap.Build(games, TimeSpan.FromHours(Settings.LadderSessionBreakHours), baseHero: _cards.BaseHeroId, rewards: games.Any(g => g.Mode == "Arena") ? _history.ArenaRewards() : null);
        var card = RecapCard(recap, _cards.NameOf);
        return new ShareViewModel(
            ShareKind.SessionRecap,
            card,
            Loc.T("Growth.Recap.Title"),
            Loc.F("Growth.Recap.FileName", recap.IsEmpty ? DateTime.Now : recap.Since),
            Settings,
            OwnBattleTag(games));
    }

    /// <summary>The share window of a deck's picture; null when the deck has no code to draw.</summary>
    public ShareViewModel? DeckShare(string? code, string? name, string? playerClass)
    {
        if (DeckPicture(code, name, playerClass) is not { } card)
        {
            return null;
        }

        return new ShareViewModel(
            ShareKind.Deck,
            card,
            Loc.T("Growth.Deck.Title"),
            Loc.F("Growth.Deck.FileName", card.Name.Length > 0 ? card.Name : card.ClassName),
            Settings,
            OwnBattleTag(_history.Recent(1)));
    }

    /// <summary>The YouTube timestamps window's model: History's games with its mode filter (proposal C2).</summary>
    public TimestampsViewModel Timestamps() =>
        new(_history.Recent(2000).Where(g => FilterMatch(g, HistoryFilter)).ToList(), Settings, _cards.NameOf, DateTime.Now);

    /// <summary>The recap's sections: Ranked per format, Battlegrounds per mode, Arena runs.</summary>
    internal static SessionRecapCard RecapCard(SessionRecapData recap, Func<string, string> cardName)
    {
        var sections = new List<RecapSection>();
        foreach (var ranked in recap.Ranked)
        {
            var stats = new List<ShareStat>
            {
                new(Loc.T("Growth.Recap.Games"), Number(ranked.Record.Games), Percent(ranked.Record.WinRate)),
                new(Loc.T("Growth.Recap.Record"), Loc.F("Widgets.Ladder.Record", ranked.Record.Wins, ranked.Record.Losses)),
            };
            if (ranked.Start is { } start && ranked.End is { } end)
            {
                // The rank now is the value; where it started and the change go under it (a long "from → to" would not fit).
                var (change, sign) = RankChange(ranked.Steps, ranked.LegendPlaces);
                stats.Add(new(Loc.T("Growth.Recap.Rank"), end.Display, start == end ? string.Empty : Loc.F("Growth.Recap.RankFrom", change, start.Display), sign));
            }

            if (ranked.BestDeck is { } deck && ranked.BestDeckRecord is { } record)
            {
                stats.Add(new(Loc.T("Growth.Recap.BestDeck"), deck, Loc.F("Growth.Recap.BestDeckRecord", record.Wins, record.Losses)));
            }

            if (ranked.LongestWinStreak >= 2)
            {
                stats.Add(new(Loc.T("Growth.Recap.Streak"), Number(ranked.LongestWinStreak), Loc.P("Growth.Recap.StreakValue", ranked.LongestWinStreak), 1));
            }

            sections.Add(new RecapSection(Loc.F("Growth.Recap.Ranked", Loc.T(ranked.Format == LadderSession.Wild ? "Widgets.Ladder.Wild" : "Widgets.Ladder.Standard")), BestDeckClassHero(ranked), stats, []));
        }

        foreach (var bg in recap.Battlegrounds)
        {
            var stats = new List<ShareStat>
            {
                new(Loc.T("Growth.Recap.Games"), Number(bg.Games)),
                new(Loc.T("Growth.Recap.AveragePlace"), bg.AveragePlace is { } avg ? avg.ToString("0.0", CultureInfo.CurrentCulture) : "—"),
                new(Loc.T(bg.Mode == "Duos" ? "Growth.Recap.Top2" : "Growth.Recap.Top4"), Percent(bg.TopRate)),
            };
            if (bg.MmrStart is { } from && bg.MmrEnd is { } to)
            {
                stats.Add(new(Loc.T("Growth.Recap.Mmr"), Signed(bg.MmrDelta ?? 0), Loc.F("Widgets.Ladder.Ranks", Number(from), Number(to)), Math.Sign(bg.MmrDelta ?? 0)));
            }

            if (bg.BestHero is { } best && bg.BestHeroPlace is { } place)
            {
                stats.Add(new(Loc.T("Growth.Recap.BestHero"), cardName(best), Loc.F("Growth.Recap.BestHeroPlace", place)));
            }

            sections.Add(new RecapSection(
                Loc.T(bg.Mode == "Duos" ? "Growth.Recap.Duos" : "Growth.Recap.Battlegrounds"),
                bg.BestHero ?? string.Empty,
                stats,
                bg.Placements.Select(p => new SharePlace(p.ToString(CultureInfo.CurrentCulture), p <= bg.TopCut)).ToList()));
        }

        foreach (var run in recap.Arena)
        {
            var stats = new List<ShareStat>
            {
                new(Loc.T("Growth.Recap.Run"), Loc.F("Widgets.Ladder.Record", run.Wins, run.Losses), Loc.T(run.Finished ? "Growth.Recap.RunFinished" : "Growth.Recap.RunOngoing")),
            };
            if (run.Rewards is { } rewards)
            {
                // As the Arena page's tile: the gold, the rest under it.
                stats.Add(new(Loc.T("Growth.Recap.Rewards"), rewards.Gold > 0 ? Loc.F("Growth.Recap.Gold", rewards.Gold) : "—", RewardsText(rewards, " · ", withGold: false)));
            }

            sections.Add(new RecapSection(Loc.F("Growth.Recap.Arena", ConstructedMeta.ClassName(run.PlayerClass)), ClassHero(run.PlayerClass), stats, []));
        }

        var when = recap.IsEmpty ? string.Empty : Loc.F("Growth.Recap.When", recap.Since, recap.Until);
        return new SessionRecapCard(when, sections, Loc.T("Growth.Recap.Empty"));
    }

    /// <summary>The deck picture of a code; null when the code does not decode.</summary>
    internal DeckPictureCard? DeckPicture(string? code, string? name, string? playerClass)
    {
        if (DeckImage.From(code, name, playerClass, _cards) is not { } deck)
        {
            return null;
        }

        var rows = deck.Cards.Select(c => new DeckListRow(
                c.CardId,
                c.Name,
                c.Cost,
                c.Rarity,
                c.Legendary ? "★" : c.Copies > 1 ? c.Copies.ToString(CultureInfo.CurrentCulture) : string.Empty))
            .ToList();
        var tallest = Math.Max(1, deck.Curve.Max());
        var curve = deck.Curve.Select((count, cost) => new ManaCurveBar(
                cost == DeckImage.CurveBuckets - 1 ? $"{cost}+" : cost.ToString(CultureInfo.CurrentCulture),
                count,
                (double)count / tallest))
            .ToList();
        var format = deck.Format switch
        {
            "FT_WILD" => Loc.T("Widgets.Ladder.Wild"),
            "FT_STANDARD" => Loc.T("Widgets.Ladder.Standard"),
            _ => null,
        };
        var facts = string.Join(" · ", new[]
        {
            format,
            Loc.P("Growth.Deck.Cards", deck.CardCount),
            deck.Dust > 0 ? Loc.F("Growth.Deck.Dust", deck.Dust) : null,
            Loc.F("Growth.Deck.AverageCost", deck.AverageCost),
        }.OfType<string>());
        var half = (rows.Count + 1) / 2;
        var className = ConstructedMeta.ClassName(deck.PlayerClass);
        return new DeckPictureCard(
            deck.Name.Length > 0 ? deck.Name : className,
            className,
            ClassHero(deck.PlayerClass),
            facts,
            deck.Code,
            rows,
            rows.Take(half).ToList(),
            rows.Skip(half).ToList(),
            curve);
    }

    /// <summary>The player's own BattleTag from the latest game that has one (the picture shows it only when asked).</summary>
    private static string? OwnBattleTag(IEnumerable<GameRecord> games) =>
        games.OrderByDescending(g => g.StartedAt).Select(g => g.PlayerName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

    private static string BestDeckClassHero(RankedRecap ranked) =>
        ranked.BestDeckClass is { Length: > 0 } heroClass ? ClassHero(heroClass) : string.Empty;

    private static (string Text, int Sign) RankChange(int? steps, int? legendPlaces) => (steps, legendPlaces) switch
    {
        (_, > 0 and var up) => (Loc.P("Widgets.Ladder.PlacesUp", up), 1),
        (_, < 0 and var down) => (Loc.P("Widgets.Ladder.PlacesDown", -down), -1),
        (> 0 and var up, _) => (Loc.P("Widgets.Ladder.StepsUp", up), 1),
        (< 0 and var down, _) => (Loc.P("Widgets.Ladder.StepsDown", -down), -1),
        _ => (string.Empty, 0),
    };

    private static string Number(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
