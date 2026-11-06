using System.Globalization;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Share;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Main.Share;
#else
using Hearthpulse.UI.Main.Share;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// "Hearthpulse 2026 in review" (proposal A3): a page of its own (a window opened from Home's banner, so the sidebar and
/// its Ctrl+1…8 stay as they are) from December 1 to January 31 (any day with <c>HEARTHPULSE_YEAR_REVIEW=1</c>), built
/// from local history only, and its share picture.
/// </summary>
public sealed partial class MainViewModel
{
    private YearCard? _yearReview;
    private int _yearGeneration;

    /// <summary>The year the page shows today, or null while the page is hidden.</summary>
    public int? ReviewYear { get; } = YearInReview.ShownYear(DateTime.Now, YearInReview.Forced());

    /// <summary>Home shows the "your year is ready" banner (December and January, or the dev flag).</summary>
    public bool ShowYearReview => ReviewYear is not null;

    /// <summary>The banner's line: "Your 2026 in review is ready: …".</summary>
    public string YearReviewBanner => ReviewYear is { } year ? Loc.F("Growth.Year.Banner", year) : string.Empty;

    /// <summary>The page's content, null until <see cref="RefreshYearReviewAsync"/> has run once.</summary>
    public YearCard? YearReview { get => _yearReview; private set => Set(ref _yearReview, value); }

    /// <summary>Reads the year's games off the UI thread and fills <see cref="YearReview"/>; a newer call wins.</summary>
    public async Task RefreshYearReviewAsync()
    {
        if (ReviewYear is not { } year)
        {
            return;
        }

        var generation = ++_yearGeneration;
        try
        {
            var data = await Task.Run(() => YearData(year)).ConfigureAwait(true);
            if (generation == _yearGeneration)
            {
                YearReview = YearCardOf(data);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Core.Diag.Error("Year in review failed", ex);
        }
    }

    /// <summary>The year page's model, its history read off the UI thread; null while the page is hidden.</summary>
    public async Task<ShareViewModel?> YearShareAsync()
    {
        await RefreshYearReviewAsync().ConfigureAwait(true);
        return YearShare();
    }

    /// <summary>The share window of the year in review; null while the page is hidden.</summary>
    public ShareViewModel? YearShare()
    {
        if (ReviewYear is not { } year)
        {
            return null;
        }

        var card = YearReview is { } shown && shown.Title == Loc.F("Growth.Year.Title", year) ? shown : YearCardOf(YearData(year));
        return new ShareViewModel(ShareKind.Year, card, card.Title, Loc.F("Growth.Year.FileName", year), Settings, OwnBattleTag(_history.Recent(1)));
    }

    private YearInReviewData YearData(int year)
    {
        var from = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Local);
        var games = _history.Since(from);
        return YearInReview.Build(games, year, _history.PlayedCardCounts(from, from.AddYears(1)), _cards.BaseHeroId);
    }

    /// <summary>The page and picture of a year: the tiles that have data, in a fixed order.</summary>
    internal YearCard YearCardOf(YearInReviewData data)
    {
        var title = Loc.F("Growth.Year.Title", data.Year);
        if (data.IsEmpty)
        {
            return new YearCard(title, Loc.T("Growth.Year.Subtitle"), [], string.Empty, string.Empty, string.Empty, Loc.F("Growth.Year.Empty", data.Year));
        }

        var stats = new List<ShareStat>
        {
            new(Loc.T("Growth.Year.Games"), Number(data.Games), data.Record.Wins + data.Record.Losses > 0 ? Loc.F("Growth.Year.Record", data.Record.Wins, data.Record.Losses) : string.Empty),
            new(Loc.T("Growth.Year.Hours"), Loc.F("Growth.Year.HoursValue", Math.Round(data.TimePlayed.TotalHours))),
        };
        if (data.FavouriteClass is { } heroClass)
        {
            stats.Add(new(Loc.T("Growth.Year.FavouriteClass"), ConstructedMeta.ClassName(heroClass), Loc.P("Widgets.Ladder.Games", data.FavouriteClassGames)));
        }

        if (data.FavouriteDeck is { } deck)
        {
            stats.Add(new(Loc.T("Growth.Year.FavouriteDeck"), deck, Loc.P("Widgets.Ladder.Games", data.FavouriteDeckGames)));
        }

        if (data.BestWinStreak >= 2)
        {
            stats.Add(new(Loc.T("Growth.Year.BestStreak"), Number(data.BestWinStreak), Loc.P("Growth.Recap.StreakValue", data.BestWinStreak), 1));
        }

        if (data.BestRank is { } rank)
        {
            stats.Add(new(Loc.T("Growth.Year.BestRank"), rank.Display));
        }

        if (data.BestRating is { } rating)
        {
            stats.Add(new(Loc.T("Growth.Year.BestRating"), Number(rating)));
        }

        if (data.TopCard is { } card)
        {
            stats.Add(new(Loc.T("Growth.Year.TopCard"), _cards.NameOf(card), Loc.P("Growth.Year.TopCardPlays", data.TopCardPlays)));
        }

        if (data.FavouriteHero is { } hero)
        {
            stats.Add(new(Loc.T("Growth.Year.FavouriteHero"), _cards.NameOf(hero), Loc.P("Widgets.Ladder.Games", data.FavouriteHeroGames)));
        }

        if (data.TopRate is { } top)
        {
            stats.Add(new(Loc.T("Growth.Year.TopRate"), top.ToString("P0", CultureInfo.CurrentCulture), Loc.P("Growth.Year.TopRateDetail", data.BattlegroundsGames)));
        }

        if (data.BestMonth is { } month)
        {
            stats.Add(new(Loc.T("Growth.Year.BestMonth"), Loc.DateCulture.TextInfo.ToTitleCase(Loc.DateCulture.DateTimeFormat.GetMonthName(month))));
        }

        return new YearCard(
            title,
            Loc.T("Growth.Year.Subtitle"),
            stats,
            data.FavouriteClass is { } c ? ClassHero(c) : string.Empty,
            data.FavouriteHero ?? string.Empty,
            data.TopCard ?? string.Empty,
            Loc.F("Growth.Year.Empty", data.Year));
    }
}
