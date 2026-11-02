using Hearthpulse.App.Overlay;
using Hearthpulse.Core.Localization;
using Forms = System.Windows.Forms;

namespace Hearthpulse.App;

/// <summary>
/// The OBS capture windows and streamer mode in the host (docs/streaming.md): the windows' owner, the tray item and its
/// tooltip. Every stream switch (Settings → Stream, the live bar, the tray, a window's own menu) goes through
/// <see cref="Main.MainViewModel.StreamSettingsChanged"/>, which lands here.
/// </summary>
public partial class App
{
    private StreamWindows? _stream;
    private Forms.ToolStripMenuItem? _trayStreamerItem;

    /// <summary>Builds the windows' owner and opens the windows left on (renders build their own, never shown).</summary>
    private void SetupStream()
    {
        _stream = new StreamWindows(_overlayViewModel, _mainViewModel);
        _stream.Moved += OnStreamMoved;
        _mainViewModel.StreamSettingsChanged += OnStreamSettingsChanged;
        Loc.Instance.LanguageChanged += SyncStreamerTray;
        if (_renderDir is null)
        {
            _stream.Sync();
        }
    }

    private void OnStreamMoved()
    {
        _settingsDebounce.Stop();
        _settingsDebounce.Start();
    }

    /// <summary>A stream setting changed: windows, tray and overlay follow, and the settings are saved.</summary>
    private void OnStreamSettingsChanged()
    {
        if (_renderDir is null)
        {
            _stream?.Sync();
        }

        SyncStreamerTray();
        OnSettingsChanged();
    }

    /// <summary>The tray's "Streamer mode" item, a check box like "Overlay" (its Tag retitles it after a language change).</summary>
    private Forms.ToolStripMenuItem StreamerTrayItem()
    {
        _trayStreamerItem = new Forms.ToolStripMenuItem(Loc.T("Stream.Tray.Mode"), null, (_, _) => _mainViewModel.StreamerMode = !_mainViewModel.StreamerMode)
        {
            Checked = _settings.StreamerMode,
            Tag = "Stream.Tray.Mode",
        };
        return _trayStreamerItem;
    }

    /// <summary>The tray icon's tooltip says when streamer mode is on (the indicator for the streamer, never on stream).</summary>
    private string StreamerTrayTip() => _settings.StreamerMode ? Loc.T("Stream.Tray.Tip") : "Hearthpulse";

    private void SyncStreamerTray()
    {
        if (_trayStreamerItem is not null)
        {
            _trayStreamerItem.Checked = _settings.StreamerMode;
        }

        if (_tray is not null)
        {
            _tray.Text = StreamerTrayTip();
        }
    }
}
