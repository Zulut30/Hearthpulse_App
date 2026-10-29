namespace Hearthpulse.Core.Replay;

/// <summary>
/// What the player bar moves along: every step as a mark (the left/right keys stop on events, Enter/Backspace go
/// through every step) and the whole-game timeline (the mulligan and game turns in Constructed; the tavern and the
/// combats in Battlegrounds). Built on first use from the page's steps, in the interface language of the page.
/// </summary>
public sealed partial class MatchPage
{
    private IReadOnlyList<MatchStepMark>? _marks;
    private IReadOnlyList<MatchTimelineSpan>? _timeline;

    /// <summary>One mark per step: Constructed from the replay frames, Battlegrounds from its steps.</summary>
    public IReadOnlyList<MatchStepMark> Marks => _marks ??= Replay is { } replay ? ConstructedMarks(replay) : BattlegroundsMarks();

    /// <summary>The timeline's stretches: the mulligan and the game turns (as <see cref="Turns"/>), or the tavern and combats.</summary>
    public IReadOnlyList<MatchTimelineSpan> Timeline => _timeline ??= Replay is not null ? ConstructedSpans() : BattlegroundsSpans();

    /// <summary>The nearest event step after (<paramref name="direction"/> &gt; 0) or before <paramref name="frame"/>, else the frame.</summary>
    public int EventFrom(int frame, int direction) => EventFrom(Marks, frame, direction);

    /// <summary>
    /// The first step of the next (or previous) stretch that is not a combat - a game turn, the mulligan or a
    /// Battlegrounds tavern - from the stretch holding <paramref name="frame"/>; else the frame.
    /// </summary>
    public int TurnFrom(int frame, int direction) => TurnFrom(Timeline, frame, direction);

    internal static int EventFrom(IReadOnlyList<MatchStepMark> marks, int frame, int direction)
    {
        var found = frame;
        foreach (var mark in marks)
        {
            if (mark.IsEvent && (direction > 0
                    ? mark.Frame > frame && (found == frame || mark.Frame < found)
                    : mark.Frame < frame && (found == frame || mark.Frame > found)))
            {
                found = mark.Frame;
            }
        }

        return found;
    }

    internal static int TurnFrom(IReadOnlyList<MatchTimelineSpan> spans, int frame, int direction)
    {
        // The stretch on screen: the last one starting at or before the frame.
        var here = -1;
        foreach (var span in spans)
        {
            if (span.FirstFrame <= frame && span.FirstFrame > here)
            {
                here = span.FirstFrame;
            }
        }

        var found = frame;
        foreach (var span in spans)
        {
            if (span.Phase != MatchPhase.Combat && (direction > 0
                    ? span.FirstFrame > here && (found == frame || span.FirstFrame < found)
                    : span.FirstFrame < here && (found == frame || span.FirstFrame > found)))
            {
                found = span.FirstFrame;
            }
        }

        return found;
    }

    private IReadOnlyList<MatchStepMark> ConstructedMarks(MatchReplay replay) =>
        replay.Frames
            .Select(f => new MatchStepMark(f.Index, f.Turn, PhaseOf(f), KindOf(f.Action.Kind), f.Action.Player == ReplayPlayer.Local,
                f.Action.Kind is not (ReplayActionKind.Start or ReplayActionKind.TurnStart),
                Details(f.Action) is { Length: > 0 } details ? $"{Describe(f.Action)} · {details}" : Describe(f.Action)))
            .ToList();

    private IReadOnlyList<MatchTimelineSpan> ConstructedSpans() =>
        Turns.Select(t => new MatchTimelineSpan(t.FirstFrame, t.LastFrame, t.Number, t.Mulligan ? MatchPhase.Mulligan : MatchPhase.Turn, t.Local, t.Label, t.Tooltip))
            .ToList();

    private static MatchPhase PhaseOf(ReplayFrame frame) => frame.Action.Kind switch
    {
        ReplayActionKind.Start => MatchPhase.Start,
        ReplayActionKind.End => MatchPhase.End,
        _ when frame.Mulligan => MatchPhase.Mulligan,
        _ => MatchPhase.Turn,
    };

    private static MatchStepKind KindOf(ReplayActionKind kind) => kind switch
    {
        ReplayActionKind.Start => MatchStepKind.Start,
        ReplayActionKind.Mulligan => MatchStepKind.Mulligan,
        ReplayActionKind.TurnStart => MatchStepKind.TurnStart,
        ReplayActionKind.Play => MatchStepKind.Play,
        ReplayActionKind.HeroPower => MatchStepKind.HeroPower,
        ReplayActionKind.Use => MatchStepKind.Use,
        ReplayActionKind.Attack => MatchStepKind.Attack,
        ReplayActionKind.Choice => MatchStepKind.Choice,
        _ => MatchStepKind.End,
    };
}
