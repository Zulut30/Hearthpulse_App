using Hearthpulse.Core;
using Hearthpulse.Core.Localization;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>What an OBS capture window shows (docs/streaming.md).</summary>
public enum StreamWindowState
{
    /// <summary>No game of the window's mode: the quiet "Hearthpulse" tile on the background.</summary>
    Idle,

    /// <summary>A game of its mode with nothing to show yet (hero pick, between fights): the background only.</summary>
    Waiting,

    /// <summary>The panel, as on the overlay.</summary>
    Shown,
}

/// <summary>
/// One of the three OBS capture windows: its id (the overlay panel's, also the key in
/// <see cref="AppSettings.StreamWindows"/>), its name in the title, and its default and smallest size and default spot in
/// device-independent pixels (design: the panel at scale 1 plus the window's margins of 8).
/// </summary>
public sealed record StreamWindowSpec(string Id, string NameKey, double Width, double Height, double MinWidth, double MinHeight, double Left, double Top) : IImmutableView
{
    /// <summary>The window's margin around the panel, each side.</summary>
    public const double Padding = 8;

    /// <summary>The panel 252 wide; 720 holds 23 rows at scale 1, a longer deck shrinks as a whole.</summary>
    public static readonly StreamWindowSpec Deck = new("deck", "Stream.Window.Deck", 268, 720, 168, 200, 24, 24);

    /// <summary>The panel 230 × about 290: eight players at scale 1.</summary>
    public static readonly StreamWindowSpec Lobby = new("lobby", "Stream.Window.Lobby", 246, 306, 156, 120, 308, 24);

    /// <summary>The panel 250 × about 110, plus 14 for the note wrapping onto a second line.</summary>
    public static readonly StreamWindowSpec Odds = new("odds", "Stream.Window.Odds", 266, 140, 166, 84, 570, 24);

    public static readonly IReadOnlyList<StreamWindowSpec> All = [Deck, Lobby, Odds];

    /// <summary>The window title OBS finds it by: "Hearthpulse — Deck" (in the interface language).</summary>
    public string Title => Loc.F("Stream.Window.Title", Loc.T(NameKey));

    /// <summary>The window is switched on in Settings → Stream.</summary>
    public bool IsOn(AppSettings settings) => Id switch
    {
        "deck" => settings.StreamDeckWindow,
        "lobby" => settings.StreamLobbyWindow,
        _ => settings.StreamOddsWindow,
    };

    public void SetOn(AppSettings settings, bool on)
    {
        switch (Id)
        {
            case "deck":
                settings.StreamDeckWindow = on;
                break;
            case "lobby":
                settings.StreamLobbyWindow = on;
                break;
            default:
                settings.StreamOddsWindow = on;
                break;
        }
    }

    public static StreamWindowSpec? Find(string id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>Any OBS window is on: the overlay's model then reads the game even with the overlay off.</summary>
    public static bool AnyOn(AppSettings settings) => All.Any(s => s.IsOn(settings));
}

/// <summary>A rectangle in device-independent pixels, without the UI frameworks' types.</summary>
public readonly record struct StreamRect(double X, double Y, double Width, double Height)
{
    /// <summary>The part of this rectangle inside <paramref name="other"/> (empty: zero width or height).</summary>
    public StreamRect Intersect(StreamRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + Width, other.X + other.Width);
        var bottom = Math.Min(Y + Height, other.Y + other.Height);
        return new StreamRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

/// <summary>Where an OBS window opens and what is saved when it moves (by its id in <see cref="AppSettings.StreamWindows"/>).</summary>
public static class StreamWindowLayout
{
    /// <summary>A saved window must show at least this much (each way) on some work area, or it opens at its default spot.</summary>
    public const double MinVisible = 40;

    /// <summary>
    /// The bounds to open <paramref name="spec"/> at: the saved ones (never below the minimum size) while they still show
    /// 40 × 40 on one of <paramref name="workAreas"/>; else the default spot at the top left of
    /// <paramref name="primary"/>, keeping a saved size (a monitor that went away).
    /// </summary>
    public static StreamRect Place(StreamWindowSpec spec, StreamWindowPlacement? saved, IReadOnlyList<StreamRect> workAreas, StreamRect primary)
    {
        var width = Math.Max(spec.MinWidth, saved?.Width ?? spec.Width);
        var height = Math.Max(spec.MinHeight, saved?.Height ?? spec.Height);
        if (saved is not null)
        {
            var rect = new StreamRect(saved.X, saved.Y, width, height);
            if (workAreas.Any(area => rect.Intersect(area) is { Width: >= MinVisible, Height: >= MinVisible }))
            {
                return rect;
            }
        }

        return new StreamRect(primary.X + spec.Left, primary.Y + spec.Top, width, height);
    }

    /// <summary>The default size, where the window is (the menu's "Original size").</summary>
    public static StreamRect OriginalSize(StreamWindowSpec spec, StreamRect current) => current with { Width = spec.Width, Height = spec.Height };

    /// <summary>Keeps <paramref name="bounds"/> as the window's place in <paramref name="settings"/>.</summary>
    public static void Save(AppSettings settings, StreamWindowSpec spec, StreamRect bounds)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        settings.StreamWindows[spec.Id] = new StreamWindowPlacement { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
    }
}

/// <summary>
/// The key colours of the OBS windows' "Green" and "Magenta" backgrounds: exactly the presets of OBS's Color Key and
/// Chroma Key filters. They are a signal to OBS, not interface colours: the same in every theme and never through the
/// theme engine (so not in Theme.xaml / Theme.axaml). Each UI makes its brush from these bytes.
/// </summary>
public static class StreamBackdrop
{
    public static readonly (byte R, byte G, byte B) Green = (0x00, 0xFF, 0x00);

    public static readonly (byte R, byte G, byte B) Magenta = (0xFF, 0x00, 0xFF);

    /// <summary>The key colour of a <see cref="AppSettings.StreamWindowBackground"/>; null for the theme's background.</summary>
    public static (byte R, byte G, byte B)? KeyColor(string background) => background switch
    {
        StreamBackgrounds.Green => Green,
        StreamBackgrounds.Magenta => Magenta,
        _ => null,
    };
}
