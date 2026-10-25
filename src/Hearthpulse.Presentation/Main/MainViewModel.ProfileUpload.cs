#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

public sealed partial class MainViewModel
{
    private ProfileUploader? _uploader;
    private RelayCommand? _uploadPastGames;
    private RelayCommand? _openPrivacy;
    private RelayCommand? _openTrackerData;
    private Func<string> _uploadMessage = () => string.Empty;

    /// <summary>Raised when the player turns sending games to the profile on or off; the host saves the settings.</summary>
    public event Action? UploadSettingChanged;

    /// <summary>Connects the account card's "Send games to my profile" switch and status to the uploader.</summary>
    public void AttachUploader(ProfileUploader uploader)
    {
        _uploader = uploader;
        uploader.StatusChanged += () => DispatchToUi(() => Raise(nameof(UploadStatus)));
        Raise(nameof(UploadStatus));
    }

    /// <summary>
    /// Send finished games to the hearthpulse.net profile (<see cref="Core.AppSettings.UploadGamesToProfile"/>): on by
    /// default while signed in, the switch turns it off.
    /// </summary>
    public bool UploadGames
    {
        get => Settings.UploadGamesToProfile;
        set
        {
            if (Settings.UploadGamesToProfile == value)
            {
                return;
            }

            Settings.UploadGamesToProfile = value;
            Raise();
            Raise(nameof(UploadStatus));
            ShowUploadMessage(() => string.Empty);
            UploadSettingChanged?.Invoke();
            _uploader?.Kick();
        }
    }

    /// <summary>The site's privacy policy.</summary>
    public RelayCommand OpenPrivacy => _openPrivacy ??= new RelayCommand(() =>
    {
        if (_account?.OpenPrivacy() == false)
        {
            var url = _account.PrivacyUrl.AbsoluteUri;
            ShowUploadMessage(() => Loc.F("Upload.Message.PrivacyFailed", url));
        }
    });

    /// <summary>"App data" on the account page: export or delete what the tracker sent.</summary>
    public RelayCommand OpenTrackerData => _openTrackerData ??= new RelayCommand(() =>
    {
        if (_account?.OpenTrackerData() == false)
        {
            var url = _account.TrackerDataUrl.AbsoluteUri;
            ShowUploadMessage(() => Loc.F("Upload.Message.DataFailed", url));
        }
    });

    /// <summary>Queued games, or why sending waits; empty when there is nothing to say.</summary>
    public string UploadStatus => UploadGames ? _uploader?.Status ?? string.Empty : string.Empty;

    /// <summary>The answer to the "Send past games" button or a link that did not open, in the interface language.</summary>
    public string UploadMessage => _uploadMessage();

    private void ShowUploadMessage(Func<string> text)
    {
        _uploadMessage = text;
        Raise(nameof(UploadMessage));
    }

    /// <summary>
    /// Sends every past game of the history that is not on the profile yet. The uploader reads the history a page at a
    /// time; the status line beside the button counts what still waits.
    /// </summary>
    public RelayCommand UploadPastGames => _uploadPastGames ??= new RelayCommand(() =>
    {
        if (_uploader is null)
        {
            return;
        }

        var key = !UploadGames
            ? "Upload.Message.TurnOnFirst"
            : _uploader.StartBackfill()
                ? "Upload.Message.BackfillStarted"
                : "Upload.Message.SignInFirst";
        ShowUploadMessage(() => Loc.T(key));
    });
}
