#if HEARTHPULSE_WPF
using System.IO;
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>A step of the setup wizard's header: its number, name and whether it is the open one or already passed.</summary>
public sealed record SetupStepItem(int Index, string Number, string Title, bool Current, bool Passed);

/// <summary>
/// The setup wizard (D4, <c>docs/design-identity.md</c>): five short steps over the page - Hearthstone's logs, moving
/// HDT or Firestone history, placing the overlay panels, the look, done. It opens by itself once for a new player (no
/// games yet, never finished or skipped) and from the getting-started card, empty pages and Settings.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The steps in order; <see cref="SetupStep"/> indexes them.</summary>
    public static IReadOnlyList<string> SetupStepKeys { get; } = ["Logs", "Migration", "Layout", "Look", "Done"];

    private bool _setupOpen;
    private int _setupStep;
    private bool _setupLogsFixing;
    private string _setupLogsNote = string.Empty;
    private RelayCommand? _openSetup;
    private RelayCommand? _closeSetup;
    private RelayCommand? _setupNext;
    private RelayCommand? _setupBack;
    private RelayCommand? _setupGo;
    private RelayCommand? _fixLogs;

    /// <summary>True while the wizard covers the page.</summary>
    public bool SetupOpen { get => _setupOpen; private set => Set(ref _setupOpen, value); }

    /// <summary>The open step, 0 to <see cref="SetupStepKeys"/>.Count - 1.</summary>
    public int SetupStep => _setupStep;

    /// <summary>The header's round step markers.</summary>
    public IReadOnlyList<SetupStepItem> SetupSteps =>
        SetupStepKeys.Select((key, i) => new SetupStepItem(
            i, (i + 1).ToString(System.Globalization.CultureInfo.CurrentCulture), Loc.T("Setup.Step." + key), i == _setupStep, i < _setupStep)).ToList();

    public bool SetupOnLogs => _setupStep == 0;

    public bool SetupOnMigration => _setupStep == 1;

    public bool SetupOnLayout => _setupStep == 2;

    public bool SetupOnLook => _setupStep == 3;

    public bool SetupOnDone => _setupStep == SetupStepKeys.Count - 1;

    /// <summary>Back is offered from the second step on.</summary>
    public bool SetupCanGoBack => _setupStep > 0;

    /// <summary>Hearthstone's folder is known and its log settings need nothing.</summary>
    public bool SetupLogsOk => Settings.HearthstonePath is not null && _setupLogsMissing.Count == 0;

    /// <summary>The logs step's status line: ready, what is missing, or that Hearthstone was not found.</summary>
    public string SetupLogsStatus => Settings.HearthstonePath is null ? Loc.T("Setup.Logs.NotFound")
        : SetupLogsOk ? Loc.T("Setup.Logs.Ready")
        : Loc.F("Setup.Logs.Missing", string.Join(", ", _setupLogsMissing));

    /// <summary>log.config's missing sections, read when the logs step opens and after the fix.</summary>
    private IReadOnlyList<string> _setupLogsMissing = [];

    /// <summary>Shown after the fix wrote log.config: Hearthstone reads it at its start.</summary>
    public string SetupLogsNote { get => _setupLogsNote; private set => Set(ref _setupLogsNote, value); }

    /// <summary>The fix can be pressed: Hearthstone was found, something is missing and no fix runs.</summary>
    public bool SetupCanFixLogs => Settings.HearthstonePath is not null && !SetupLogsOk && !_setupLogsFixing;

    /// <summary>The "Background characters" switch on the look step; the page's hero follows it at once.</summary>
    public bool SetupPageHeroes
    {
        get => Settings.PageHeroes;
        set
        {
            if (Settings.PageHeroes != value)
            {
                Settings.PageHeroes = value;
                SaveSetupSettings();
                Raise(nameof(SetupPageHeroes));
                RaisePageHero();
            }
        }
    }

    /// <summary>Opens the wizard on its first step.</summary>
    public RelayCommand OpenSetup => _openSetup ??= new RelayCommand(_ => ShowSetup(0));

    /// <summary>Closes the wizard (Skip or Start playing); it does not open by itself again.</summary>
    public RelayCommand CloseSetup => _closeSetup ??= new RelayCommand(_ =>
    {
        SetupOpen = false;
        if (!Settings.SetupDone)
        {
            Settings.SetupDone = true;
            SaveSetupSettings();
        }
    });

    public RelayCommand SetupNext => _setupNext ??= new RelayCommand(_ => GoToSetupStep(_setupStep + 1));

    public RelayCommand SetupBack => _setupBack ??= new RelayCommand(_ => GoToSetupStep(_setupStep - 1));

    /// <summary>Goes to the step a header marker names (its index, as a number or text).</summary>
    public RelayCommand SetupGo => _setupGo ??= new RelayCommand(p =>
    {
        if (p is int index || (p is string text && int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out index)))
        {
            GoToSetupStep(index);
        }
    });

    /// <summary>Writes Hearthstone's log.config (and lifts the log size limit), as the app does at its start.</summary>
    public RelayCommand FixLogs => _fixLogs ??= new RelayCommand(_ => FixLogsAsync().Forget("Setup log fix"));

    /// <summary>The wizard opens by itself once: for a player with no games who has not finished or skipped it.</summary>
    private void OfferSetup(bool noGames)
    {
        if (noGames && !Settings.SetupDone && !_setupOfferedThisRun)
        {
            _setupOfferedThisRun = true;
            ShowSetup(0);
        }
    }

    private bool _setupOfferedThisRun;

    /// <summary>Opens the wizard on <paramref name="step"/>.</summary>
    public void ShowSetup(int step)
    {
        GoToSetupStep(step);
        SetupOpen = true;
    }

    private void GoToSetupStep(int step)
    {
        _setupStep = Math.Clamp(step, 0, SetupStepKeys.Count - 1);
        if (SetupOnLogs)
        {
            _setupLogsMissing = ReadLogsMissing();
        }

        if (SetupOnMigration)
        {
            // The scan of HDT and Firestone folders waits until something shows the card (Settings > Data does too).
            Migration.EnsureScanned();
        }

        foreach (var name in new[]
                 {
                     nameof(SetupStep), nameof(SetupSteps), nameof(SetupOnLogs), nameof(SetupOnMigration), nameof(SetupOnLayout),
                     nameof(SetupOnLook), nameof(SetupOnDone), nameof(SetupCanGoBack), nameof(SetupLogsOk), nameof(SetupLogsStatus),
                     nameof(SetupCanFixLogs), nameof(SetupPageHeroes),
                 })
        {
            Raise(name);
        }
    }

    private async Task FixLogsAsync()
    {
        var install = Settings.HearthstonePath;
        _setupLogsFixing = true;
        Raise(nameof(SetupCanFixLogs));
        try
        {
            var restart = await Task.Run(() =>
            {
                try
                {
                    return LogConfig.EnsureAll(install);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diag.Error("Setup wizard: log.config not writable", ex);
                    return false;
                }
            }).ConfigureAwait(true);
            _setupLogsMissing = await Task.Run(ReadLogsMissing).ConfigureAwait(true);
            SetupLogsNote = SetupLogsOk ? Loc.T(restart && HearthstoneRunning ? "Setup.Logs.Restart" : "Setup.Logs.Fixed") : Loc.T("Setup.Logs.Failed");
        }
        finally
        {
            _setupLogsFixing = false;
            Raise(nameof(SetupLogsOk));
            Raise(nameof(SetupLogsStatus));
            Raise(nameof(SetupCanFixLogs));
        }
    }

    private IReadOnlyList<string> ReadLogsMissing()
    {
        try
        {
            return LogConfig.MissingSections(Settings.HearthstonePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Setup wizard: log.config not readable", ex);
            return [];
        }
    }

    private void SaveSetupSettings()
    {
        try
        {
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diag.Error("Saving the settings from the setup wizard failed", ex);
        }
    }
}
