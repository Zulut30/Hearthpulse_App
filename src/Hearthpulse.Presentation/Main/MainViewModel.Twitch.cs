#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using Hearthpulse.Core;
using Hearthpulse.Core.Accounts;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Twitch;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// Settings → Stream → Twitch extension (docs/streaming.md): the switch, the delay, the link's status line and the
/// "Link Twitch" button. The sending itself is <see cref="TwitchPublisher"/>; this only shows it and tells it of a change.
/// </summary>
public sealed partial class MainViewModel
{
    private TwitchPublisher? _twitch;
    private RelayCommand? _linkTwitch;

    /// <summary>Connects the card to the sender the host built (the status line follows its link).</summary>
    public void AttachTwitch(TwitchPublisher twitch)
    {
        _twitch = twitch;
        twitch.Changed += () => DispatchToUi(RaiseTwitch);
        RaiseTwitch();
    }

    /// <summary>The switch: the deck goes to the extension on the player's channel.</summary>
    public bool TwitchExtension
    {
        get => Settings.TwitchExtension;
        set
        {
            if (value == Settings.TwitchExtension)
            {
                return;
            }

            Settings.TwitchExtension = value;
            _twitch?.Kick();
            ChangedStream();
        }
    }

    /// <summary>The delay in seconds, for the slider (whole seconds, 0–600).</summary>
    public double TwitchDelay
    {
        get => Settings.TwitchDelaySeconds;
        set
        {
            var seconds = double.IsFinite(value) ? (int)Math.Round(Math.Clamp(value, 0, AppSettings.MaxTwitchDelaySeconds)) : 0;
            if (seconds == Settings.TwitchDelaySeconds)
            {
                return;
            }

            Settings.TwitchDelaySeconds = seconds;
            Raise(nameof(TwitchDelay));
            Raise(nameof(TwitchDelayText));
            StreamSettingsChanged?.Invoke();
        }
    }

    /// <summary>"30 s" beside the slider.</summary>
    public string TwitchDelayText => Loc.F("Twitch.DelayValue", Settings.TwitchDelaySeconds);

    private TwitchLink? TwitchLinkNow => TwitchExtension ? _twitch?.Link : null;

    /// <summary>The site takes the deck: the status line's dot is green.</summary>
    public bool TwitchConnected => TwitchLinkNow?.State == TwitchLinkState.Connected;

    /// <summary>The status line under the switch; empty while it is off.</summary>
    public string TwitchStatusText => TwitchLinkNow is not { } link
        ? string.Empty
        : link.State switch
        {
            TwitchLinkState.SignedOut => Loc.T("Twitch.Status.SignedOut"),
            TwitchLinkState.SignInAgain => Loc.T("Twitch.Status.SignInAgain"),
            TwitchLinkState.NotLinked => Loc.T("Twitch.Status.NotLinked"),
            TwitchLinkState.NotConfigured => Loc.T("Twitch.Status.NotConfigured"),
            TwitchLinkState.Connected => link.Login is { Length: > 0 } login ? Loc.F("Twitch.Status.Connected", login) : Loc.T("Twitch.Status.ConnectedNoLogin"),
            TwitchLinkState.Error => Loc.F("Twitch.Status.Error", TwitchErrorReason(link.Code)),
            _ => Loc.T("Twitch.Status.Checking"),
        };

    /// <summary>The status line is there (only with the switch on).</summary>
    public bool TwitchStatusShown => TwitchStatusText.Length > 0;

    /// <summary>"Link Twitch" shows while the account has no Twitch linked.</summary>
    public bool TwitchLinkShown => TwitchLinkNow?.State == TwitchLinkState.NotLinked;

    /// <summary>Opens the site's page that links a Twitch account, in the browser.</summary>
    public RelayCommand LinkTwitch => _linkTwitch ??= new RelayCommand(() =>
    {
        if (_twitch?.Link.LinkUrl is { } url)
        {
            SystemBrowser.Open(url);
        }
    });

    internal static string TwitchErrorReason(string? code) => code switch
    {
        "OFFLINE" => Loc.T("Twitch.Error.Offline"),
        "NOT_AVAILABLE" => Loc.T("Twitch.Error.NotAvailable"),
        "RATE_LIMITED" => Loc.T("Twitch.Error.RateLimited"),
        "TWITCH_UNAVAILABLE" => Loc.T("Twitch.Error.TwitchDown"),
        "SERVER_ERROR" => Loc.T("Twitch.Error.Server"),
        _ => Loc.F("Twitch.Error.Refused", code ?? "?"),
    };

    private void RaiseTwitch()
    {
        Raise(nameof(TwitchExtension));
        Raise(nameof(TwitchDelay));
        Raise(nameof(TwitchDelayText));
        Raise(nameof(TwitchConnected));
        Raise(nameof(TwitchStatusText));
        Raise(nameof(TwitchStatusShown));
        Raise(nameof(TwitchLinkShown));
    }
}
