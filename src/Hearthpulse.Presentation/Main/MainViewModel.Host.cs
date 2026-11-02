using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>What both hosts report into and ask for: the busy line, the log import.</summary>
public sealed partial class MainViewModel
{
    private Func<string>? _busyText;

    /// <summary>
    /// Shows the busy line from a text maker, kept so a language switch shows it again in the new language; null clears
    /// it. Setting <see cref="Busy"/> directly (the render scenes) leaves the kept text alone.
    /// </summary>
    public void ShowBusy(Func<string>? text)
    {
        _busyText = text;
        Busy = text?.Invoke() ?? string.Empty;
    }

    private void ReshowBusy()
    {
        if (_busyText is not null)
        {
            Busy = _busyText();
        }
    }

    /// <summary>
    /// Imports the games of every Power log the client kept (<see cref="LogBackfill"/>), off the UI thread, then reads
    /// the history again. At start (<paramref name="force"/> false) the result is shown only when games were added; the
    /// "Import again" button always shows it.
    /// </summary>
    /// <param name="installDir">The Hearthstone folder; without it there is nothing to import.</param>
    public async Task ImportLogsAsync(string? installDir, bool force)
    {
        if (installDir is null)
        {
            ShowBusy(() => Loc.T("Host.Busy.NothingToImport"));
            return;
        }

        ShowBusy(() => Loc.T("Host.Busy.Importing"));
        var progress = new Progress<string>(text => ShowBusy(() => text));
        var cards = _cards;
        var result = await Task.Run(() => LogBackfill.Run(installDir, _history, cards, progress)).ConfigureAwait(true);
        Settings.HistoryImported = true;
        Settings.HistoryStatsFilled = true;
        Settings.HistoryCardsFilled = true;
        Settings.Save();
        ShowBusy(force || result.GamesAdded > 0
            ? () => Loc.F("Host.Busy.Imported", result.GamesAdded, result.Sessions)
            : null);
        await RefreshAsync().ConfigureAwait(true);
    }
}
