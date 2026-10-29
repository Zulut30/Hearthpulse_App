#if HEARTHPULSE_WPF
using Hearthpulse.App.Infrastructure;
#else
using Hearthpulse.UI.Infrastructure;
#endif
using System.Globalization;
using Hearthpulse.Core;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;

#if HEARTHPULSE_WPF
namespace Hearthpulse.App.Main.Match;
#else
namespace Hearthpulse.UI.Main.Match;
#endif

/// <summary>How a timeline stretch or tick is drawn.</summary>
public enum MatchTimelineLook
{
    /// <summary>Our turn, or a tick of our events only.</summary>
    Mine,

    /// <summary>The opponent's turn, or a tick of its events only.</summary>
    Theirs,

    /// <summary>The mulligan, the hero choice, the end; a tick of both players' events.</summary>
    Neutral,

    /// <summary>A Battlegrounds tavern.</summary>
    Shop,

    /// <summary>A Battlegrounds combat without a known result, or a tie.</summary>
    Combat,

    Won,

    Lost,
}

/// <summary>
/// A timeline stretch as drawn: where it sits as fractions of the game (by step, not by time), the steps it holds, its
/// look and texts.
/// </summary>
public sealed record MatchTimelineSegment(double Start, double End, int FirstFrame, int LastFrame, MatchTimelineLook Look, string Label, string Tooltip)
{
    public bool IsCombat => Look is MatchTimelineLook.Combat or MatchTimelineLook.Won or MatchTimelineLook.Lost;
}

/// <summary>An event tick (close events merged into one): where it sits, the step it goes to, whose it is, what happened.</summary>
public sealed record MatchTimelineTick(double At, int Frame, MatchTimelineLook Look, string Tooltip);

/// <summary>A leaderboard row of the Battlegrounds table: hero names only, never the players'.</summary>
public sealed record MatchLeaderLine(string HeroCardId, string Name, string Place, string Health, string Tier, bool Local, bool Fighting, bool Dead, string Tooltip);

/// <summary>
/// The player bar of the match page: playback (a step a second, x2, x4), event and turn jumps, the whole-game timeline,
/// and the Battlegrounds table of a format 4 file. Shared by both apps; the views only map keys and the mouse to it.
/// </summary>
public sealed partial class MatchViewModel
{
    // MDI glyphs (Material Design Icons code points, as in the rest of the app).
    private const string IconPlay = "\U000F040A";
    private const string IconPause = "\U000F03E4";
    private const string IconTurn = "\U000F0467";
    private const string IconCombat = "\U000F0787";

    /// <summary>Ticks merged so that a game of any length draws at most this many.</summary>
    private const int MaxTicks = 300;

    private static readonly int[] Speeds = [1, 2, 4];

    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource? _playing;
    private int _speed = 1;
    private IReadOnlyList<MatchTimelineSegment> _segments = [];
    private IReadOnlyList<MatchTimelineTick> _ticks = [];
    private int _timelineCurrent = -1;
    private double _timelinePosition;
    private MatchBgStepView? _battlegroundsStep;
    private IReadOnlyList<MatchLeaderLine> _leaders = [];

    public RelayCommand PreviousEvent { get; }

    public RelayCommand NextEvent { get; }

    /// <summary>Space and the middle button: play from here (from the first step at the end), or pause.</summary>
    public RelayCommand TogglePlay { get; }

    /// <summary>The + key: x1 → x2 → x4.</summary>
    public RelayCommand Faster { get; }

    /// <summary>The − key: x4 → x2 → x1.</summary>
    public RelayCommand Slower { get; }

    /// <summary>The speed button: x1 → x2 → x4 → x1.</summary>
    public RelayCommand CycleSpeed { get; }

    public bool Playing => _playing is not null;

    public string PlayGlyph => Playing ? IconPause : IconPlay;

    public string PlayTip => Loc.T(Playing ? "Match.Player.Pause" : "Match.Player.Play");

    /// <summary>Steps a second while playing: 1, 2 or 4.</summary>
    public int Speed => _speed;

    public string SpeedText => Loc.F("Match.Player.Speed", _speed);

    /// <summary>"Which side at the bottom" is a Constructed choice; Battlegrounds always has ours at the bottom.</summary>
    public bool ShowSide => IsConstructed;

