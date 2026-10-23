using Hearthlens.Core.Cards;
using Hearthlens.Core.Hearthstone;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;

namespace Hearthlens.Core.Data;

public sealed record BackfillResult(int Sessions, int GamesFound, int GamesAdded);

/// <summary>Imports finished games from every Power log the client kept on disk.</summary>
public static class LogBackfill
{
    public static BackfillResult Run(string installDir, HistoryStore history, CardDb cards, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        int sessions = 0, found = 0, added = 0;
        foreach (var session in HearthstoneInstall.AllLogSessions(installDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = HearthstoneInstall.SessionStart(session);
            if (start is null)
            {
                continue;
            }

            foreach (var file in new[] { "Power_old.log", "Power.log" })
            {
                var path = Path.Combine(session, file);
                if (!File.Exists(path))
                {
                    continue;
                }

                sessions++;
                progress?.Report($"Reading {Path.GetFileName(session)}");
                foreach (var record in Parse(path, DateOnly.FromDateTime(start.Value), cards, cancellationToken))
                {
                    found++;
                    if (history.Add(record))
                    {
                        added++;
                    }
                }
            }
        }

        return new BackfillResult(sessions, found, added);
    }

    public static IReadOnlyList<GameRecord> Parse(string powerLogPath, DateOnly date, CardDb cards, CancellationToken cancellationToken = default)
    {
        var records = new List<GameRecord>();
        var parser = new PowerParser(date);
        var constructed = new ConstructedTracker();
        var battlegrounds = new BattlegroundsTracker { Cards = cards };
        var collector = new Collector(records, battlegrounds, cards);
        parser.AddListener(constructed);
        parser.AddListener(battlegrounds);
        parser.AddListener(collector);

        using var stream = new FileStream(powerLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            if (++count % 50_000 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            parser.Feed(line);
        }

        parser.AbandonCurrentGame();
        return records;
    }

    private sealed class Collector(List<GameRecord> records, BattlegroundsTracker battlegrounds, CardDb cards) : IPowerListener
    {
        public void OnGameEnded(GameState game)
        {
            if (GameRecords.Build(game, battlegrounds, cards, null, "backfill") is { } record)
            {
                records.Add(record);
            }
        }
    }
}
