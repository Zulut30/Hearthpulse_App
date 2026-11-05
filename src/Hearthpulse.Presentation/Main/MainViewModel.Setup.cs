#if HEARTHPULSE_WPF
using System.IO;
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// A round step marker in the setup wizard's header: its number and name, lit while it is the open step, ticked only once
/// the step is done (logs on, history moved or nothing to move, panels arranged, look seen). Made once and updated, so
/// keyboard focus on a marker stays put.
/// </summary>
public sealed class SetupStepItem : Observable
{
    private bool _current;
    private bool _done;

    internal SetupStepItem(int index, string title)
    {
        Index = index;
        Number = (index + 1).ToString(CultureInfo.CurrentCulture);
        Title = title;
    }

    public int Index { get; }

    public string Number { get; }

    public string Title { get; }

    public bool Current { get => _current; private set => Set(ref _current, value); }

    public bool Done { get => _done; private set => Set(ref _done, value); }

    /// <summary>What a screen reader says: "Step 2 of 5: Your games, current" (or "done").</summary>
    public string AccessibleName => Loc.F(Current ? "Setup.StepNameCurrent" : Done ? "Setup.StepNameDone" : "Setup.StepName",
        Number, MainViewModel.SetupStepKeys.Count.ToString(CultureInfo.CurrentCulture), Title);

    internal void Update(bool current, bool done)
    {
        Current = current;
        Done = done;
        Raise(nameof(AccessibleName));
    }
}

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
    private bool _setupArranged;
    private bool _setupLookSeen;
    private bool _setupWatchesMigration;
    private string _setupLogsNote = string.Empty;
    private IReadOnlyList<SetupStepItem>? _setupSteps;
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

    /// <summary>The header's round step markers (the same objects for the life of the view model).</summary>
    public IReadOnlyList<SetupStepItem> SetupSteps =>
        _setupSteps ??= SetupStepKeys.Select((key, i) => new SetupStepItem(i, Loc.T("Setup.Step." + key))).ToList();

    public bool SetupOnLogs => _setupStep == 0;

    public bool SetupOnMigration => _setupStep == 1;

    public bool SetupOnLayout => _setupStep == 2;

    public bool SetupOnLook => _setupStep == 3;

    public bool SetupOnDone => _setupStep == SetupStepKeys.Count - 1;

    /// <summary>Back is offered from the second step on.</summary>
    public bool SetupCanGoBack => _setupStep > 0;

    /// <summary>
    /// The log.config the logs step reads and writes: null for the client's own (next to Hearthstone's settings, see
    /// <see cref="HearthstoneInstall.LogConfigPathFor"/>); tests point it at a scratch file.
    /// </summary>
    public string? SetupLogConfigFile { get; set; }

    private string? SetupLogConfigPath => SetupLogConfigFile ?? HearthstoneInstall.LogConfigPathFor(Settings.HearthstonePath);

    /// <summary>Hearthstone's folder is known and its log settings need nothing.</summary>
    public bool SetupLogsOk => Settings.HearthstonePath is not null && _setupLogsMissing.Count == 0;

    /// <summary>The logs step's status line in plain words: ready, not all on, or that Hearthstone was not found.</summary>
    public string SetupLogsStatus => Settings.HearthstonePath is null ? Loc.T("Setup.Logs.NotFound")
        : SetupLogsOk ? Loc.T("Setup.Logs.Ready")
        : Loc.T("Setup.Logs.Missing");

    /// <summary>log.config's missing sections, read when the logs step opens and after the fix.</summary>
    private IReadOnlyList<string> _setupLogsMissing = [];

    /// <summary>Shown after the fix: done (and whether Hearthstone must restart), or why it failed.</summary>
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

    /// <summary>Closes the wizard (Skip, Esc or Start playing); it does not open by itself again.</summary>
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
        if (p is int index || (p is string text && int.TryParse(text, CultureInfo.InvariantCulture, out index)))
        {
            GoToSetupStep(index);
        }
    });

    /// <summary>Writes Hearthstone's log.config (and lifts the log size limit), as the app does at its start.</summary>
    public RelayCommand FixLogs => _fixLogs ??= new RelayCommand(_ => FixSetupLogsAsync().Forget("Setup log fix"));

    /// <summary>Esc while the wizard is open closes it like Skip; false when it is not open (Esc keeps its other uses).</summary>
    public bool CloseSetupOnEscape()
    {
        if (!SetupOpen)
        {
            return false;
        }

        CloseSetup.Execute(null);
        return true;
    }

    /// <summary>The overlay step's "Arrange panels" was pressed: the step counts as done.</summary>
    public void SetupArranged()
    {
        _setupArranged = true;
        RefreshSetupSteps();
    }

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
            if (!_setupWatchesMigration)
            {
                _setupWatchesMigration = true;
                Migration.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(Migration.HasResult) or nameof(Migration.NothingFound))
                    {
                        RefreshSetupSteps();
                    }
                };
            }
        }

        _setupLookSeen |= SetupOnLook;
        foreach (var name in new[]
                 {
                     nameof(SetupStep), nameof(SetupOnLogs), nameof(SetupOnMigration), nameof(SetupOnLayout), nameof(SetupOnLook),
                     nameof(SetupOnDone), nameof(SetupCanGoBack), nameof(SetupLogsOk), nameof(SetupLogsStatus), nameof(SetupCanFixLogs),
                     nameof(SetupPageHeroes),
                 })
        {
            Raise(name);
        }

        RefreshSetupSteps();
    }

    /// <summary>Lights the open step's marker and ticks the done ones.</summary>
    private void RefreshSetupSteps()
    {
        var done = new[] { SetupLogsOk, Migration.HasResult || Migration.NothingFound, _setupArranged, _setupLookSeen, false };
        foreach (var item in SetupSteps)
        {
            item.Update(item.Index == _setupStep, done[item.Index]);
        }
    }

    private async Task FixSetupLogsAsync()
    {
        var install = Settings.HearthstonePath;
        var file = SetupLogConfigFile;
        _setupLogsFixing = true;
        Raise(nameof(SetupCanFixLogs));
        try
        {
            var restart = await Task.Run(() =>
            {
                try
                {
                    // The client's own file: everything the app does at its start (and the log size limit); a scratch one: that file.
                    return file is null ? LogConfig.EnsureAll(install) : LogConfig.EnsureIn(file);
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
            // The markers first: whoever waits for the fix to end (SetupCanFixLogs) reads them right away
            _setupLogsFixing = false;
            RefreshSetupSteps();
            Raise(nameof(SetupLogsOk));
            Raise(nameof(SetupLogsStatus));
            Raise(nameof(SetupCanFixLogs));
        }
    }

    private IReadOnlyList<string> ReadLogsMissing()
    {
        try
        {
            return SetupLogConfigPath is { } path ? LogConfig.MissingSectionsIn(path) : [];
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
