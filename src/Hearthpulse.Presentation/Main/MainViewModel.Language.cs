using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>The interface language: texts the view model builds itself follow a language change without a restart.</summary>
public sealed partial class MainViewModel
{
    /// <summary>
    /// Shown under the language choice while the loaded card data is in another language than the one picked: card
    /// names are read once at start, so they switch after a restart.
    /// </summary>
    public string LanguageRestartHint =>
        _cards.Count > 0 && !string.Equals(_cards.Locale, Settings.CardLocale(), StringComparison.OrdinalIgnoreCase)
            ? Loc.T("Settings.Language.RestartHint")
            : string.Empty;

    /// <summary>Rebuilds the texts made from data (lists, captions, tiles) and re-reads every bound property.</summary>
    private void OnLanguageChanged()
    {
        RefreshAsync().Forget("History refresh");
        RebuildMeta();
        // My Stats keeps the texts it built; a refresh only rebuilds them when the history changed.
        RebuildBattlegroundsStats();
        RebuildConstructedStats();
        RebuildArenaStats();

        // The deck list, merge suggestions and the open deck page (shown again from the rebuilt families).
        RebuildDecks();
        RebuildMatch();
        RebuildConstructedMeta();
        if (_meta is null || _cards.Count == 0)
        {
            MetaUpdated = Loc.T("Main.Bg.CommunityLoading");
        }

        RebuildLanguagePlatformData();

        // The live bar, session line and setup warning come from the last snapshot.
        if (_lastSnapshot is { } snapshot)
        {
            Apply(snapshot);
        }

        ClearLanguagePlatformCache();
        RefreshPanelToggles();
        Raise(string.Empty);
    }

    partial void RebuildLanguagePlatformData();

    partial void ClearLanguagePlatformCache();
}
