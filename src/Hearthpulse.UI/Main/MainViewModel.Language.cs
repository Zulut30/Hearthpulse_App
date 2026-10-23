using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

namespace Hearthpulse.UI.Main;

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
        Raise(string.Empty);
    }
}
