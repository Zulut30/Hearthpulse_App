using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Replay;
using Hearthpulse.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthpulse.Tests;

/// <summary>
/// Turn timing from Power.log, in the shape of the October 2026 logs: TURN is already 1 during the mulligan, a turn's
/// main phase starts at STEP=MAIN_READY and ends at STEP=MAIN_END, and the players' TIMEOUT tag (45, 55, then 75
/// seconds) is set for a player's next turn when its turn ends.
/// </summary>
public sealed class TurnClockTests(ITestOutputHelper output)
{
    private static readonly DateTime Day = new(2026, 10, 23);

    private static readonly string[] Start =
    [
        "21:38:40.0 DebugPrintPower() - CREATE_GAME",
        "21:38:40.0 DebugPrintPower() -     GameEntity EntityID=1",
        "21:38:40.0 DebugPrintPower() -         tag=CARDTYPE value=GAME",
        "21:38:40.0 DebugPrintPower() -     Player EntityID=2 PlayerID=1 GameAccountId=[hi=144115198130930503 lo=11]",
        "21:38:40.0 DebugPrintPower() -         tag=CONTROLLER value=1",
        "21:38:40.0 DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "21:38:40.0 DebugPrintPower() -     Player EntityID=3 PlayerID=2 GameAccountId=[hi=144115198130930503 lo=22]",
        "21:38:40.0 DebugPrintPower() -         tag=CONTROLLER value=2",
        "21:38:40.0 DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "21:38:40.0 DebugPrintGame() - GameType=GT_RANKED",
        "21:38:40.0 DebugPrintGame() - FormatType=FT_STANDARD",
        "21:38:40.0 DebugPrintGame() - PlayerID=1, PlayerName=Me#1",
        "21:38:40.0 DebugPrintGame() - PlayerID=2, PlayerName=You#2",
        "21:38:40.0 DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=CURRENT_PLAYER value=1 ",
        "21:38:40.0 DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=TIMEOUT value=45 ",
        "21:38:40.0 DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=TIMEOUT value=45 ",
        "21:38:40.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=1 ",
        "21:38:40.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=BEGIN_MULLIGAN ",
        // The mulligan takes 37 seconds; the first turn's clock starts after it.
        "21:39:17.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_READY ",
        "21:39:17.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_ACTION ",
        "21:39:31.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END ",
        "21:39:31.0 DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=TIMEOUT value=55 ",
        "21:39:31.0 DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=CURRENT_PLAYER value=0 ",
        "21:39:31.0 DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=CURRENT_PLAYER value=1 ",
        "21:39:31.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=2 ",
        "21:39:31.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_READY ",
        "21:40:01.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END ",
        "21:40:01.0 DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=TIMEOUT value=55 ",
        "21:40:01.0 DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=CURRENT_PLAYER value=0 ",
        "21:40:01.0 DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=CURRENT_PLAYER value=1 ",
        "21:40:01.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=3 ",
        "21:40:01.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_READY ",
        "21:40:11.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_END ",
        "21:40:11.0 DebugPrintPower() - TAG_CHANGE Entity=Me#1 tag=CURRENT_PLAYER value=0 ",
        "21:40:11.0 DebugPrintPower() - TAG_CHANGE Entity=You#2 tag=CURRENT_PLAYER value=1 ",
        "21:40:11.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=TURN value=4 ",
        "21:40:11.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_READY ",
        "21:40:20.0 DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STEP value=MAIN_ACTION ",
    ];

    private static (TurnClock Clock, GameState Game) Parse(IEnumerable<string> lines)
    {
        var parser = new PowerParser(DateOnly.FromDateTime(Day));
        var clock = new TurnClock(() => parser.LastTimestamp);
        parser.AddListener(clock);
        foreach (var line in lines)
        {
            var space = line.IndexOf(' ');
            parser.Feed($"D {line[..space]}000000 GameState.{line[(space + 1)..]}");
        }

        var game = parser.Game!;
        game.LocalPlayerId = 1;
        return (clock, game);
    }

