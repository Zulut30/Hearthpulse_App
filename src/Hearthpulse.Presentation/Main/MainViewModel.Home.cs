#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>Home's getting-started card: shown to a new player until the first game is recorded.</summary>
public sealed partial class MainViewModel
{
    private bool _showGettingStarted;

    /// <summary>
    /// True once the history was read and holds no games: Home shows the getting-started card. False before the first
    /// read, so the card does not flash for a player who has games.
    /// </summary>
    public bool ShowGettingStarted { get => _showGettingStarted; private set => Set(ref _showGettingStarted, value); }

    private bool _logsReady;

    /// <summary>
    /// Getting started, step 2: Hearthstone was found (it runs) and its log settings need nothing. Not while it is not found:
    /// with no install folder the log check has nothing to read and reports nothing missing.
    /// </summary>
    public bool LogsReady { get => _logsReady; private set => Set(ref _logsReady, value); }
}
