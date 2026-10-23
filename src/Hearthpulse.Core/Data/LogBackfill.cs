using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Logs;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Core.Data;

public sealed record BackfillResult(int Sessions, int GamesFound, int GamesAdded)
{
    /// <summary>Games that got their deck from Decks.log (see <see cref="LogBackfill.FillDecks"/>).</summary>
    public int DecksFilled { get; init; }
}

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
                    else
                    {
                        // A game saved before the My Stats fields existed gets them from its log now.
                        history.FillMissingFields(record);
                    }
                }
            }
        }

        return new BackfillResult(sessions, found, added) { DecksFilled = FillDecks(installDir, history, cards) };
    }

    /// <summary>
    /// Fills the deck of Constructed games saved without a code (imported games, and live games from before the
    /// Decks.log fix) from the "Finding Game With Deck" block the same log session wrote just before each game.
    /// Cheap: reads only the small Decks.log files and the history. Returns how many games changed.
    /// </summary>
    public static int FillDecks(string installDir, HistoryStore history, CardDb cards)
    {
        // Runs in the background at every start: a busy database or an unreadable log must only cost this pass.
        try
        {
            var entries = DecksLog.ReadAll(installDir);
            if (entries.Count == 0)
            {
                return 0;
            }

            // Every game, Battlegrounds included: a game between two Constructed ones bounds which Finding is theirs.
            var games = history.Recent(int.MaxValue);
            var fills = DeckRecords.MatchFindings(games, entries, code => DeckRecords.ClassOf(code, cards));
            var changed = fills.Count == 0 ? 0 : history.FillDecks(fills);
            if (changed > 0)
            {
                Diag.Info($"Deck filled in for {changed} past games from Decks.log");
            }

            return changed;
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            Diag.Error("Filling past games' decks from Decks.log failed", ex);
            return 0;
        }
    }

    public static IReadOnlyList<GameRecord> Parse(string powerLogPath, DateOnly date, CardDb cards, CancellationToken cancellationToken = default)
    {
        var records = new List<GameRecord>();
        var parser = new PowerParser(date);
        var constructed = new ConstructedTracker();
        var battlegrounds = new BattlegroundsTracker { Cards = cards };
        var facts = new GameFactsTracker();
        var collector = new Collector(records, battlegrounds, cards, facts);
        parser.AddListener(constructed);
        parser.AddListener(battlegrounds);
        parser.AddListener(facts);
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

    private sealed class Collector(List<GameRecord> records, BattlegroundsTracker battlegrounds, CardDb cards, GameFactsTracker facts) : IPowerListener
    {
        public void OnGameEnded(GameState game)
        {
            if (GameRecords.Build(game, battlegrounds, cards, null, "backfill", facts: facts) is { } record)
            {
                records.Add(record);
            }
        }
    }
}
