using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>What the match page's player moves along: a mark per step, the whole-game timeline, event and turn jumps.</summary>
public sealed class MatchTimelineTests : IDisposable
{
    /// <summary>
    /// A short Constructed game: the mulligan, our turn 1 (a minion), their turn 1 (a minion and an attack), our turn 2 (the
    /// hero power), the end. Frames: 0 start, 1 mulligan, 2 turn start, 3 play, 4 turn start, 5 play, 6 attack, 7 turn
    /// start, 8 hero power, 9 end.
    /// </summary>
    internal static readonly string[] Events =
    [
        "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30", "E 6 L PLAY 0 HERO_08bp HERO_POWER",
        "S BEGIN_MULLIGAN", "S MAIN_READY",
        "V L RESOURCES 1", "B", "E 3 L PLAY 1 CS2_120 MINION ATK=2 HEALTH=3", "P 3 -",
        "T 2 O 30", "V O RESOURCES 2", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "P 4 -", "A 4 3", "V 3 DAMAGE 4",
        "T 3 L 60", "U 6 4",
        "R WON",
    ];

    private readonly ReplayStore _store = new(Path.Combine(Path.GetTempPath(), $"hp-match-timeline-{Guid.NewGuid():N}"));

    public void Dispose()
    {
        if (Directory.Exists(_store.Directory))
        {
            Directory.Delete(_store.Directory, recursive: true);
        }
    }

    internal static GameRecord Game(string? replay) => new()
    {
        Key = "20261029120000|timeline|GT_RANKED",
        StartedAt = new DateTime(2026, 10, 29, 12, 0, 0),
        EndedAt = new DateTime(2026, 10, 29, 12, 6, 0),
        Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Win",
        PlayerClass = "MAGE", OpponentClass = "WARRIOR", Turns = 3, Replay = replay,
    };

    private MatchPage Page()
    {
        var name = _store.Save(Game(null).Key, ReplayFile.Write(new ReplayHeader(), Events));
        var page = MatchPage.Load(Game(name), _store, CardDb.Empty, null);
        Assert.Null(page.Missing);
        return page;
    }

    [Fact]
    public void Constructed_marks_follow_the_frames_and_turn_starts_are_not_events()
    {
        var page = Page();
        var frames = page.Replay!.Frames;

        Assert.Equal(10, frames.Count);
        Assert.Equal(frames.Select(f => f.Index), page.Marks.Select(m => m.Frame));
        Assert.Equal(
            [MatchStepKind.Start, MatchStepKind.Mulligan, MatchStepKind.TurnStart, MatchStepKind.Play, MatchStepKind.TurnStart, MatchStepKind.Play,
             MatchStepKind.Attack, MatchStepKind.TurnStart, MatchStepKind.HeroPower, MatchStepKind.End],
            page.Marks.Select(m => m.Kind));
        Assert.Equal([1, 3, 5, 6, 8, 9], page.Marks.Where(m => m.IsEvent).Select(m => m.Frame));
        Assert.Equal([MatchPhase.Start, MatchPhase.Mulligan], page.Marks.Take(2).Select(m => m.Phase));
        Assert.Equal(MatchPhase.End, page.Marks[^1].Phase);
        Assert.All(page.Marks.Skip(2).SkipLast(1), m => Assert.Equal(MatchPhase.Turn, m.Phase));

        // Ours and theirs by the acting player; the game turn as in the frames.
        Assert.Equal([3, 8], page.Marks.Where(m => m.Local && m.Kind is MatchStepKind.Play or MatchStepKind.HeroPower).Select(m => m.Frame));
        Assert.False(page.Marks[6].Local);
        Assert.Equal(frames.Select(f => f.Turn), page.Marks.Select(m => m.Turn));

        // A caption says what happened, as the turn's action list does.
        Assert.Equal(page.Describe(frames[6].Action), page.Marks[6].Caption);
        Assert.Contains(page.Actions(6), a => a.Frame == 6 && a.Text == page.Marks[6].Caption);
    }

