using Avalonia.Threading;
using Hearthpulse.UI.Infrastructure;
using Hearthpulse.Core.Accounts;

namespace Hearthpulse.UI.Main;

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
    private bool _uploadBackfilling;
    private string _uploadMessage = string.Empty;

    /// <summary>Raised when the player turns sending games to the profile on or off; the host saves the settings.</summary>
    public event Action? UploadSettingChanged;

    /// <summary>Connects the account card's "Send games to my profile" switch and status to the uploader.</summary>
    public void AttachUploader(ProfileUploader uploader)
    {
        _uploader = uploader;
        uploader.StatusChanged += () => Dispatcher.UIThread.Post(() => Raise(nameof(UploadStatus)));
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
    /// who signed in before the feature).
    /// </summary>
    public bool AskUpload => AccountSignedIn && Settings.UploadGamesToProfile is null;

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

    /// <summary>Queues every past game of the history that is not on the profile yet.</summary>
    public RelayCommand UploadPastGames => _uploadPastGames ??= new RelayCommand(async () =>
    {
        if (_uploader is null || _uploadBackfilling)
        {
            return;
        }

        if (!UploadGames)
        {
            UploadMessage = "Turn on sending games first.";
            return;
        }

        _uploadBackfilling = true;
        UploadMessage = "Looking through your history…";
        try
        {
            var count = await _uploader.BackfillAsync();
            UploadMessage = count switch
            {
                0 => "Every past game is already on your profile.",
                1 => "1 past game queued. It is being sent now.",
                _ => $"{count} past games queued. They are being sent now.",
            };
        }
        catch (Exception ex)
        {
            Core.Diag.Error("profile upload: backfill failed", ex);
            UploadMessage = "Couldn't read your history.";
        }
        finally
        {
            _uploadBackfilling = false;
        }
    });
}
