using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Localization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// The Brawl page's article card (docs/brawl.md): this week's Tavern Brawl article of hs-manacost.ru (the owner's site)
/// and its deck collection, opened in the browser; only links on that site are opened.
/// </summary>
public sealed partial class MainViewModel
{
    private BrawlArticles _brawlArticles = BrawlArticles.Empty;
    private RelayCommand? _openBrawlArticle;
    private RelayCommand? _openBrawlDecks;

    /// <summary>The articles the host loaded (start-up and every refresh); nothing current hides the card.</summary>
    public void SetBrawlArticles(BrawlArticles articles)
    {
        ArgumentNullException.ThrowIfNull(articles);
        _brawlArticles = articles;
        RaiseBrawlArticle();
    }

    public bool BrawlArticleShown => _brawlArticles.Main is not null;

    public string BrawlArticleTitle => _brawlArticles.Main?.Title ?? string.Empty;

    /// <summary>"28 октября · hs-manacost.ru".</summary>
    public string BrawlArticleMeta => _brawlArticles.Main is { } a ? Loc.F("Brawl.Article.Meta", a.Date.ToLocalTime(), ManacostArticles.Host) : string.Empty;

    public string BrawlArticleExcerpt => _brawlArticles.Main?.Excerpt ?? string.Empty;

    public bool BrawlArticleHasExcerpt => BrawlArticleExcerpt.Length > 0;

    /// <summary>The cover as a local file (the host downloaded it from the site), or null.</summary>
    public string? BrawlArticleCover => _brawlArticles.Main?.CoverFile;

    public bool BrawlArticleHasCover => BrawlArticleCover is not null;

    /// <summary>The articles are in Russian: an English interface says so on the card.</summary>
    public bool BrawlArticleInRussianNote => !Loc.IsRussian;

    public bool BrawlDecksShown => _brawlArticles.Decks is not null;

    /// <summary>"Подборка колод: 12 колод для Потасовки".</summary>
    public string BrawlDecksTitle => _brawlArticles.Decks is { } d ? Loc.F("Brawl.Article.Decks", d.Title) : string.Empty;

    public RelayCommand OpenBrawlArticle => _openBrawlArticle ??= new RelayCommand(() => OpenOnSite(_brawlArticles.Main?.Link));

    public RelayCommand OpenBrawlDecks => _openBrawlDecks ??= new RelayCommand(() => OpenOnSite(_brawlArticles.Decks?.Link));

    /// <summary>Opens a link only when it is on hs-manacost.ru over https (the list is checked when read too).</summary>
    internal static bool OpenOnSite(Uri? link) => ManacostArticles.OnSite(link?.AbsoluteUri) is { } safe && SystemBrowser.Open(safe);

    private void RaiseBrawlArticle()
    {
        Raise(nameof(BrawlArticleShown));
        Raise(nameof(BrawlArticleTitle));
        Raise(nameof(BrawlArticleMeta));
        Raise(nameof(BrawlArticleExcerpt));
        Raise(nameof(BrawlArticleHasExcerpt));
        Raise(nameof(BrawlArticleCover));
        Raise(nameof(BrawlArticleHasCover));
        Raise(nameof(BrawlArticleInRussianNote));
        Raise(nameof(BrawlDecksShown));
        Raise(nameof(BrawlDecksTitle));
    }
}
