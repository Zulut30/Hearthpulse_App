#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Updates;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// Updates (TASK-2026-11-09-update-delivery): the "Update ready" chip, the status line in Settings → About, "Check now"
/// and "What's new". The view model takes strings, <see cref="Uri"/> and text makers only; the host wires it to
/// <see cref="UpdateService"/>.
/// </summary>
/// <remarks>
/// Status order: a running check shows "Checking…"; a failed install from the previous run stays until a newer version
/// is downloaded; a downloaded update is not overwritten by a later failure or "up to date"; otherwise the last result.
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>The player asked to install a downloaded update right away (the app installs it and restarts).</summary>
    public event Action? UpdateRequested;

    /// <summary>The player pressed "Check now"; the host asks the update loop and reports back with
    /// <see cref="UpdateCheckRefused"/> or <see cref="UpdateCheckFinished"/>.</summary>
    public event Action? UpdateCheckRequested;

    private bool _updateReady;
    private string? _updateVersion;
    private Func<string>? _updateStatus;
    private bool _updateChecking;
    private string? _installFailedVersion;
    private Func<string>? _installFailed;
    private RelayCommand? _checkUpdatesNow;

    private string? _notesVersion;
    private string? _notesText;
    private Uri? _notesPage;
    private bool _notesExpanded;
    private RelayCommand? _toggleReleaseNotes;
    private RelayCommand? _openReleasePage;

    /// <summary>A newer version has been downloaded: it installs by itself when Hearthpulse closes.</summary>
    public bool UpdateReady
    {
        get => _updateReady;
        private set => Set(ref _updateReady, value);
    }

    /// <summary>The downloaded update in a sentence; built on read so it follows a language change.</summary>
    public string UpdateText => _updateVersion is null ? string.Empty : Loc.F("Main.Update.Downloaded", _updateVersion);

    public RelayCommand InstallUpdate { get; }

    /// <summary>Tells the dashboard that a downloaded update is waiting for the next restart; a newer one replaces it.</summary>
    public void SetUpdateReady(string version)
    {
        _updateVersion = version;
        _updateStatus = null;
        Raise(nameof(UpdateText));
        UpdateReady = true;
        Raise(nameof(UpdateStatus));
    }

    /// <summary>The update line for Settings → About (checking, failed install, downloaded, last result).</summary>
    public string UpdateStatus
    {
        get
        {
            if (UpdateChecking)
            {
                return Loc.T("Settings.About.Checking");
            }

            if (_installFailed is not null && (!UpdateReady || _updateVersion == _installFailedVersion))
            {
                return _installFailed();
            }

            if (UpdateReady)
            {
                return UpdateText;
            }

            return _updateStatus?.Invoke() ?? Loc.T("Main.Update.NotChecked");
        }
    }

    /// <summary>Shows what the last update check found.</summary>
    public void SetUpdateStatus(string status) => SetUpdateStatus(() => status);

    /// <summary>The update status as a text maker: a language switch shows it again in the new language. While a
    /// downloaded update waits, the line keeps saying so (a later failure or "up to date" changes nothing visible).
    /// A failure also ends a running "Check now": the round is over even when it never reported its check.</summary>
    public void SetUpdateStatus(Func<string> status)
    {
        _updateStatus = status;
        UpdateChecking = false;
        Raise(nameof(UpdateStatus));
    }

    /// <summary>The install the previous run started did not happen: the same text as its notification, in the status
    /// line until a newer version is downloaded.</summary>
    public void SetInstallFailed(string version, Func<string> message)
    {
        _installFailedVersion = version;
        _installFailed = message;
        Raise(nameof(UpdateStatus));
    }

    /// <summary>The running version, for Settings → About.</summary>
    public string VersionText => $"Hearthpulse {UpdateService.CurrentVersion}";

    // ---- "Check now" ---------------------------------------------------------------------------

    /// <summary>"Check now" exists on desktops; phones get updates from their store.</summary>
    public bool UpdateCheckSupported => !OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS();

    /// <summary>A check the player asked for is running.</summary>
    public bool UpdateChecking
    {
        get => _updateChecking;
        private set
        {
            if (Set(ref _updateChecking, value))
            {
                Raise(nameof(CanCheckUpdates));
                Raise(nameof(UpdateStatus));
            }
        }
    }

    /// <summary>The "Check now" button is enabled: nothing is running.</summary>
    public bool CanCheckUpdates => UpdateCheckSupported && !UpdateChecking;

    /// <summary>"Check now": shows "Checking…" at once and asks the host; works with automatic updates off.</summary>
    public RelayCommand CheckUpdatesNow => _checkUpdatesNow ??= new RelayCommand(() =>
    {
        if (!CanCheckUpdates || UpdateCheckRequested is null)
        {
            return;
        }

        UpdateChecking = true;
        UpdateCheckRequested.Invoke();
    });

    /// <summary>The loop refused the check (GitHub's limit, a check moments ago): its reason in the status line and the
    /// button back. Nothing was sent. The reason is read once, now: the trigger's text changes with its next answer.</summary>
    public void UpdateCheckRefused(Func<string> reason)
    {
        var text = reason();
        _updateStatus = () => text;
        UpdateChecking = false;
        Raise(nameof(UpdateStatus));
    }

    /// <summary>A check finished (automatic or asked for): its sentence in the status line and the button back.</summary>
    public void UpdateCheckFinished(Func<string> message)
    {
        _updateStatus = message;
        UpdateChecking = false;
        Raise(nameof(UpdateStatus));
    }

    // ---- "What's new" ---------------------------------------------------------------------------

    /// <summary>The newer version's release notes; <paramref name="notes"/> null hides the block, <paramref name="page"/>
    /// null hides the GitHub link. A new version folds the notes again.</summary>
    public void SetReleaseNotes(string version, string? notes, Uri? page)
    {
        if (_notesVersion != version)
        {
            ReleaseNotesExpanded = false;
        }

        _notesVersion = version;
        _notesText = string.IsNullOrWhiteSpace(notes) ? null : notes;
        _notesPage = page;
        Raise(nameof(HasReleaseNotes));
        Raise(nameof(ReleaseNotesTitle));
        Raise(nameof(ReleaseNotesToggleName));
        Raise(nameof(ReleaseNotesText));
        Raise(nameof(HasReleasePage));
        Raise(nameof(ReleaseNotesShown));
        Raise(nameof(ReleasePageShown));
    }

    /// <summary>A newer version has notes to show.</summary>
    public bool HasReleaseNotes => _notesText is not null;

    /// <summary>"What's new in 0.15.0"; built on read so it follows a language change (the notes stay English).</summary>
    public string ReleaseNotesTitle => _notesVersion is null ? string.Empty : Loc.F("Settings.About.WhatsNew", _notesVersion);

    /// <summary>The "What's new" link's accessible name with its state ("…, collapsed" / "…, expanded"), since the link
    /// and its chevron alone do not tell a screen reader whether the notes are open.</summary>
    public string ReleaseNotesToggleName => _notesVersion is null
        ? string.Empty
        : Loc.F(ReleaseNotesExpanded ? "Settings.About.WhatsNewExpanded" : "Settings.About.WhatsNewCollapsed", _notesVersion);

    /// <summary>The notes as plain text (Core cuts them to 1500 characters).</summary>
    public string ReleaseNotesText => _notesText ?? string.Empty;

    /// <summary>The release has a page on GitHub to open.</summary>
    public bool HasReleasePage => _notesPage is not null;

    /// <summary>The notes are unfolded.</summary>
    public bool ReleaseNotesExpanded
    {
        get => _notesExpanded;
        private set
        {
            if (Set(ref _notesExpanded, value))
            {
                Raise(nameof(ReleaseNotesShown));
                Raise(nameof(ReleasePageShown));
                Raise(nameof(ReleaseNotesGlyph));
                Raise(nameof(ReleaseNotesToggleName));
            }
        }
    }

    /// <summary>The notes' text shows: there are notes and they are unfolded.</summary>
    public bool ReleaseNotesShown => HasReleaseNotes && ReleaseNotesExpanded;

    /// <summary>"All changes on GitHub" shows: the notes are unfolded and the release has a page.</summary>
    public bool ReleasePageShown => ReleaseNotesShown && HasReleasePage;

    /// <summary>MDI chevron after the "What's new" link: down while folded, up while unfolded.</summary>
    public string ReleaseNotesGlyph => ReleaseNotesExpanded ? "\U000F0143" : "\U000F0140";

    /// <summary>Folds or unfolds the notes.</summary>
    public RelayCommand ToggleReleaseNotes => _toggleReleaseNotes ??= new RelayCommand(() => ReleaseNotesExpanded = !ReleaseNotesExpanded);

    /// <summary>Opens the release page in the browser.</summary>
    public RelayCommand OpenReleasePage => _openReleasePage ??= new RelayCommand(() =>
    {
        if (_notesPage is { } page)
        {
            OpenBrowser(page);
        }
    });

    /// <summary>The browser call, swapped in tests.</summary>
    internal Func<Uri, bool> OpenBrowser { get; set; } = SystemBrowser.Open;
}