    [Fact]
    public void Constructed_timeline_is_the_mulligan_and_the_turns()
    {
        var page = Page();

        Assert.Equal(page.Turns.Count, page.Timeline.Count);
        Assert.Equal([(0, 1), (2, 3), (4, 6), (7, 9)], page.Timeline.Select(s => (s.FirstFrame, s.LastFrame)));
        Assert.Equal([MatchPhase.Mulligan, MatchPhase.Turn, MatchPhase.Turn, MatchPhase.Turn], page.Timeline.Select(s => s.Phase));
        Assert.Equal([false, true, false, true], page.Timeline.Select(s => s.Local));
        Assert.Equal(["M", "1", "1", "2"], page.Timeline.Select(s => s.Label));
        Assert.Equal(page.Turns.Select(t => t.Tooltip), page.Timeline.Select(s => s.Tooltip));
        Assert.Same(page.Timeline, page.Timeline);
    }

    [Fact]
    public void Event_and_turn_jumps_on_a_constructed_game()
    {
        var page = Page();

        // Events skip the start and turn starts.
        Assert.Equal(1, page.EventFrom(0, +1));
        Assert.Equal(3, page.EventFrom(1, +1));
        Assert.Equal(5, page.EventFrom(3, +1));
        Assert.Equal(5, page.EventFrom(4, +1));
        Assert.Equal(3, page.EventFrom(5, -1));
        Assert.Equal(1, page.EventFrom(2, -1));
        Assert.Equal(9, page.EventFrom(9, +1));
        Assert.Equal(0, page.EventFrom(0, -1));

        // Turns: the start of the next or previous stretch, from anywhere in the current one.
        Assert.Equal(2, page.TurnFrom(0, +1));
        Assert.Equal(4, page.TurnFrom(3, +1));
        Assert.Equal(7, page.TurnFrom(5, +1));
        Assert.Equal(9, page.TurnFrom(9, +1));
        Assert.Equal(2, page.TurnFrom(5, -1));
        Assert.Equal(0, page.TurnFrom(2, -1));
        // Nothing before the first stretch: the step stays.
        Assert.Equal(1, page.TurnFrom(1, -1));
    }

    [Fact]
    public void Battlegrounds_turns_skip_combats_and_events_skip_deaths()
    {
        // As the Battlegrounds steps lay out: the hero choice, then per turn the tavern and the combat.
        MatchTimelineSpan Span(int first, int last, int turn, MatchPhase phase) => new(first, last, turn, phase, true, string.Empty, string.Empty);
        MatchStepMark Mark(int frame, MatchStepKind kind, bool isEvent) => new(frame, 1, MatchPhase.Combat, kind, true, isEvent, kind.ToString());
        IReadOnlyList<MatchTimelineSpan> spans =
        [
            Span(0, 1, 0, MatchPhase.Start), Span(2, 4, 1, MatchPhase.Shop), Span(5, 9, 1, MatchPhase.Combat), Span(10, 12, 2, MatchPhase.Shop),
            Span(13, 13, 2, MatchPhase.Shop), Span(14, 16, 2, MatchPhase.Combat), Span(17, 17, 2, MatchPhase.End),
        ];
        IReadOnlyList<MatchStepMark> marks =
        [
            Mark(5, MatchStepKind.CombatStart, true), Mark(6, MatchStepKind.Attack, true), Mark(7, MatchStepKind.Deaths, false),
            Mark(8, MatchStepKind.Trigger, false), Mark(9, MatchStepKind.CombatEnd, true),
        ];

        // Ctrl+arrows: from inside a combat back to its tavern, on to the next tavern; never onto a combat.
        Assert.Equal(10, MatchPage.TurnFrom(spans, 7, +1));
        Assert.Equal(2, MatchPage.TurnFrom(spans, 7, -1));
        Assert.Equal(13, MatchPage.TurnFrom(spans, 10, +1));
        Assert.Equal(17, MatchPage.TurnFrom(spans, 14, +1));
        Assert.Equal(13, MatchPage.TurnFrom(spans, 15, -1));
        Assert.Equal(0, MatchPage.TurnFrom(spans, 3, -1));
        Assert.Equal(17, MatchPage.TurnFrom(spans, 17, +1));

        // Arrows: deaths and triggers are steps, not events.
        Assert.Equal(9, MatchPage.EventFrom(marks, 6, +1));
        Assert.Equal(6, MatchPage.EventFrom(marks, 9, -1));
        Assert.Equal(5, MatchPage.EventFrom(marks, 0, +1));
    }
}