    /// <summary>A Battlegrounds file of format 3: rounds and lobby only, the note says why.</summary>
    public bool ShowBattlegroundsOldNote => IsBattlegrounds && !HasBattlegroundsSteps;

    /// <summary>The game went over the recording limit: the replay stops early.</summary>
    public bool Truncated => _page?.Truncated == true;

    /// <summary>The "?" tooltip: the timeline legend of the mode, then the keys.</summary>
    public string HelpText => $"{Loc.T(IsConstructed ? "Match.Timeline.LegendConstructed" : "Match.Timeline.LegendBattlegrounds")}\n\n{Loc.T("Match.Player.Help")}";

    /// <summary>The timeline's stretches, left to right.</summary>
    public IReadOnlyList<MatchTimelineSegment> TimelineSegments { get => _segments; private set => Set(ref _segments, value); }

    /// <summary>The timeline's event ticks, close ones merged (at most <see cref="MaxTicks"/>).</summary>
    public IReadOnlyList<MatchTimelineTick> TimelineTicks { get => _ticks; private set => Set(ref _ticks, value); }

    /// <summary>The stretch holding the step on screen, as an index of <see cref="TimelineSegments"/>, or -1.</summary>
    public int TimelineCurrent { get => _timelineCurrent; private set => Set(ref _timelineCurrent, value); }

    /// <summary>The step on screen as a fraction of the game: the middle of its slot.</summary>
    public double TimelinePosition { get => _timelinePosition; private set => Set(ref _timelinePosition, value); }

    /// <summary>The Battlegrounds step on screen (format 4).</summary>
    public MatchBgStepView? BattlegroundsStep
    {
        get => _battlegroundsStep;
        private set
        {
            if (Set(ref _battlegroundsStep, value))
            {
                Raise(nameof(BattlegroundsWon));
                Raise(nameof(BattlegroundsLost));
                Raise(nameof(BattlegroundsCombatOver));
                Raise(nameof(ActionsTitle));
                Raise(nameof(ActionsIcon));
            }
        }
    }

    /// <summary>The step ends a combat the player won.</summary>
    public bool BattlegroundsWon => _battlegroundsStep?.ResultKey == "Win";

    public bool BattlegroundsLost => _battlegroundsStep?.ResultKey == "Loss";

    /// <summary>The step ends a combat (won, lost or tied): the step bar carries the result.</summary>
    public bool BattlegroundsCombatOver => _battlegroundsStep?.ResultKey is not null;

    public IReadOnlyList<MatchLeaderLine> Leaders { get => _leaders; private set => Set(ref _leaders, value); }

    /// <summary>"This turn", or "This combat" during a Battlegrounds combat.</summary>
    public string ActionsTitle => Loc.T(_battlegroundsStep?.InCombat == true ? "Match.Player.ThisCombat" : "Match.View.ThisTurn");

    public string ActionsIcon => _battlegroundsStep?.InCombat == true ? IconCombat : IconTurn;

    /// <summary>The frame count the timeline maps the pointer to: a step for each of the game's slots.</summary>
    public static int FrameAt(double fraction, int frameCount) =>
        frameCount <= 0 ? 0 : Math.Clamp((int)Math.Floor(fraction * frameCount), 0, frameCount - 1);

    /// <summary>The player turn (Constructed) or the Battlegrounds turn of a step; 0 before the first turn.</summary>
    private int TurnAt(int frame) =>
        _page?.Replay is { Frames.Count: > 0 } replay ? replay.Frames[Math.Clamp(frame, 0, replay.Frames.Count - 1)].PlayerTurn
            : _battlegroundsStep?.Turn ?? 0;

    private void PlayOrPause()
    {
        if (Playing)
        {
            Pause();
            return;
        }

        if (_page is null || FrameMax == 0)
        {
            return;
        }

        if (Frame >= FrameMax)
        {
            Frame = 0;
        }

        var playing = new CancellationTokenSource();
        _playing = playing;
        RaisePlayback();
        PlayAsync(playing).Forget("Match replay playback");
    }

