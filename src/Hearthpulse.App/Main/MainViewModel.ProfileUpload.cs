using System.Windows;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.Core.Accounts;

namespace Hearthpulse.App.Main;

public sealed partial class MainViewModel
{
    private ProfileUploader? _uploader;
    private RelayCommand? _uploadPastGames;
    private bool _uploadBackfilling;
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

    /// <summary>Send finished games to the hearthpulse.net profile (<see cref="Core.AppSettings.UploadGamesToProfile"/>).</summary>
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
            UploadMessage = string.Empty;
            UploadSettingChanged?.Invoke();
            _uploader?.Kick();
        }
    }

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
