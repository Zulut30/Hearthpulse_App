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

/// <summary>Settings → Stream, the "Session" window card (proposal B1): its switch and "Start a new session".</summary>
public sealed partial class MainViewModel
{
    public bool StreamSessionWindow { get => Settings.StreamSessionWindow; set => SetStreamWindow(StreamWindowSpec.Session.Id, value); }

    /// <summary>"Counting since 18:04" after a reset; empty while the session follows the ladder session's rule.</summary>
    public string StreamSessionStartText => Settings.StreamSessionStart is { } start ? Loc.F("Stream.Session.Counting", start) : string.Empty;

    /// <summary>"Start a new session": the window counts from now (rank, rating and record start over).</summary>
    public void ResetStreamSession()
    {
        Settings.StreamSessionStart = DateTime.Now;
        ChangedStream();
    }
}