    /// <summary>
    /// A step every second / speed on the UI context; a step chosen by hand meanwhile does not stop it. Ends at the last
    /// step, or when paused (the button, another tab, another game, the page closed).
    /// </summary>
    private async Task PlayAsync(CancellationTokenSource playing)
    {
        try
        {
            while (true)
            {
                await _delay(TimeSpan.FromSeconds(1.0 / _speed), playing.Token).ConfigureAwait(true);
                if (_playing != playing)
                {
                    return;
                }

                Frame++;
                if (Frame >= FrameMax)
                {
                    Pause();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Paused while waiting.
        }
    }

    /// <summary>The page went out of sight (the window hidden to the tray, another page): no steps drawn while nobody sees them.</summary>
    public void StopPlayback() => Pause();

    private void Pause()
    {
        if (_playing is not { } playing)
        {
            return;
        }

        _playing = null;
        playing.Cancel();
        playing.Dispose();
        RaisePlayback();
    }

    private void ChangeSpeed(int step, bool wrap)
    {
        var index = Array.IndexOf(Speeds, _speed) + step;
        _speed = Speeds[wrap ? (index + Speeds.Length) % Speeds.Length : Math.Clamp(index, 0, Speeds.Length - 1)];
        Raise(nameof(Speed));
        Raise(nameof(SpeedText));
    }

    private void RaisePlayback()
    {
        Raise(nameof(Playing));
        Raise(nameof(PlayGlyph));
        Raise(nameof(PlayTip));
    }

    /// <summary>The timeline of a newly shown page; the step itself is drawn by <c>Refresh</c>.</summary>
    private void ShowPlayer(MatchPage page)
    {
        var count = page.FrameCount;
        TimelineSegments = count == 0 ? [] : Segments(page.Timeline, count, frame => page.BattlegroundsStep(frame)?.ResultKey);
        TimelineTicks = count == 0 ? [] : Ticks(page.Marks, count);
        if (!page.HasBattlegroundsSteps)
        {
            BattlegroundsStep = null;
            Leaders = [];
        }

        Raise(nameof(FrameMax));
        Raise(nameof(Truncated));
        Raise(nameof(HelpText));
        Raise(nameof(ActionsTitle));
    }

    private void ClearPlayer()
    {
        TimelineSegments = [];
        TimelineTicks = [];
        TimelineCurrent = -1;
        TimelinePosition = 0;
        BattlegroundsStep = null;
        Leaders = [];
        Raise(nameof(Truncated));
    }

    /// <summary>The current stretch and the position line follow the step on screen.</summary>
    private void MoveTimeline()
    {
        var count = _page?.FrameCount ?? 0;
        TimelinePosition = count == 0 ? 0 : (_frame + 0.5) / count;
        var current = -1;
        for (var i = 0; i < _segments.Count; i++)
        {
            if (_segments[i].FirstFrame <= _frame && (current < 0 || _segments[i].FirstFrame >= _segments[current].FirstFrame))
            {
                current = i;
            }
        }

        TimelineCurrent = current;
    }

    /// <summary>
    /// The timeline's stretches as fractions of a game of <paramref name="count"/> steps, each in its look. A combat takes
    /// the colour of the result the game showed at its end (<paramref name="resultAt"/> its last step).
    /// </summary>
    public static IReadOnlyList<MatchTimelineSegment> Segments(IReadOnlyList<MatchTimelineSpan> spans, int count, Func<int, string?> resultAt) =>
        spans.Select(span => new MatchTimelineSegment((double)span.FirstFrame / count, (double)(span.LastFrame + 1) / count, span.FirstFrame, span.LastFrame,
            span.Phase switch
            {
                MatchPhase.Turn => span.Local ? MatchTimelineLook.Mine : MatchTimelineLook.Theirs,
                MatchPhase.Shop => MatchTimelineLook.Shop,
                MatchPhase.Combat => resultAt(span.LastFrame) switch
                {
                    "Win" => MatchTimelineLook.Won,
                    "Loss" => MatchTimelineLook.Lost,
                    _ => MatchTimelineLook.Combat,
                },
                _ => MatchTimelineLook.Neutral,
            },
            span.Label, span.Tooltip)).ToList();

    /// <summary>
    /// The event ticks: one per event, merged into buckets of ceil(steps / 300) steps on long games. A merged tick goes
    /// to its first event, is ours or theirs only when all its events are, and lists up to three captions.
    /// </summary>
    public static IReadOnlyList<MatchTimelineTick> Ticks(IReadOnlyList<MatchStepMark> marks, int count)
    {
        var bucket = Math.Max(1, (int)Math.Ceiling((double)count / MaxTicks));
        return marks
            .Where(m => m.IsEvent && m.Frame >= 0 && m.Frame < count)
            .GroupBy(m => m.Frame / bucket)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var events = g.OrderBy(m => m.Frame).ToList();
                var look = events.TrueForAll(m => m.Local) ? MatchTimelineLook.Mine
                    : events.TrueForAll(m => !m.Local) ? MatchTimelineLook.Theirs : MatchTimelineLook.Neutral;
                var lines = events.Take(3).Select(m => m.Caption);
                var tooltip = string.Join("\n", events.Count > 3 ? lines.Append(Loc.F("Match.Timeline.More", events.Count - 3)) : lines);
                return new MatchTimelineTick((events[0].Frame + 0.5) / count, events[0].Frame, look, tooltip);
            })
            .ToList();
    }

