#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>
/// The match player's timeline in both UIs (<c>MatchTimeline</c>), in pixels of the control: where the stretches, event
/// ticks, position line and diamond go, which theme brushes they take, and which step a click or a drag goes to. Each
/// UI builds and draws the children and takes the pointer.
/// </summary>
internal static class MatchTimelineLayout
{
    /// <summary>The tick row: its top, height and the width of a tick's click zone.</summary>
    public const double TickTop = 4;

    public const double TickHeight = 10;

    public const double TickZone = 8;

    /// <summary>The stretch row, under the ticks.</summary>
    public const double SpanTop = 16;

    public const double SpanHeight = 22;

    /// <summary>The bookmark row, under the stretches: its top, height and the width of a bookmark's click zone.</summary>
    public const double BookmarkTop = 40;

    public const double BookmarkHeight = 12;

    public const double BookmarkZone = 12;

    /// <summary>The height the timeline asks for, with the bookmark row even without bookmarks (the bar does not jump).</summary>
    public const double Height = 52;

    /// <summary>The pointer moves this far with the button down before a press scrubs instead of clicking.</summary>
    public const double DragSlop = 4;

    /// <summary>A stretch's fill, edge (null: no border) and text brush keys; the current one in the selected look.</summary>
    public static (string Fill, string? Edge, string Text) SpanLook(MatchTimelineLook look, bool current) =>
        current ? ("AccentContainer", "Accent", "OnAccentContainer") : look switch
        {
            MatchTimelineLook.Mine => ("AccentSoft", null, "Text"),
            MatchTimelineLook.Theirs => ("Accent2Soft", null, "Muted"),
            MatchTimelineLook.Shop => ("Surface3", "Border", "Muted"),
            MatchTimelineLook.Won => ("PositiveSoft", "Positive", "Muted"),
            MatchTimelineLook.Lost => ("NegativeSoft", "Negative", "Muted"),
            MatchTimelineLook.Combat => ("Surface4", "Border", "Muted"),
            _ => ("Surface4", null, "Muted"),
        };

    /// <summary>A tick mark's brush key: our events, theirs, or both players' (neutral).</summary>
    public static string TickBrush(MatchTimelineLook look) => look switch
    {
        MatchTimelineLook.Mine => "Accent",
        MatchTimelineLook.Theirs => "Accent2",
        _ => "Muted",
    };

    /// <summary>
    /// The marker of a "Turn" list entry: our turn, theirs, a won or lost combat; the tavern, the mulligan, the hero choice,
    /// the end and an open combat neutral.
    /// </summary>
    public static string MarkerBrush(MatchTimelineLook look) => look switch
    {
        MatchTimelineLook.Mine => "Accent",
        MatchTimelineLook.Theirs => "Accent2",
        MatchTimelineLook.Won => "Positive",
        MatchTimelineLook.Lost => "Negative",
        _ => "Muted",
    };

    /// <summary>A bookmark mark's brush key: told from the event ticks by its shape and row as well.</summary>
    public const string BookmarkBrush = "Info";

    /// <summary>A bookmarked step's place, 0–1: the middle of its slot, as the position line.</summary>
    public static double BookmarkAt(int frame, int frameMax) => (frame + 0.5) / Math.Max(1, frameMax + 1);

    /// <summary>Where a bookmark's click zone (and its glyph) goes, centred on its step.</summary>
    public static (double X, double Y, double Width, double Height) Bookmark(int frame, int frameMax, double width) =>
        ((BookmarkAt(frame, frameMax) * width) - (BookmarkZone / 2), BookmarkTop, BookmarkZone, BookmarkHeight);

    /// <summary>The width a stretch gets inside its 1 px margins (may be below zero for a tiny one).</summary>
    public static double SpanPixels(MatchTimelineSegment segment, double width) => ((segment.End - segment.Start) * width) - 2;

    /// <summary>A turn's label shows only where it fits; combats have a glyph instead.</summary>
    public static bool ShowsLabel(MatchTimelineSegment segment, double pixels) => segment.Label.Length > 0 && !segment.IsCombat && pixels >= 18;

    /// <summary>A combat's glyph shows only where it fits.</summary>
    public static bool ShowsGlyph(MatchTimelineSegment segment, double pixels) => segment.IsCombat && pixels >= 16;

    /// <summary>Where a stretch goes: its fraction of the width, 1 px in on each side, at least 1 px wide.</summary>
    public static (double X, double Y, double Width, double Height) Span(MatchTimelineSegment segment, double width) =>
        ((segment.Start * width) + 1, SpanTop, Math.Max(1, SpanPixels(segment, width)), SpanHeight);

    /// <summary>Where a tick's click zone goes, centred on its fraction of the width.</summary>
    public static (double X, double Y, double Width, double Height) Tick(MatchTimelineTick tick, double width) =>
        ((tick.At * width) - (TickZone / 2), TickTop, TickZone, TickHeight);

    /// <summary>The position line, from the tick row down through the stretches.</summary>
    public static (double X, double Y, double Width, double Height) Line(double position, double width) =>
        ((position * width) - 1, TickTop, 2, SpanTop + SpanHeight - TickTop);

    /// <summary>The diamond on top of the position line.</summary>
    public static (double X, double Y, double Width, double Height) Diamond(double position, double width) =>
        ((position * width) - 4, 0, 8, 8);

    /// <summary>
    /// A click's step: in the tick row the nearest tick within its zone, on the stretches the first step of the stretch
    /// under the pointer, in the bookmark row the nearest bookmark within its zone, else the slot there
    /// (<see cref="MatchViewModel.FrameAt"/>).
    /// </summary>
    public static int FrameUnder(
        IEnumerable<MatchTimelineTick> ticks, IEnumerable<MatchTimelineSegment> spans, double x, double y, double width, int frameMax,
        IEnumerable<int>? bookmarks = null)
    {
        width = Math.Max(1, width);
        if (y >= BookmarkTop)
        {
            var near = (bookmarks ?? []).Select(f => (Frame: f, Distance: Math.Abs((BookmarkAt(f, frameMax) * width) - x)))
                .Where(b => b.Distance <= BookmarkZone / 2).OrderBy(b => b.Distance).ToList();
            if (near.Count > 0)
            {
                return near[0].Frame;
            }
        }
        else if (y < SpanTop)
        {
            var near = ticks.Where(t => Math.Abs((t.At * width) - x) <= TickZone / 2)
                .OrderBy(t => Math.Abs((t.At * width) - x)).FirstOrDefault();
            if (near is not null)
            {
                return near.Frame;
            }
        }
        else if (spans.FirstOrDefault(s => x >= s.Start * width && x < s.End * width) is { } segment)
        {
            return segment.FirstFrame;
        }

        return MatchViewModel.FrameAt(x / width, frameMax + 1);
    }

    /// <summary>A drag's step: the slot under the pointer.</summary>
    public static int FrameAt(double x, double width, int frameMax) => MatchViewModel.FrameAt(x / Math.Max(1, width), frameMax + 1);
}
