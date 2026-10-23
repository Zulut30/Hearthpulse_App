using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking.Counters;

/// <summary>
/// The counters panel for render checks (<c>--render-ui</c>): a Constructed game from the latest kept Power.log that has
/// one, at the moment with the most counters under way, or a made-up game when no kept log has one.
/// </summary>
public static class CounterScene
{
    /// <summary>The richest moment of the latest kept Constructed game, or null when no kept log has one.</summary>
    public static ConstructedView? FromLatestLog(string installDir, CardDb cards)
    {
        foreach (var log in HearthstoneInstall.AllLogSessions(installDir).Reverse()
                     .SelectMany(d => new[] { Path.Combine(d, "Power.log"), Path.Combine(d, "Power_old.log") })
                     .Where(File.Exists))
        {
            if (Replay(log, cards) is { } view)
            {
                return view;
            }
        }

        return null;
    }

    /// <summary>A game in progress like the one players see: the opponent's C'Thun abilities, your corpses and spells.</summary>
    public static ConstructedView Synthetic(CardDb cards)
    {
        CounterCard Card(string id, int count = 1, bool? done = null) => new(id, cards.NameOf(id), count, done);
        CounterView Counter(string id, string value, bool opponent, string icon, double? progress = null) =>
            new(id, Counters.All.First(c => c.Id == id).Title, value, progress, opponent, icon, Counters.All.First(c => c.Id == id).Description, [], true);

        IReadOnlyList<CounterView> counters =
        [
            Counter("corpses", "16", false, "RLK_060") with { RelatedCards = [Card("RLK_060", 2), Card("RLK_120", 1)] },
            Counter("spells-cast", "5", false, "RLK_025") with { Counted = [Card("RLK_025", 2), Card("RLK_060"), Card("RLK_035", 2)] },
            Counter("minions-died-turn", "0", false, "RLK_060"),
            Counter("cthun", "1/4", true, "BE_013", 0.25) with
            {
                Goal = 4,
                Parts = [Card("BE_013t1", 0, false), Card("BE_013t2", 0, false), Card("BE_013t3", 0, true), Card("BE_013t4", 0, false)],
                RelatedCards = [Card("BE_013"), Card("BE_204")],
            },
        ];
        return new ConstructedView(7, null, false, "HERO_11", "DEATHKNIGHT", "HERO_09", "PRIEST", "Opponent", [], 18, 5, [], [], 17, 6, 0, null, null)
        {
            Counters = counters,
        };
    }

    private static ConstructedView? Replay(string log, CardDb cards)
    {
        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
        var tracker = new ConstructedTracker();
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        ConstructedView? best = null;
        var bestScore = 0;
        var lines = 0;
        foreach (var line in Lines(log))
        {
            parser.Feed(line);
            if (++lines % 200 != 0 || parser.Game is not { IsConstructedLike: true, LocalPlayerId: > 0, OpponentPlayerId: > 0 } game)
            {
                continue;
            }

            parser.Flush();
            var view = tracker.BuildView(game, cards);
            // Counters a card cares about, with the cards to show in their tooltips; C'Thun's abilities first.
            var score = view.Counters.Where(c => c.Relevant)
                .Sum(c => 1 + (c.Parts.Any(p => p.Done == true) ? 20 : 0) + (c.Counted.Count + c.RelatedCards.Count > 0 ? 1 : 0));
            if (score >= bestScore)
            {
                (best, bestScore) = (view, score);
            }
        }

        return best;
    }

    /// <summary>Reads a log while Hearthstone may still be writing it; a log it holds exclusively is read from a copy.</summary>
    private static IEnumerable<string> Lines(string path)
    {
        string[] lines;
        try
        {
            lines = ReadShared(path);
        }
        catch (IOException)
        {
            var copy = Path.Combine(Path.GetTempPath(), $"hearthpulse-render-{Guid.NewGuid():N}.log");
            File.Copy(path, copy);
            try
            {
                lines = ReadShared(copy);
            }
            finally
            {
                File.Delete(copy);
            }
        }

        return lines;
    }

    private static string[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return [.. lines];
    }
}
