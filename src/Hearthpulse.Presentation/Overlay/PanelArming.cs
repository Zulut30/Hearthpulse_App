using System.Globalization;
using Hearthpulse.Core;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Overlay;
#else
namespace Hearthpulse.UI.Overlay;
#endif

/// <summary>
/// What the pointer does to the panel under it, decided on each overlay tick the same way in both overlays. Ctrl held,
/// or panels unlocked, arms the panel: it takes the mouse and moves like in layout mode. A locked panel the pointer
/// rests on offers the "Unlock panels" button. Panels on the game's own cards (mulligan, picks, Discover, tavern) arm
/// only with Ctrl and never offer the button, so unlocked panels never take a click meant for a card. Each change
/// gives one log line, so hearthpulse.log shows whether Ctrl ever reached a panel.
/// </summary>
public sealed class PanelArming
{
    /// <summary>How long the pointer rests on a locked panel before its unlock button shows.</summary>
    public static readonly TimeSpan OfferDelay = TimeSpan.FromMilliseconds(400);

    private string? _over;
    private DateTime _overSince;
    private string? _armed;
    private bool _control;

    /// <summary>
    /// One tick: <paramref name="over"/> is the Id of the topmost shown panel under the pointer (null over empty space,
    /// outside the game and in layout mode), <paramref name="onGameCards"/> says it lies on the game's cards.
    /// </summary>
    /// <returns>
    /// Whether that panel is armed, whether it shows the lock button ("Unlock panels", or "Lock panels" once
    /// unlocked), and the log line when something changed (null otherwise).
    /// </returns>
    public (bool Armed, bool Offered, string? Note) Update(string? over, bool onGameCards, bool controlDown, bool unlocked, DateTime now)
    {
        if (over != _over)
        {
            _over = over;
            _overSince = now;
        }

        var armed = over is not null && (controlDown || (unlocked && !onGameCards));
        var offered = over is not null && !onGameCards && (armed || now - _overSince >= OfferDelay);
        string? note = null;
        var armedId = armed ? over : null;
        if (armedId != _armed)
        {
            note = armedId is not null ? $"Overlay panel {armedId} armed ({(controlDown ? "Ctrl" : "panels unlocked")})" : $"Overlay panel {_armed} released";
            _armed = armedId;
        }
        else if (controlDown && !_control && over is null)
        {
            // Ctrl reached the overlay but not a panel: told apart in the log from Ctrl never read at all.
            note = "Overlay: Ctrl held with no panel under the pointer";
        }

        _control = controlDown;
        return (armed, offered, note);
    }

    /// <summary>
    /// One overlay tick: finds the topmost panel under the pointer (later panels draw over earlier ones; none in layout
    /// mode, or with the pointer outside, <paramref name="under"/> null) and arms it as <see cref="Apply"/> does.
    /// </summary>
    /// <param name="under">Whether the pointer is on a shown panel, its tag strip included while the tag is up.</param>
    public (bool Armed, bool Dragging) Tick<TPanel>(IReadOnlyList<TPanel> panels, Func<TPanel, bool>? under, bool editing, bool controlDown, bool unlocked, DateTime now)
        where TPanel : class, IArmablePanel
    {
        ArgumentNullException.ThrowIfNull(panels);
        TPanel? over = null;
        if (under is not null && !editing)
        {
            for (var i = panels.Count - 1; i >= 0 && over is null; i--)
            {
                if (under(panels[i]))
                {
                    over = panels[i];
                }
            }
        }

        return Apply(panels, over, editing, controlDown, unlocked, now);
    }

    /// <summary>
    /// One tick for every panel of an overlay: arms <paramref name="over"/> (the topmost shown panel under the pointer,
    /// found by the overlay; null over empty space and in layout mode) or offers its lock button as <see cref="Update"/>
    /// decides, takes both from the other panels, and keeps a panel being dragged armed until its button comes up (not in
    /// layout mode, where every panel moves anyway). The log line goes to hearthpulse.log.
    /// </summary>
    /// <returns>Whether <paramref name="over"/> is armed, and whether any panel is being dragged or resized.</returns>
    public (bool Armed, bool Dragging) Apply(IReadOnlyList<IArmablePanel> panels, IArmablePanel? over, bool editing, bool controlDown, bool unlocked, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(panels);
        var (armed, offered, note) = Update(over?.Id, over?.SitsOnGame == true, controlDown, unlocked, now);
        if (note is not null)
        {
            Diag.Debug(note);
        }

        var dragging = false;
        foreach (var panel in panels)
        {
            var arm = (armed && panel == over) || (panel.IsDragging && !editing);
            dragging |= panel.IsDragging;
            if (panel.IsDragArmed != arm)
            {
                panel.IsDragArmed = arm;
            }

            var offer = offered && panel == over;
            if (panel.OffersLock != offer)
            {
                panel.OffersLock = offer;
            }
        }

        return (armed, dragging);
    }

    /// <summary>The log line for a drag the player starts on a panel (move, or resize from the corner marker).</summary>
    public static string DragStarted(string id, bool resizing) => $"Overlay panel {id} drag started ({(resizing ? "corner, resize" : "move")})";

    /// <summary>The log line for a panel's new spot and size (a drag, a wheel step or a button), or its reset.</summary>
    public static string Placed(string id, PanelPlacement? placement) => placement is null
        ? $"Overlay panel {id} reset to its default spot"
        : string.Create(CultureInfo.InvariantCulture, $"Overlay panel {id} placed at {placement.X:0.###}, {placement.Y:0.###}, size {placement.Scale:0.##}");
}

/// <summary>What <see cref="PanelArming.Apply"/> reads and sets on a movable panel (each UI's <c>MovablePanel</c>).</summary>
public interface IArmablePanel
{
    /// <summary>The panel's settings key ("deck", "opponent"...).</summary>
    string Id { get; }

    /// <summary>The panel lies on the game's own cards (mulligan, picks, Discover, tavern): only Ctrl arms it.</summary>
    bool SitsOnGame { get; }

    /// <summary>The player is moving or resizing it.</summary>
    bool IsDragging { get; }

    /// <summary>It takes the mouse and moves as in layout mode.</summary>
    bool IsDragArmed { get; set; }

    /// <summary>Its tag shows the lock button.</summary>
    bool OffersLock { get; set; }
}
