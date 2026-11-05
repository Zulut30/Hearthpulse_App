using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Tracking;
using Hearthpulse.Core.Tracking.Counters;

namespace Hearthpulse.Core.Hosting;

/// <summary>
/// The frames both renderers draw for the visual baselines (render group <c>baseline</c>, compared by
/// <c>tools/visual/visual_check.py</c>): main-window pages with a few made-up games, and the overlay over two made-up
/// game moments on game screens of three shapes. Rendered isolated on the fixture data folder (<c>tests/visual/data</c>),
/// so they show the same on any PC and day.
/// </summary>
public static class VisualBaseline
{
    /// <summary>Main-window pages, each saved as <c>main-&lt;page&gt;.png</c>.</summary>
    public static IReadOnlyList<string> Pages { get; } = ["Overview", "Battlegrounds", "Standard", "History", "Settings"];

    /// <summary>
    /// Game screens the overlay is drawn on: 16:9, 16:10 and 4:3 at 1200 lines or 1080 (the overlay scales with the
    /// screen's height). Each moment is saved as <c>overlay-&lt;moment&gt;-&lt;name&gt;.png</c>.
    /// </summary>
    public static IReadOnlyList<(string Name, int Width, int Height)> Screens { get; } =
        [("16x9", 1920, 1080), ("16x10", 1920, 1200), ("4x3", 1600, 1200)];

    /// <summary>
    /// The overlay's moments: <c>counters</c>, a Constructed game with the counters panel (<see cref="CounterScene.Synthetic"/>),
    /// and <c>odds</c>, a Battlegrounds fight with its odds (<see cref="CombatOddsScene.Synthetic"/>).
    /// </summary>
    public static IReadOnlyList<(string Name, Func<CardDb, LiveSnapshot> Snapshot)> Moments { get; } =
    [
        ("counters", cards => InGame("GT_RANKED") with { Constructed = CounterScene.Synthetic(cards) }),
        ("odds", _ => InGame("GT_BATTLEGROUNDS") with { Battlegrounds = CombatOddsScene.Synthetic() }),
    ];

    /// <summary>
    /// The games the pages show, added to the empty copy of the fixture before they open: a Battlegrounds game and two
    /// ranked ones, Standard and Wild. Their times have no zone, so no PC shows them at another hour; they lie far in the
    /// past, so "today" and "yesterday" never apply.
    /// </summary>
    public static IReadOnlyList<GameRecord> Games { get; } =
    [
        new()
        {
            Key = "baseline-bg", StartedAt = new DateTime(2026, 1, 15, 19, 30, 0), EndedAt = new DateTime(2026, 1, 15, 19, 52, 0),
            Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", PlayerName = "Tester", HeroCardId = "TB_BaconShop_HERO_41",
            Result = "Win", Placement = 2, RatingBefore = 6500, RatingAfter = 6560, Turns = 12, TavernTier = 5, Source = "baseline",
        },
        new()
        {
            Key = "baseline-standard", StartedAt = new DateTime(2026, 1, 14, 21, 5, 0), EndedAt = new DateTime(2026, 1, 14, 21, 17, 0),
            Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", PlayerName = "Tester", OpponentName = "Rival",
            HeroCardId = "HERO_08", OpponentHeroCardId = "HERO_09", PlayerClass = "MAGE", OpponentClass = "PRIEST", Result = "Win",
            Turns = 9, DeckName = "Mage", Rank = "Diamond 5", RankStarLevel = 46, WentFirst = true, Source = "baseline",
        },
        new()
        {
            Key = "baseline-wild", StartedAt = new DateTime(2026, 1, 13, 18, 40, 0), EndedAt = new DateTime(2026, 1, 13, 18, 58, 0),
            Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_WILD", PlayerName = "Tester", OpponentName = "Rival",
            HeroCardId = "HERO_02", OpponentHeroCardId = "HERO_06", PlayerClass = "SHAMAN", OpponentClass = "DRUID", Result = "Loss",
            Turns = 11, DeckName = "Shaman", Rank = "Gold 3", RankStarLevel = 28, WentFirst = false, Source = "baseline",
        },
    ];

    private static LiveSnapshot InGame(string gameType) =>
        LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = gameType };
}
