using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
#if HEARTHPULSE_WPF
using Hearthpulse.App.Overlay;
#else
using Hearthpulse.UI.Overlay;
#endif

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main;
#else
namespace Hearthpulse.UI.Main;
#endif

/// <summary>
/// Settings → Stream, the live bar's streamer button and the tray item (docs/streaming.md). Each control binds to these
/// properties, so a change from any of them shows in the others at once; <see cref="StreamSettingsChanged"/> tells the host
/// to open or close the OBS windows, repaint them and save.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A stream setting changed here (switches, background, the live bar, the tray, a window closed by hand).</summary>
    public event Action? StreamSettingsChanged;

    /// <summary>Streamer mode: no opponent names, BattleTags or ratings on the overlay, the OBS windows and the match header.</summary>
    public bool StreamerMode
    {
        get => Settings.StreamerMode;
        set
        {
            if (value == Settings.StreamerMode)
            {
                return;
            }

            Settings.StreamerMode = value;
            _matchPage?.RefreshStreamerMode();
            Raise(nameof(AccountName));
            ChangedStream();
        }
    }

    /// <summary>The live bar button's tip: the state and what a click does.</summary>
    public string StreamerModeTip => Loc.T(StreamerMode ? "Stream.Mode.On" : "Stream.Mode.Off");

    public bool StreamDeckWindow { get => Settings.StreamDeckWindow; set => SetStreamWindow(StreamWindowSpec.Deck.Id, value); }

    public bool StreamLobbyWindow { get => Settings.StreamLobbyWindow; set => SetStreamWindow(StreamWindowSpec.Lobby.Id, value); }

    public bool StreamOddsWindow { get => Settings.StreamOddsWindow; set => SetStreamWindow(StreamWindowSpec.Odds.Id, value); }

    /// <summary>The OBS windows stay above other windows (Settings and each window's menu).</summary>
    public bool StreamWindowsOnTop
    {
        get => Settings.StreamWindowsOnTop;
        set
        {
            if (value != Settings.StreamWindowsOnTop)
            {
                Settings.StreamWindowsOnTop = value;
                ChangedStream();
            }
        }
    }

    // One bool per background option, for the segment radios (setting false is ignored: the group unchecks the old one).
    public bool StreamBackgroundTheme { get => Settings.StreamBackgroundTheme; set => SetIfChosen(value, StreamBackgrounds.Theme); }

    public bool StreamBackgroundGreen { get => Settings.StreamBackgroundGreen; set => SetIfChosen(value, StreamBackgrounds.Green); }

    public bool StreamBackgroundMagenta { get => Settings.StreamBackgroundMagenta; set => SetIfChosen(value, StreamBackgrounds.Magenta); }

    /// <summary>The OBS help's line about the key filter: "add a Color Key filter … Green"; empty with the theme's background.</summary>
    public string StreamKeyNote => Settings.StreamWindowBackground switch
    {
        StreamBackgrounds.Green => Loc.F("Stream.Help.Key", Loc.T("Stream.Background.Green")),
        StreamBackgrounds.Magenta => Loc.F("Stream.Help.Key", Loc.T("Stream.Background.Magenta")),
        _ => string.Empty,
    };

    /// <summary>Opens or closes an OBS window by its id ("deck", "lobby", "odds"); a window closed by hand comes here too.</summary>
    public void SetStreamWindow(string id, bool on)
    {
        if (StreamWindowSpec.Find(id) is not { } spec || spec.IsOn(Settings) == on)
        {
            return;
        }

        spec.SetOn(Settings, on);
        ChangedStream();
    }

    /// <summary>Picks the background by its name ("Theme", "Green", "Magenta"); false when nothing changed.</summary>
    public bool SetStreamBackground(string background)
    {
        if (!StreamBackgrounds.All.Contains(background, StringComparer.Ordinal) || Settings.StreamWindowBackground == background)
        {
            return false;
        }

        Settings.StreamWindowBackground = background;
        ChangedStream();
        return true;
    }

    /// <summary>Re-reads every stream property (after the settings changed elsewhere, e.g. a reset).</summary>
    public void RefreshStream()
    {
        Raise(nameof(StreamerMode));
        Raise(nameof(StreamerModeTip));
        Raise(nameof(AccountName));
        Raise(nameof(StreamDeckWindow));
        Raise(nameof(StreamLobbyWindow));
        Raise(nameof(StreamOddsWindow));
        Raise(nameof(StreamSessionWindow));
        Raise(nameof(StreamSessionStartText));
        Raise(nameof(StreamWindowsOnTop));
        Raise(nameof(StreamBackgroundTheme));
        Raise(nameof(StreamBackgroundGreen));
        Raise(nameof(StreamBackgroundMagenta));
        Raise(nameof(StreamKeyNote));
        ShowDiscord(_lastSnapshot);
        RaiseDiscord();
        RaiseTwitch();
    }

    private void SetIfChosen(bool value, string background)
    {
        if (value)
        {
            SetStreamBackground(background);
        }
    }

    private void ChangedStream()
    {
        RefreshStream();
        StreamSettingsChanged?.Invoke();
    }
}
