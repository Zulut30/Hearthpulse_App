using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Power;

namespace Hearthpulse.Core.Tracking;

/// <summary>
/// Off-screen render helper (<c>--render-ui</c>): replays the newest kept Constructed Power.log, read-only, with the deck
/// its session's Decks.log names for each game, and returns the most telling moment for the deck and hand panels.
/// </summary>
public static class ConstructedReplay
{
    /// <summary>
    /// The view with the most to show (a long opponent hand with known or created cards, mid-game), or null when no
    /// kept log holds a Constructed game.
    /// </summary>
    public static ConstructedView? Latest(string installDir, CardDb cards, ConstructedMeta? meta)
    {
        foreach (var session in HearthstoneInstall.AllLogSessions(installDir).Reverse())
        {
            foreach (var log in new[] { "Power.log", "Power_old.log" }.Select(f => Path.Combine(session, f)).Where(File.Exists))
            {
                // The client may rotate or delete a log while it is read: then the next one is tried.
                try
                {
                    if (!ReadShared(log).Take(50_000).Any(l => l.Contains("GameType=GT_RANKED", StringComparison.Ordinal)
                                                               || l.Contains("GameType=GT_CASUAL", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    if (Replay(log, DecksLog.Read(session), cards, meta) is { } view)
                    {
                        return view;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Diag.Error($"Replaying {log} failed", ex);
                }
            }
        }

        return null;
    }

    private static ConstructedView? Replay(string log, IReadOnlyList<DecksLogEntry> decks, CardDb cards, ConstructedMeta? meta)
    {
        var start = HearthstoneInstall.SessionStart(Path.GetDirectoryName(log)!) ?? File.GetLastWriteTime(log);
        var parser = new PowerParser(DateOnly.FromDateTime(start));
        var tracker = new ConstructedTracker { Meta = meta };
        parser.AddListener(tracker);
        parser.AddListener(tracker.Counters);
        GameState? current = null;
        ConstructedView? best = null;
        var bestScore = 0;
        var lines = 0;
        foreach (var line in ReadShared(log))
        {
            parser.Feed(line);
            if (++lines % 200 != 0 || parser.Game is not { IsConstructedLike: true, LocalPlayerId: > 0 } game || game.IsComplete)
            {
                continue;
            }

            if (game != current)
            {
                current = game;
                var finding = decks.LastOrDefault(d => d.FindingGame && d.At <= game.StartedAt && game.StartedAt - d.At <= DeckRecords.MaxQueue);
                tracker.Deck = finding is null ? null : DeckString.TryDecode(finding.Code, finding.Name, finding.DeckId);
            }

            parser.Flush();
            var view = tracker.BuildView(game, cards);
            var score = Score(view);
            if (score > bestScore)
            {
                best = view;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Mid-game with a deck; more cards in the opponent's hand and more marks with a portrait score higher.</summary>
    private static int Score(ConstructedView view) =>
        view.Turn < 3 || !view.DeckKnown
            ? 0
            : view.OpponentHand.Count + (3 * view.OpponentHand.Count(h => h.CardId is not null || h.CreatedById is not null))
              + (view.Deck.Count(r => r.Remaining < r.Total) > 3 ? 2 : 0);

    /// <summary>Reads a log line by line while Hearthstone may still be writing it.</summary>
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
