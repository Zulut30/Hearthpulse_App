namespace Hearthpulse.Core;

/// <summary>
/// The main window's life beside the tray, the same in both hosts (they own the window and its timer; this decides).
/// It is built when first opened, let go once it has been hidden in the tray for
/// <see cref="MemoryTrimmer.HiddenWindowRelease"/> and no game is being played, and built again where it was. A window the
/// player closed for good (no tray) is forgotten, so opening builds a new one instead of showing a closed one.
/// </summary>
/// <param name="placeOf">Where a window is (page, tabs, bounds), taken when it is let go or closed.</param>
public sealed class MainWindowLife<TWindow, TPlace>(Func<TWindow, TPlace> placeOf)
    where TWindow : class
    where TPlace : class
{
    private DateTime? _hiddenAt;

    /// <summary>The window, or null while there is none: not built yet, let go or closed.</summary>
    public TWindow? Window { get; private set; }

    /// <summary>Where the last window was, for the next one.</summary>
    public TPlace? Place { get; private set; }

    /// <summary>The window to show: the one there is, or a new one from <paramref name="build"/> on the last place.</summary>
    public TWindow Open(Func<TPlace?, TWindow> build)
    {
        _hiddenAt = null;
        return Window ??= build(Place);
    }

    /// <summary><paramref name="window"/> went into the tray at <paramref name="now"/>.</summary>
    public void Hidden(TWindow window, DateTime now)
    {
        if (ReferenceEquals(window, Window))
        {
            _hiddenAt ??= now;
        }
    }

    /// <summary><paramref name="window"/> is on screen again.</summary>
    public void Shown(TWindow window)
    {
        if (ReferenceEquals(window, Window))
        {
            _hiddenAt = null;
        }
    }

    /// <summary>Whether the window waits in the tray to be let go (or be shown again).</summary>
    public bool Waiting => Window is not null && _hiddenAt is not null;

    /// <summary>
    /// Whether to let the window go now: hidden long enough, and no game being played. Closing every page it built runs on
    /// the UI thread that also draws the overlay, so mid-match it waits for the end of the game.
    /// </summary>
    public bool ReleaseDue(DateTime now, bool inGame) =>
        Window is not null && _hiddenAt is { } at && now - at >= MemoryTrimmer.HiddenWindowRelease && !inGame;

    /// <summary>
    /// Lets the window go (let go in the tray, or rebuilt for a new theme): its place is kept, and the window is returned
    /// for the host to close.
    /// </summary>
    public TWindow? Release()
    {
        if (Window is not { } window)
        {
            return null;
        }

        Place = placeOf(window);
        Window = null;
        _hiddenAt = null;
        return window;
    }

    /// <summary><paramref name="window"/> is closing (the tray or for good): note where it is while it still knows.</summary>
    public void Closing(TWindow window)
    {
        if (ReferenceEquals(window, Window))
        {
            Place = placeOf(window);
        }
    }

    /// <summary><paramref name="window"/> was closed for good by the player: forgotten.</summary>
    public void Closed(TWindow window)
    {
        if (ReferenceEquals(window, Window))
        {
            Window = null;
            _hiddenAt = null;
        }
    }
}
