using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Accounts;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    /// <summary>
    /// What goes to the profile, said the same way in the question and under the switch (docs/profile-upload.md).
    /// </summary>
    public const string UploadWhat =
        "After each game: mode, start time and duration, result, place or rank, rating, heroes and classes, your deck name and code, " +
        "final board, combats, the heroes you were offered, who went first and the opponent's archetype when the cards they played showed it. " +
        "Never your BattleTag, your opponent's name or anything hidden in the game.";

    /// <summary><see cref="UploadWhat"/> for bindings.</summary>
    public string UploadWhatText => UploadWhat;

    private ProfileUploader? _uploader;
    private RelayCommand? _uploadPastGames;
    private RelayCommand? _answerUpload;
    private RelayCommand? _openPrivacy;
    private RelayCommand? _openTrackerData;
    private string _uploadMessage = string.Empty;

    /// <summary>Raised when the player turns sending games to the profile on or off; the host saves the settings.</summary>
    public event Action? UploadSettingChanged;

    /// <summary>Connects the account card's "Send games to my profile" switch and status to the uploader.</summary>
    public void AttachUploader(ProfileUploader uploader)
    {
        _uploader = uploader;
        uploader.StatusChanged += () => Application.Current?.Dispatcher.BeginInvoke(() => Raise(nameof(UploadStatus)));
        Raise(nameof(UploadStatus));
    }

    /// <summary>
    /// Send finished games to the hearthpulse.net profile (<see cref="Core.AppSettings.UploadGamesToProfile"/>). Off until
    /// the player turns it on; turning it on or off also answers the one-time question.
    /// </summary>
    public bool UploadGames
    {
        get => Settings.UploadGamesToProfile == true;
        set
        {
            if (Settings.UploadGamesToProfile == value)
            {
                return;
            }

            Settings.UploadGamesToProfile = value;
            Raise();
            Raise(nameof(UploadStatus));
            Raise(nameof(AskUpload));
            UploadMessage = string.Empty;
            UploadSettingChanged?.Invoke();
            _uploader?.Kick();
        }
    }

    /// <summary>
    /// Signed in and never asked: the one-time question whether to send games (after signing in, and once for players
    /// who signed in before the feature). Waits while the stats pool question is up, so the two never overlap.
    /// </summary>
    public bool AskUpload => AccountSignedIn && Settings.UploadGamesToProfile is null && !AskPoolConsent;

    /// <summary>The question's buttons: "yes" turns sending on, anything else is "Not now" (off, the switch stays in Settings).</summary>
    public RelayCommand AnswerUpload => _answerUpload ??= new RelayCommand(p => UploadGames = p as string == "yes");

    /// <summary>The site's privacy policy.</summary>
    public RelayCommand OpenPrivacy => _openPrivacy ??= new RelayCommand(() =>
    {
        if (_account?.OpenPrivacy() == false)
        {
            UploadMessage = "Couldn't open your browser. The policy is at " + _account.PrivacyUrl.AbsoluteUri;
        }
    });

    /// <summary>"App data" on the account page: export or delete what the tracker sent.</summary>
    public RelayCommand OpenTrackerData => _openTrackerData ??= new RelayCommand(() =>
    {
        if (_account?.OpenTrackerData() == false)
        {
            UploadMessage = "Couldn't open your browser. Your data is at " + _account.TrackerDataUrl.AbsoluteUri;
        }
    });

    /// <summary>Queued games, or why sending waits; empty when there is nothing to say.</summary>
    public string UploadStatus => UploadGames ? _uploader?.Status ?? string.Empty : string.Empty;

    /// <summary>The answer to the "Send past games" button.</summary>
    public string UploadMessage { get => _uploadMessage; private set => Set(ref _uploadMessage, value); }

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

        UploadMessage = !UploadGames
            ? "Turn on sending games first."
            : _uploader.StartBackfill()
                ? "Sending your past games. Games already on your profile are skipped."
                : "Sign in to send your past games.";
    });
}
