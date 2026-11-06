using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;

namespace Hearthpulse.Tests;

/// <summary>The two Arenas (owner, 2026-11-07): told apart by the logged game type, never mixed in one run.</summary>
public sealed class ArenaKindTests
{
    private static readonly DateTime Start = new(2026, 11, 7, 18, 0, 0);

    private static int _key;

    private static GameRecord Game(int minutes, string result, string gameType = "GT_ARENA", string playerClass = "MAGE") => new()
    {
        Key = "a" + Interlocked.Increment(ref _key),
        StartedAt = Start.AddMinutes(minutes),
        EndedAt = Start.AddMinutes(minutes + 8),
        Mode = "Arena",
        GameType = gameType,
        Result = result,
        PlayerClass = playerClass,
    };

    [Theory]
    [InlineData("GT_UNDERGROUND_ARENA", ArenaKind.Underground)]
    [InlineData("GT_ARENA", ArenaKind.Arena)]
    [InlineData("", ArenaKind.Arena)]
    public void The_game_type_tells_the_arena(string gameType, ArenaKind kind)
    {
        Assert.Equal(kind, ArenaStats.KindOf(Game(0, "Win", gameType)));
        Assert.True(ArenaStats.Is(Game(0, "Win", gameType), kind));
        Assert.False(ArenaStats.Is(Game(0, "Win", gameType) with { Mode = "Ranked" }, kind));
    }

    [Fact]
    public void Runs_of_the_two_arenas_never_mix_even_without_deck_ids()
    {
        GameRecord[] games =
        [
            Game(0, "Win"),
            Game(10, "Win", "GT_UNDERGROUND_ARENA"),
            Game(20, "Loss"),
            Game(30, "Loss", "GT_UNDERGROUND_ARENA"),
        ];

        var runs = ArenaStats.Runs(games);

        Assert.Equal(4, runs.Count);
        Assert.Equal([ArenaKind.Arena, ArenaKind.Underground, ArenaKind.Arena, ArenaKind.Underground], runs.Select(r => r.Kind));
    }

    [Fact]
    public void An_underground_run_ends_at_five_losses_a_regular_one_at_three()
    {
        var regular = ArenaStats.Runs(Enumerable.Range(0, 3).Select(i => Game(i * 10, "Loss")).ToList());
        var underground = ArenaStats.Runs(Enumerable.Range(0, 5).Select(i => Game(i * 10, "Loss", "GT_UNDERGROUND_ARENA")).ToList());

        Assert.True(Assert.Single(regular).Finished);
        var run = Assert.Single(underground);
        Assert.Equal(5, run.Losses);
        Assert.True(run.Finished);
        Assert.False(ArenaStats.Runs(Enumerable.Range(0, 4).Select(i => Game(i * 10, "Loss", "GT_UNDERGROUND_ARENA")).ToList()).Single().Finished);
    }
}
