#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// One option of a Settings tab for the search: the tab (its tag and title), the label shown, what it is found by and
/// the control to jump to (a view element; the model never looks inside it).
/// </summary>
public sealed record SettingsEntry(string Tab, string TabTitle, string Label, string Haystack, object Target)
{
    /// <summary>"Tab › Label", the result line and its accessible name.</summary>
    public string Path => TabTitle + " › " + Label;
}

/// <summary>
/// The Settings tools over the tabs: the search (<see cref="SettingsQuery"/> over the options the view indexes), reset
/// of a group or of everything (with an inline confirmation) and the settings file. A reset or an import changes many
/// settings at once: every binding is refreshed and the host repaints, applies and saves (<see cref="SettingsReplaced"/>).
/// </summary>
public sealed partial class MainViewModel
{
    private string _settingsQuery = string.Empty;
    private IReadOnlyList<SettingsEntry> _settingsHits = [];
    private bool _resetAllPending;
    private string _settingsFileNote = string.Empty;
    private RelayCommand? _jumpToSetting;
    private RelayCommand? _clearSettingsQuery;
    private RelayCommand? _resetSettingsGroup;
    private RelayCommand? _askResetAll;
    private RelayCommand? _confirmResetAll;
    private RelayCommand? _cancelResetAll;

    /// <summary>The view's options, collected from its Settings tabs when the search first needs them.</summary>
    public Func<IReadOnlyList<SettingsEntry>>? SettingsIndex { get; set; }

    /// <summary>The player picked a search result: the view opens its tab, scrolls to it and highlights it.</summary>
    public event Action<SettingsEntry>? SettingJumpRequested;

    /// <summary>A reset or an import replaced settings: the view re-applies language and toggles, the host saves and applies.</summary>
    public event Action? SettingsReplaced;

    public string SettingsQuery
    {
        get => _settingsQuery;
        set
        {
            if (Set(ref _settingsQuery, value ?? string.Empty))
            {
                var index = SettingsIndex?.Invoke() ?? [];
                _settingsHits = SettingsSearch.Find(_settingsQuery, index, e => e.Label, e => e.Haystack);
                Raise(nameof(SettingsHits));
                Raise(nameof(SettingsSearching));
                Raise(nameof(SettingsNoHits));
            }
        }
    }

    public IReadOnlyList<SettingsEntry> SettingsHits => _settingsHits;

    /// <summary>A query is typed: the results replace the tabs.</summary>
    public bool SettingsSearching => SettingsSearch.Normalize(_settingsQuery).Length > 0;

    public bool SettingsNoHits => SettingsSearching && _settingsHits.Count == 0;

    public RelayCommand JumpToSetting => _jumpToSetting ??= new RelayCommand(p =>
    {
        if (p is SettingsEntry entry)
        {
            SettingsQuery = string.Empty;
            SettingJumpRequested?.Invoke(entry);
        }
    });

    public RelayCommand ClearSettingsQuery => _clearSettingsQuery ??= new RelayCommand(() => SettingsQuery = string.Empty);

    /// <summary>Puts one group (a <see cref="SettingsGroups"/> name) back to its defaults.</summary>
    public RelayCommand ResetSettingsGroup => _resetSettingsGroup ??= new RelayCommand(p =>
    {
        if (p is string group)
        {
            Replace(() => SettingsGroups.Reset(Settings, group));
        }
    });

    /// <summary>"Reset everything" asks first: the confirmation row shows under the button.</summary>
    public bool ResetAllPending
    {
        get => _resetAllPending;
        private set => Set(ref _resetAllPending, value);
    }

    public RelayCommand AskResetAll => _askResetAll ??= new RelayCommand(() => ResetAllPending = true);

    public RelayCommand CancelResetAll => _cancelResetAll ??= new RelayCommand(() => ResetAllPending = false);

    public RelayCommand ConfirmResetAll => _confirmResetAll ??= new RelayCommand(() =>
    {
        ResetAllPending = false;
        Replace(() =>
        {
            SettingsGroups.ResetAll(Settings);
            return true;
        });
        SettingsFileNote = Loc.T("Settings.Reset.Done");
    });

    /// <summary>What the last export, import or reset did, under the file buttons.</summary>
    public string SettingsFileNote
    {
        get => _settingsFileNote;
        private set => Set(ref _settingsFileNote, value);
    }

    /// <summary>The settings file's text for the view to write.</summary>
    public string ExportSettings() => SettingsGroups.Export(Settings);

    /// <summary>The view wrote (or failed to write) the settings file.</summary>
    public void SettingsExported(string? path) =>
        SettingsFileNote = path is null ? Loc.T("Settings.File.ExportFailed") : Loc.F("Settings.File.Exported", System.IO.Path.GetFileName(path));

    /// <summary>Takes a settings file's text the view read; false (and a note) when it is not one.</summary>
    public bool ImportSettings(string? json)
    {
        var imported = false;
        if (json is not null)
        {
            Replace(() => imported = SettingsGroups.Import(Settings, json));
        }

        SettingsFileNote = Loc.T(imported ? "Settings.File.Imported" : "Settings.File.NotSettings");
        return imported;
    }

    private void Replace(Func<bool> change)
    {
        var theme = (CurrentTheme, CurrentLook);
        if (!change())
        {
            return;
        }

        // Every binding reads again (the tabs bind straight to Settings and to wrappers here); then the theme, the OBS
        // windows and the rest of the app follow.
        _customPreview = null;
        _previewTheme = null;
        Raise(string.Empty);
        if ((CurrentTheme, CurrentLook) != theme)
        {
            ThemeChanged?.Invoke();
        }

        StreamSettingsChanged?.Invoke();
        SettingsReplaced?.Invoke();
    }
}
