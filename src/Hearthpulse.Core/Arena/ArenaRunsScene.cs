using Hearthpulse.Core.Data;

namespace Hearthpulse.Core.Arena;

/// <summary>
/// Made-up Arena games for the <c>--render-ui</c> scenes of My Stats → Arena (the rewards chip and editor,
/// <c>docs/arena-rewards.md</c>): a finished 7–3 Hunter run with rewards and a Mage run still going. Dated before any
/// real game of a data copy (October 1, 2026), so lists of recent games keep the real ones on top.
/// </summary>
public static class ArenaRunsScene
{
    public const long FinishedDeckId = 990_001;

    public const long OngoingDeckId = 990_002;

    /// <summary>The finished run's rewards: 150 gold, 40 dust, a pack.</summary>
    public static ArenaRewards FinishedRewards { get; } = new(150, 40, 1, 0, 0);

    /// <summary>The finished run's key (<see cref="Stats.ArenaRun.Key"/>).</summary>
    public static string FinishedKey => "deck:" + FinishedDeckId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The games, oldest first.</summary>
    public static IReadOnlyList<GameRecord> Games()
    {
        var start = new DateTime(2026, 10, 1, 18, 0, 0, DateTimeKind.Local);
        string[] hunter = ["Win", "Win", "Loss", "Win", "Win", "Win", "Loss", "Win", "Win", "Loss"];
        string[] mage = ["Win", "Loss", "Win"];
        return hunter.Select((r, i) => Game(r, "HUNTER", FinishedDeckId, start.AddMinutes(12 * i)))
            .Concat(mage.Select((r, i) => Game(r, "MAGE", OngoingDeckId, start.AddDays(1).AddMinutes(12 * i))))
            .ToList();
    }

    /// <summary>An Underground Arena run in progress (Warrior, 3–2): the Arena page's second tab.</summary>
    public static IReadOnlyList<GameRecord> UndergroundGames()
    {
        var start = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Local);
        string[] warrior = ["Win", "Loss", "Win", "Win", "Loss"];
        return warrior.Select((r, i) => Game(r, "WARRIOR", UndergroundDeckId, start.AddMinutes(12 * i)) with { GameType = "GT_UNDERGROUND_ARENA", OpponentClass = "PRIEST" }).ToList();
    }

    private const long UndergroundDeckId = 3_300_003;

    private static GameRecord Game(string result, string playerClass, long deckId, DateTime started) => new()
    {
        Key = $"render-arena-{deckId}-{started.Ticks}",
        StartedAt = started,
        EndedAt = started.AddMinutes(10),
        Mode = "Arena",
        GameType = "GT_ARENA",
        Format = "FT_WILD",
        Result = result,
        Turns = 9,
        PlayerClass = playerClass,
        OpponentClass = "WARRIOR",
        DeckId = deckId,
    };
}
