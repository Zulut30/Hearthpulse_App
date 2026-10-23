using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

public sealed class ChoiceTrackingTests
{
    private const int Me = 5;

    [Fact]
    public void A_discover_pick_and_a_play_are_recorded()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 23));
        var tracker = new BattlegroundsTracker { Cards = CardDb.Empty };
        parser.AddListener(tracker);
        Feed(parser,
        [
            "DebugPrintPower() - CREATE_GAME",
            "DebugPrintPower() -     GameEntity EntityID=4",
            "DebugPrintPower() -         tag=CARDTYPE value=GAME",
            "DebugPrintPower() -         tag=TURN value=5",
            $"DebugPrintPower() -     Player EntityID=14 PlayerID={Me} GameAccountId=[hi=144115198130930503 lo=11]",
            $"DebugPrintPower() -         tag=CONTROLLER value={Me}",
            "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
            "DebugPrintGame() - GameType=GT_BATTLEGROUNDS",
            $"DebugPrintGame() - PlayerID={Me}, PlayerName=Me#1234",
            .. Entity(30, "BG_SOURCE", "PLAY"),
            .. Entity(40, "BG_A", "SETASIDE"),
            .. Entity(41, "BG_B", "SETASIDE"),
            .. Entity(42, "BG_C", "SETASIDE"),
            .. Entity(50, "BG_PLAYED", "HAND"),
            "DebugPrintEntityChoices() - id=7 Player=Me#1234 TaskList=12 ChoiceType=GENERAL CountMin=1 CountMax=1",
            $"DebugPrintEntityChoices() -   Source={Ref(30, "BG_SOURCE", "PLAY")}",
            $"DebugPrintEntityChoices() -   Entities[0]={Ref(40, "BG_A", "SETASIDE")}",
            $"DebugPrintEntityChoices() -   Entities[1]={Ref(41, "BG_B", "SETASIDE")}",
            $"DebugPrintEntityChoices() -   Entities[2]={Ref(42, "BG_C", "SETASIDE")}",
            "DebugPrintEntitiesChosen() - id=7 Player=Me#1234 EntitiesCount=1",
            $"DebugPrintEntitiesChosen() -   Entities[0]={Ref(41, "BG_B", "SETASIDE")}",
            $"DebugPrintPower() - TAG_CHANGE Entity={Ref(50, "BG_PLAYED", "HAND")} tag=ZONE value=PLAY ",
        ]);

        var view = tracker.BuildView(parser.Game!);

        var choice = Assert.Single(view.Choices);
        Assert.Equal("discover", choice.Kind);
        Assert.Equal("BG_SOURCE", choice.Source);
        Assert.Equal(["BG_A", "BG_B", "BG_C"], choice.Offered);
        Assert.Equal(["BG_B"], choice.Picked);
        Assert.Equal("BG_PLAYED", Assert.Single(view.Plays).CardId);
    }

    private static void Feed(PowerParser parser, params string[] bodies)
    {
        foreach (var body in bodies)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
    }

    private static string[] Entity(int id, string cardId, string zone) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={Me}",
        "DebugPrintPower() -     tag=CARDTYPE value=MINION",
    ];

    private static string Ref(int id, string cardId, string zone) =>
        $"[entityName=X id={id} zone={zone} zonePos=0 cardId={cardId} player={Me}]";

    // Live check against the player's own newest Battlegrounds log; a no-op on machines without Hearthstone logs.
    // The deterministic check is A_discover_pick_and_a_play_are_recorded.
    [Fact]
    public void Picks_and_plays_are_recorded_from_a_real_game()
    {
        var logs = Directory.Exists(@"D:\Hearthstone\Logs")
            ? Directory.EnumerateFiles(@"D:\Hearthstone\Logs", "Power*.log", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .Where(f => ReadShared(f).Any(x => x.Contains("GT_BATTLEGROUNDS", StringComparison.Ordinal)))
                .Take(1)
                .ToList()
            : [];
        if (logs.Count == 0)
        {
            return;
        }

        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(logs[0])));
        var tracker = new BattlegroundsTracker { Cards = CardDb.Empty };
        parser.AddListener(tracker);
        BattlegroundsView? best = null;
        var lines = 0;
        foreach (var line in ReadShared(logs[0]))
        {
            parser.Feed(line);
            if (++lines % 200 != 0 || parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                continue;
            }

            parser.Flush();
            var view = tracker.BuildView(game);
            if (view.Choices.Count + view.Plays.Count >= (best is null ? 1 : best.Choices.Count + best.Plays.Count))
            {
                best = view;
            }
        }

        Assert.NotNull(best);
        Assert.NotEmpty(best!.Plays);
        // A short game can end before any Discover, triple reward or trinket (hero picks are not recorded).
        if (best.Choices.Count > 0)
        {
            Assert.Contains(best.Choices, c => c.Picked.Count > 0);
        }
        Assert.All(best.Choices, c => Assert.All(c.Picked, p => Assert.Contains(p, c.Offered)));
    }

    private static IEnumerable<string> ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}