    [Fact]
    public void Turns_run_from_main_ready_to_main_end_with_the_current_players_timeout_as_limit()
    {
        var (clock, game) = Parse(Start);

        Assert.Equal(
            [
                new TurnTiming(1, 1, Day.Add(new TimeSpan(21, 39, 17)), 45, 14),
                new TurnTiming(2, 2, Day.Add(new TimeSpan(21, 39, 31)), 45, 30),
                new TurnTiming(3, 1, Day.Add(new TimeSpan(21, 40, 1)), 55, 10),
                new TurnTiming(4, 2, Day.Add(new TimeSpan(21, 40, 11)), 55, null),
            ],
            clock.Turns);

        var view = clock.View(game)!;
        Assert.False(view.LocalActive);
        Assert.True(view.Running);
        Assert.Equal(2, view.Turn);
        Assert.Equal(55, view.LimitSeconds);
        Assert.Equal(TimeSpan.FromSeconds(12), view.LocalAverage);
        Assert.Equal(TimeSpan.FromSeconds(30), view.OpponentAverage);
        Assert.Equal(TimeSpan.FromSeconds(10), view.LocalLast);
    }

    [Fact]
    public void The_rope_burns_in_the_last_fifteen_seconds_and_an_old_turn_is_stale()
    {
        var started = Day.AddHours(21);
        var view = new TurnClockView(3, true, started, 75, null, null, null, null);

        Assert.False(view.Rope(started.AddSeconds(59)));
        Assert.True(view.Rope(started.AddSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(15), view.Remaining(started.AddSeconds(60)));
        Assert.Equal(TimeSpan.Zero, view.Remaining(started.AddSeconds(90)));
        Assert.Equal("1:02", TurnClockView.Format(view.Elapsed(started.AddSeconds(62))));
        Assert.False(view.IsStale(started.AddSeconds(130)));
        Assert.True(view.IsStale(started.AddMinutes(5)));
    }

    [Fact]
    public void A_new_game_clears_the_turns_and_battlegrounds_has_no_clock()
    {
        var (clock, game) = Parse(Start);
        clock.OnGameStarted(game);
        Assert.Empty(clock.Turns);
        Assert.Null(clock.View(game));
    }

    [Fact]
    public void The_match_analysis_averages_turns_from_the_replay_without_the_first_one()
    {
        ReplayTurn[] turns =
        [
            new(0, null, 0, 1, 0) { Seconds = 37 },
            new(1, ReplayPlayer.Local, 2, 3, 0) { Seconds = 51 },
            new(2, ReplayPlayer.Opponent, 4, 5, 51) { Seconds = 30 },
            new(3, ReplayPlayer.Local, 6, 7, 81) { Seconds = 10 },
            new(4, ReplayPlayer.Opponent, 8, 9, 91) { Seconds = 50 },
            new(5, ReplayPlayer.Local, 10, 11, 141) { Seconds = 70 },
        ];

        var tile = MatchAnalysis.TurnTimeTile(turns)!;

        Assert.Equal("0:40 / 0:40", tile.Value);
        Assert.Contains("1:10", tile.Detail);
        Assert.Null(MatchAnalysis.TurnTimeTile([turns[0], turns[1]]));
    }

    [RealLogsFact]
    public void Kept_constructed_logs_give_turns_within_their_limits()
    {
        // Machine-dependent smoke run: every turn of every kept Constructed game starts with a known player and limit,
        // and none outlasts its limit by more than the animations the game waits for.
        var turns = 0;
        foreach (var dir in Directory.EnumerateDirectories(RealLogTests.LogRoot, "Hearthstone_*"))
        {
            var path = Path.Combine(dir, "Power.log");
            if (!File.Exists(path) || HearthstoneInstall.SessionStart(dir) is not { } start)
            {
                continue;
            }

            var parser = new PowerParser(DateOnly.FromDateTime(start));
            var clock = new TurnClock(() => parser.LastTimestamp);
            var checker = new OnEnd(_ =>
            {
                foreach (var t in clock.Turns.Where(t => t.Seconds is not null))
                {
                    turns++;
                    Assert.NotEqual(0, t.PlayerId);
                    Assert.Contains(t.LimitSeconds, new[] { 45, 55, 75, 90 });
                    Assert.InRange(t.Seconds!.Value, 0, t.LimitSeconds + 45);
                }

                output.WriteLine($"{Path.GetFileName(dir)}: {string.Join(" ", clock.Turns.Select(t => $"{t.Turn}:{t.Seconds:0}/{t.LimitSeconds}"))}");
            });
            parser.AddListener(clock);
            parser.AddListener(checker);
            foreach (var line in RealLogTests.SharedLines(path))
            {
                parser.Feed(line);
            }

            parser.Flush();
        }

        output.WriteLine($"{turns} turns");
    }

    private sealed class OnEnd(Action<GameState> ended) : IPowerListener
    {
        public void OnGameEnded(GameState game)
        {
            if (!game.IsBattlegrounds)
            {
                ended(game);
            }
        }
    }
}
