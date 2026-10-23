using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

public sealed class ChoiceTrackingTests
{
    // Live check against the player's own logs; skipped on machines without Hearthstone logs.
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
        Assert.Contains(best.Choices, c => c.Picked.Count > 0);
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