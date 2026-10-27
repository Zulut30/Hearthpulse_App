using Hearthpulse.Core.Power;

namespace Hearthpulse.Tests;

/// <summary>The date a game gets when its log lines cross midnight.</summary>
public sealed class ParserClockTests
{
    private static void Feed(PowerParser parser, string time, string body) =>
        parser.Feed($"D {time}.0000000 GameState.DebugPrintPower() - {body}");

    [Fact]
    public void Joining_a_session_after_midnight_dates_its_game_the_next_day()
    {
        // The client started at 23:30; the app joins its last game, which began at 00:10 the next day.
        var parser = new PowerParser(new DateTime(2026, 10, 20, 23, 30, 0));
        Feed(parser, "00:10:00", "CREATE_GAME");

        Assert.Equal(new DateTime(2026, 10, 21, 0, 10, 0), parser.Game!.StartedAt);
    }

    [Fact]
    public void A_session_started_the_same_evening_keeps_its_date()
    {
        var parser = new PowerParser(new DateTime(2026, 10, 20, 23, 30, 0));
        Feed(parser, "23:40:00", "CREATE_GAME");

        Assert.Equal(new DateTime(2026, 10, 20, 23, 40, 0), parser.Game!.StartedAt);
    }

    [Fact]
    public void Replaying_a_game_that_crossed_midnight_keeps_its_dates()
    {
        var parser = new PowerParser(new DateTime(2026, 10, 20, 23, 0, 0));
        Feed(parser, "23:50:00", "CREATE_GAME");
        Feed(parser, "00:10:00", "TAG_CHANGE Entity=GameEntity tag=TURN value=5");

        // A resync drops the game and replays it from its CREATE_GAME line.
        parser.ResetGame();
        Feed(parser, "23:50:00", "CREATE_GAME");
        Assert.Equal(new DateTime(2026, 10, 20, 23, 50, 0), parser.Game!.StartedAt);

        Feed(parser, "00:11:00", "TAG_CHANGE Entity=GameEntity tag=TURN value=6");
        Assert.Equal(new DateTime(2026, 10, 21, 0, 11, 0), parser.LastTimestamp);
    }
}
