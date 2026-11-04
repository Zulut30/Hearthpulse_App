using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The Brawl page's article card (<c>docs/brawl.md</c>, package B2): hidden without a current article, the title, date
/// and teaser of the week's post, the deck collection line, the "in Russian" note in English, and links opened only
/// on hs-manacost.ru.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class BrawlArticleTests
{
    private static readonly ManacostArticle Week = new(
        "Потасовка недели: Битва зомбозверей", "Коротко о главном.", new DateTime(2026, 10, 28, 17, 0, 0, DateTimeKind.Utc),
        new Uri("https://hs-manacost.ru/potasovka-nedeli/"), null);

    [Fact]
    public void The_card_shows_the_weeks_article_and_its_deck_collection()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var vm = new MainViewModel(HistoryStore.InMemory(), new AppSettings(), TestDataFolder.App);
        Assert.False(vm.BrawlArticleShown);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var decks = Week with { Title = "12 колод для Потасовки", Link = new Uri("https://hs-manacost.ru/decks/") };
        vm.SetBrawlArticles(new BrawlArticles(Week, decks));

        Assert.Contains(nameof(MainViewModel.BrawlArticleShown), raised);
        Assert.True(vm.BrawlArticleShown);
        Assert.Equal(Week.Title, vm.BrawlArticleTitle);
        Assert.Equal(Loc.F("Brawl.Article.Meta", Week.Date.ToLocalTime(), ManacostArticles.Host), vm.BrawlArticleMeta);
        Assert.True(vm.BrawlArticleHasExcerpt);
        Assert.False(vm.BrawlArticleHasCover);
        Assert.True(vm.BrawlArticleInRussianNote);
        // In Russian the note is hidden (this class is in the language collection: no other test runs meanwhile).
        Loc.Instance.SetLanguage(Loc.Russian);
        try
        {
            Assert.False(vm.BrawlArticleInRussianNote);
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
        }

        Assert.True(vm.BrawlDecksShown);
        Assert.Equal(Loc.F("Brawl.Article.Decks", decks.Title), vm.BrawlDecksTitle);

        vm.SetBrawlArticles(BrawlArticles.Empty);
        Assert.False(vm.BrawlArticleShown);
        Assert.False(vm.BrawlDecksShown);
    }

    [Fact]
    public void Links_off_the_site_are_never_opened()
    {
        Assert.False(MainViewModel.OpenOnSite(null));
        Assert.False(MainViewModel.OpenOnSite(new Uri("https://evil.example/hs-manacost.ru")));
        Assert.False(MainViewModel.OpenOnSite(new Uri("http://hs-manacost.ru/")));
    }
}
