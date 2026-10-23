using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hearthstone;
using Hearthpulse.Core.Memory;
using Hearthpulse.Core.Tracking;

namespace Hearthpulse.Bench;

/// <summary>Formatting and memory helpers.</summary>
internal static class Measure
{
    public static string Mb(long bytes) => (bytes / 1048576.0).ToString("N1", CultureInfo.InvariantCulture) + " MB";

    public static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];

    public static string Spread(List<double> values)
    {
        if (values.Count == 0)
        {
            return "none";
        }

        var sorted = values.OrderBy(v => v).ToList();
        return string.Create(CultureInfo.InvariantCulture,
            $"avg {sorted.Average():N3}, p50 {Percentile(sorted, .5):N3}, p95 {Percentile(sorted, .95):N3}, p99 {Percentile(sorted, .99):N3}, max {sorted[^1]:N1} ms");
    }

    /// <summary>Working set, private bytes, live managed heap (after a full GC), LOH and GC committed memory.</summary>
    public static void Memory(string label)
    {
        var live = GC.GetTotalMemory(forceFullCollection: true);
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[ram] {label,-40} WS {process.WorkingSet64 / 1048576,4} MB  private {process.PrivateMemorySize64 / 1048576,4} MB  managed {live / 1048576,4} MB  LOH {info.GenerationInfo[3].SizeAfterBytes / 1048576,3} MB (free {info.GenerationInfo[3].FragmentationAfterBytes / 1048576,3})  GC committed {info.TotalCommittedBytes / 1048576,4} MB"));
    }

    public static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Hearthpulse-bench/1.0");
        return http;
    }
}

/// <summary>
/// Feeds a kept Power.log to a <see cref="GameSession"/> the way LogWatcher does: batches of 100 ms of log time,
/// <see cref="GameSession.OnTick"/> after each batch with the clock at the batch's log time. Memory is never read.
/// </summary>
internal static class LogReplay
{
    public static IReadOnlyList<(string Dir, string Path, DateTime Start)> Logs(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root, "Hearthstone_*")
                .Select(d => (Dir: d, Path: Path.Combine(d, "Power.log"), Start: HearthstoneInstall.SessionStart(d)))
                .Where(l => l.Start is not null && File.Exists(l.Path))
                .Select(l => (l.Dir, l.Path, l.Start!.Value))
                .ToList()
            : [];

    /// <summary>A session with test hooks: replay clock, Hearthstone "running", no memory reads.</summary>
    public static (GameSession Session, Action<DateTime> SetClock, string Db) NewSession(CardDb cards)
    {
        var db = Path.Combine(Path.GetTempPath(), $"hp-bench-{Guid.NewGuid():N}.db");
        var now = DateTime.UtcNow;
        var session = new GameSession(new HistoryStore(db), new HearthstoneMemory())
        {
            Clock = () => now,
            IsHearthstoneRunning = () => true,
            ReadsMemory = false,
            Cards = cards,
        };
        return (session, t => now = t, db);
    }

    /// <summary>Replays one log; <paramref name="onBatch"/> wraps OnLines, <paramref name="onTick"/> wraps OnTick.</summary>
    public static long Run(GameSession session, Action<DateTime> setClock, string dir, string path, DateTime start,
        Action<Action>? onBatch = null, Action<Action>? onTick = null)
    {
        onBatch ??= a => a();
        onTick ??= a => a();
        setClock(start.ToUniversalTime());
        session.OnSessionStarted(dir, start, attachedMidSession: false);
        var batch = new List<string>(256);
        TimeSpan? batchStart = null;
        var day = start.Date;
        var last = TimeSpan.Zero;
        var at = start;
        long lines = 0;

        void Flush(DateTime time)
        {
            if (batch.Count > 0)
            {
                var lines2 = batch;
                onBatch(() => session.OnLines("Power.log", lines2, catchingUp: false));
                lines += batch.Count;
                batch = new List<string>(256);
            }

            setClock(time.ToUniversalTime());
            onTick(session.OnTick);
        }

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 18 && TimeSpan.TryParseExact(line.AsSpan(2, 16), @"hh\:mm\:ss\.fffffff", CultureInfo.InvariantCulture, out var ts))
                {
                    if (ts < last - TimeSpan.FromHours(1))
                    {
                        day = day.AddDays(1);
                    }

                    last = ts;
                    at = day + ts;
                    if (batchStart is { } bs && ts - bs >= TimeSpan.FromMilliseconds(100))
                    {
                        Flush(at);
                        batchStart = ts;
                    }

                    batchStart ??= ts;
                }

                batch.Add(line);
            }
        }

        // The quiet flush needs 150 ms without lines, then one more publish.
        Flush(at.AddSeconds(1));
        Flush(at.AddSeconds(2));
        return lines;
    }

    public static void Delete(string db)
    {
        try
        {
            File.Delete(db);
        }
        catch (IOException)
        {
            // SQLite may still hold the file; it is in the temp folder.
        }
    }
}
