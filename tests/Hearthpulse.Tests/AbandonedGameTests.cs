using System.Globalization;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;
using Microsoft.Data.Sqlite;

namespace Hearthpulse.Tests;

/// <summary>
/// A game the client left mid-way (closed, crashed) used to stay "live" for as long as the app ran and never reached
/// History. It is now saved as abandoned once the client has been gone for <see cref="GameSession.AbandonAfter"/>, with
/// no result, so win rates and placements stay as they were. The game is the real Battlegrounds fixture, which ends
/// with the client closed in the third round.
/// </summary>
public sealed class AbandonedGameTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 23, 0, 10, 0, DateTimeKind.Utc);

    private readonly string _db = Path.Combine(Path.GetTempPath(), $"hp-abandon-{Guid.NewGuid():N}.db");
    private readonly HearthstoneMemory _memory = new();
    private readonly HistoryStore _history;
    private readonly GameSession _session;
    private DateTime _now = T0;
    private bool _running;

    public AbandonedGameTests()
    {
        _history = new HistoryStore(_db);
        _session = new GameSession(_history, _memory)
        {
            Clock = () => _now,
            IsHearthstoneRunning = () => _running,
            ReadsMemory = false,
        };
        _session.OnSessionStarted("Hearthstone_2026_10_23_02_01_43", new DateTime(2026, 10, 23, 2, 1, 43), attachedMidSession: false);
    }

    public void Dispose()
    {
        _memory.Dispose();
        SqliteConnection.ClearAllPools();
        File.Delete(_db);
    }

    private void Feed(IEnumerable<string> lines) => _session.OnLines("Power.log", lines.ToList(), catchingUp: false);

    private void TickAt(TimeSpan sinceT0)
    {
        _now = T0 + sinceT0;
        _session.OnTick();
    }

    [Fact]
    public void A_restarted_watcher_joining_the_same_session_does_not_end_the_live_game()
    {
        // The host restarts a stuck watcher; the new one joins the same session folder at its last game.
        _running = true;
        Feed(BattlegroundsFixtureTests.Lines());
        TickAt(TimeSpan.Zero);

        _session.OnSessionStarted("Hearthstone_2026_10_23_02_01_43", new DateTime(2026, 10, 23, 2, 1, 43), attachedMidSession: true);
        TickAt(TimeSpan.FromSeconds(1));

        Assert.Empty(_history.Recent());
    }

    [Fact]
    public void A_game_the_client_left_is_saved_as_abandoned_after_the_grace_time()
    {
        Feed(BattlegroundsFixtureTests.Lines());
        TickAt(TimeSpan.Zero);
        TickAt(GameSession.AbandonAfter - TimeSpan.FromSeconds(10));
        Assert.Empty(_history.Recent());

        TickAt(GameSession.AbandonAfter + TimeSpan.FromSeconds(10));
        var game = Assert.Single(_history.Recent());
        Assert.True(game.IsAbandoned);
        Assert.Equal(GameRecord.AbandonedResult, game.Result);
        Assert.Equal("Battlegrounds", game.Mode);
        Assert.Equal(0, game.Placement);
        Assert.Equal("BG22_HERO_305", game.HeroCardId);
        // The My Stats fields of the rounds played are kept.
        Assert.Contains("\"turn\":1", game.CombatResults);
        Assert.Contains("BG22_HERO_305", game.OfferedHeroes);
        Assert.Equal(new DateTime(2026, 10, 23, 2, 7, 12), game.EndedAt.AddTicks(-(game.EndedAt.Ticks % TimeSpan.TicksPerSecond)));

        // Saved once, and left out of every stat.
        TickAt(GameSession.AbandonAfter * 3);
        Assert.Single(_history.Recent());
        Assert.Empty(StatsFilter.Apply(_history.Recent(), StatsPeriod.All, T0));
        Assert.Empty(_history.HeroStats(id => id));
        Assert.Equal(0, _history.PlacementDistribution().Sum());
    }

    [Fact]
    public void A_running_client_keeps_the_game_live()
    {
        _running = true;
        Feed(BattlegroundsFixtureTests.Lines());
        TickAt(TimeSpan.Zero);
        TickAt(TimeSpan.FromHours(2));
        Assert.Empty(_history.Recent());
    }

    [Fact]
    public void New_log_lines_restart_the_grace_time()
    {
        var lines = BattlegroundsFixtureTests.Lines().ToList();
        Feed(lines.Take(3000));
        TickAt(TimeSpan.Zero);

        _now = T0 + GameSession.AbandonAfter - TimeSpan.FromMinutes(1);
        Feed(lines.Skip(3000));
        TickAt(GameSession.AbandonAfter + TimeSpan.FromMinutes(1));
        Assert.Empty(_history.Recent());

        TickAt(GameSession.AbandonAfter * 2);
        Assert.True(Assert.Single(_history.Recent()).IsAbandoned);
    }

    [Fact]
    public void A_new_client_session_saves_the_left_game_right_away()
    {
        Feed(BattlegroundsFixtureTests.Lines());
        _session.OnSessionStarted("Hearthstone_2026_10_23_02_20_00", new DateTime(2026, 10, 23, 2, 20, 0), attachedMidSession: false);
        Assert.True(Assert.Single(_history.Recent()).IsAbandoned);
    }

    /// <summary>The fixture game again from a new CREATE_GAME ten minutes later, as a reconnect replays it, played to its end.</summary>
    private static IEnumerable<string> Reconnected()
    {
        foreach (var line in BattlegroundsFixtureTests.Lines())
        {
            var time = TimeSpan.ParseExact(line.AsSpan(2, 16), @"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture);
            yield return string.Concat(line.AsSpan(0, 2), (time + TimeSpan.FromMinutes(10)).ToString(@"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture), line.AsSpan(18));
        }

        yield return "D 02:20:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=Player#1000 tag=PLAYSTATE value=LOST ";
        yield return "D 02:20:00.0000000 GameState.DebugPrintPower() - TAG_CHANGE Entity=GameEntity tag=STATE value=COMPLETE ";
    }

    [Fact]
    public void A_reconnect_from_a_restarted_client_leaves_one_row_with_the_result()
    {
        Feed(BattlegroundsFixtureTests.Lines());
        _session.OnSessionStarted("Hearthstone_2026_10_23_02_14_00", new DateTime(2026, 10, 23, 2, 14, 0), attachedMidSession: false);
        Assert.True(Assert.Single(_history.Recent()).IsAbandoned);

        Feed(Reconnected());
        var game = Assert.Single(_history.Recent());
        Assert.False(game.IsAbandoned);
        Assert.Equal("BG22_HERO_305", game.HeroCardId);
        Assert.InRange(game.Placement, 1, 8);
    }

    [Fact]
    public void A_reconnect_in_the_same_log_leaves_one_row_with_the_result()
    {
        // The client wrote a second CREATE_GAME for the same game into the same Power.log.
        Feed(BattlegroundsFixtureTests.Lines());
        Feed(Reconnected());
        Assert.False(Assert.Single(_history.Recent()).IsAbandoned);
    }

    [Fact]
    public void Only_the_same_game_is_folded_into_its_reconnect()
    {
        var at = new DateTime(2026, 10, 23, 20, 0, 0);
        GameRecord Game(string key, DateTime start, string result, string hero = "HERO_08", string opponent = "You#2") => new()
        {
            Key = key, StartedAt = start, EndedAt = start.AddMinutes(4), Mode = "Ranked", GameType = "GT_RANKED", PlayerName = "Me#1",
            OpponentName = opponent, HeroCardId = hero, OpponentHeroCardId = "HERO_02", Result = result,
        };

        _history.Add(Game("left", at, GameRecord.AbandonedResult));
        _history.Add(Game("other hero", at, GameRecord.AbandonedResult, hero: "HERO_01"));
        _history.Add(Game("other opponent", at, GameRecord.AbandonedResult, opponent: "Them#3"));
        _history.Add(Game("long ago", at.AddHours(-3), GameRecord.AbandonedResult));
        Assert.True(_history.Add(Game("reconnect", at.AddMinutes(9), "Win")));
        Assert.Equal(["long ago", "other hero", "other opponent", "reconnect"], _history.Recent().Select(g => g.Key).Order());

        // The abandoned part saved after its reconnect (a log read late) is not added either.
        Assert.False(_history.Add(Game("left again", at, GameRecord.AbandonedResult)));
    }

    [Fact]
    public void The_real_result_replaces_an_abandoned_row_but_never_the_other_way()
    {
        var at = new DateTime(2026, 10, 23, 2, 5, 15);
        var left = new GameRecord { Key = "k", StartedAt = at, EndedAt = at.AddMinutes(2), Mode = "Ranked", Result = GameRecord.AbandonedResult, Rank = "Gold 4" };
        Assert.True(_history.Add(left));

        Assert.True(_history.Add(left with { EndedAt = at.AddMinutes(9), Result = "Win", Turns = 8, Rank = null }));
        var saved = Assert.Single(_history.Recent());
        Assert.Equal("Win", saved.Result);
        Assert.Equal(8, saved.Turns);
        Assert.Equal("Gold 4", saved.Rank);

        Assert.False(_history.Add(left));
        Assert.False(_history.Add(saved with { Result = "Loss" }));
        Assert.Equal("Win", Assert.Single(_history.Recent()).Result);
    }

    [Fact]
    public void Abandoned_constructed_games_do_not_count_in_class_deck_or_matchup_stats()
    {
        var at = new DateTime(2026, 10, 23, 12, 0, 0);
        GameRecord Game(string key, string result) => new()
        {
            Key = key, StartedAt = at, EndedAt = at.AddMinutes(8), Mode = "Ranked", Result = result,
            PlayerClass = "MAGE", OpponentClass = "ROGUE", DeckName = "Mage", DeckCode = null,
        };
        _history.Add(Game("a", "Win"));
        _history.Add(Game("b", GameRecord.AbandonedResult));

        Assert.Equal(1, Assert.Single(_history.ClassStats()).Games);
        Assert.Equal(1, Assert.Single(_history.DeckStats()).Games);
        Assert.Equal(1, Assert.Single(_history.MatchupStats()).Games);
        Assert.Equal(1, DeckRecords.Find(_history.Recent(), "Mage", null, "MAGE")!.Games);
        Assert.Equal(1, WinLoss.Of(StatsFilter.Apply(_history.Recent(), StatsPeriod.All, at)).Games);
    }
}