    /// <summary>A Battlegrounds step on the table, its leaderboard and the events of its tavern or combat.</summary>
    private void ShowBattlegroundsStep(MatchBgStepView? step)
    {
        BattlegroundsStep = step;
        Step = null;
        Leaders = step?.Leaderboard.Select(Leader).ToList() ?? [];
        Actions = _page is not null && TimelineCurrent >= 0 && TimelineCurrent < _segments.Count
            ? StretchActions(_page.Marks, _segments[TimelineCurrent], _frame)
            : [];
    }

    /// <summary>A leaderboard row as shown: health with armor, the state in the tooltip.</summary>
    public static MatchLeaderLine Leader(MatchBgLeaderRow row)
    {
        var state = row.Dead ? Loc.T("Match.Player.LeaderDead") : row.Fighting ? Loc.T("Match.Player.LeaderFighting") : null;
        return new MatchLeaderLine(row.HeroCardId, row.Name, row.Place.ToString(CultureInfo.CurrentCulture),
            row.Armor > 0 ? $"{row.Health} + {row.Armor}" : row.Health.ToString(CultureInfo.CurrentCulture),
            row.Tier.ToString(CultureInfo.CurrentCulture), row.Local, row.Fighting, row.Dead,
            state is null ? row.Name : $"{row.Name} · {state}");
    }

    /// <summary>"This turn" / "This combat": the events of a stretch, the latest one up to <paramref name="frame"/> marked.</summary>
    public static IReadOnlyList<MatchActionRow> StretchActions(IReadOnlyList<MatchStepMark> marks, MatchTimelineSegment span, int frame)
    {
        var events = marks.Where(m => m.IsEvent && m.Frame >= span.FirstFrame && m.Frame <= span.LastFrame).OrderBy(m => m.Frame).ToList();
        var current = events.LastOrDefault(m => m.Frame <= frame)?.Frame;
        return events.Select(m => new MatchActionRow(m.Frame, IconOf(m.Kind), m.Caption, m.Local, null, m.Frame == current)).ToList();
    }

    private static string IconOf(MatchStepKind kind) => kind switch
    {
        MatchStepKind.Buy => "\U000F0112",
        MatchStepKind.Sell => "\U000F1261",
        MatchStepKind.Play => "\U000F063A",
        MatchStepKind.HeroPower or MatchStepKind.Use => "\U000F140B",
        MatchStepKind.Refresh => "\U000F0450",
        MatchStepKind.Upgrade => "\U000F0060",
        MatchStepKind.Freeze => "\U000F0717",
        MatchStepKind.Move => "\U000F04E1",
        MatchStepKind.Choice or MatchStepKind.Mulligan => "\U000F0AE2",
        MatchStepKind.CombatStart => IconCombat,
        MatchStepKind.Attack => "\U000F04E5",
        MatchStepKind.Deaths => "\U000F0BC8",
        MatchStepKind.Trigger => "\U000F0D72",
        MatchStepKind.CombatEnd or MatchStepKind.End => "\U000F023B",
        _ => IconTurn,
    };
}
