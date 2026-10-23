using System.Diagnostics;
using System.Globalization;
using System.IO;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>Benchmarks of Core alone: card loading, the Power.log pipeline, history queries.</summary>
internal static class CoreBench
{
    public static void Cards()
    {
        using var http = Http();
        Memory("before cards");
        var started = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        Console.WriteLine($"[cards] CardDb.LoadAsync ruRU: {started.ElapsedMilliseconds} ms, alloc {Mb(GC.GetTotalAllocatedBytes(precise: true) - allocated)}, {cards.Count} cards");
        Memory("after cards");

        // A second load on a pool thread (a locale switch, or the app's next start-up path) shows what stays pooled.
        Task.Run(() => CardDb.LoadAsync("ruRU", http)).GetAwaiter().GetResult();
        Memory("after a second load on a pool thread");
        GC.KeepAlive(cards);
    }

    public static void Replay(Options options)
    {
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var api = new HearthpulseApi(http);
        var meta = BattlegroundsMeta.LoadAsync(new HearthpulseData(api)).GetAwaiter().GetResult();
        var catalog = CardPoolCatalog.LoadAsync(api, http).GetAwaiter().GetResult();
        var logs = LogReplay.Logs(options.Logs);
        for (var round = 0; round < options.Rounds; round++)
        {
            var publish = new List<double>();
            var parse = new List<double>();
            long lines = 0, publishAlloc = 0, parseAlloc = 0;
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var wall = Stopwatch.StartNew();
            foreach (var (dir, path, start) in logs)
            {
                var (session, setClock, db) = LogReplay.NewSession(cards);
                session.SetMeta(meta);
                session.SetCardPoolCatalog(catalog);
                var published = false;
                session.SnapshotChanged += _ => published = true;
                lines += LogReplay.Run(session, setClock, dir, path, start,
                    onBatch: feed =>
                    {
                        var t = Stopwatch.GetTimestamp();
                        var a = GC.GetAllocatedBytesForCurrentThread();
                        feed();
                        parse.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                        parseAlloc += GC.GetAllocatedBytesForCurrentThread() - a;
                    },
                    onTick: tick =>
                    {
                        published = false;
                        var t = Stopwatch.GetTimestamp();
                        var a = GC.GetAllocatedBytesForCurrentThread();
                        tick();
                        if (published)
                        {
                            publish.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
                            publishAlloc += GC.GetAllocatedBytesForCurrentThread() - a;
                        }
                    });
                LogReplay.Delete(db);
            }

            Console.WriteLine($"[replay r{round}] {logs.Count} logs, {lines:N0} lines, wall {wall.ElapsedMilliseconds:N0} ms, {lines / Math.Max(0.001, wall.Elapsed.TotalSeconds):N0} lines/s");
            Console.WriteLine($"[replay r{round}] parse {parse.Count:N0} batches: {Spread(parse)}; alloc {Mb(parseAlloc)} ({parseAlloc / Math.Max(1, lines)} B/line)");
            Console.WriteLine($"[replay r{round}] publish {publish.Count:N0}: {Spread(publish)}; alloc {Mb(publishAlloc)} ({publishAlloc / Math.Max(1, publish.Count) / 1024} KB/publish)");
            Console.WriteLine($"[replay r{round}] GCs gen0 {GC.CollectionCount(0) - gen0}, gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}");
        }
    }

    /// <summary>
    /// Log time during which the card under the mouse would be read from the game: before (every Constructed game with a
    /// supported pool context, every 200 ms) and now (<see cref="GameSession.HoverReadsWanted"/>, every 250 ms).
    /// Memory is never read here.
    /// </summary>
    public static void Hover(Options options)
    {
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var catalog = CardPoolCatalog.LoadAsync(new HearthpulseApi(http), http).GetAwaiter().GetResult();
        TimeSpan inGame = default, before = default, now = default;
        foreach (var (dir, path, start) in LogReplay.Logs(options.Logs))
        {
            var (session, setClock, db) = LogReplay.NewSession(cards);
            session.SetCardPoolCatalog(catalog);
            DateTime clock = default, last = default;
            LogReplay.Run(session, t =>
            {
                setClock(t);
                clock = t;
            }, dir, path, start, onTick: tick =>
            {
                var step = last == default ? TimeSpan.Zero : clock - last;
                last = clock;
                if (session.Current.Constructed is not null && session.Game is { } game)
                {
                    inGame += step;
                    if (CardPoolTracker.ContextOf(game, cards).IsSupported)
                    {
                        before += step;
                    }

                    if (session.HoverReadsWanted)
                    {
                        now += step;
                    }
                }

                tick();
            });
            LogReplay.Delete(db);
        }

        var readsBefore = before.TotalMilliseconds / 200;
        var readsNow = now.TotalMilliseconds / GameSession.HoverInterval.TotalMilliseconds;
        Console.WriteLine($"[hover] Constructed game time {inGame.TotalMinutes:N1} min; card-under-mouse reads on before {before.TotalMinutes:N1} min, now {now.TotalMinutes:N1} min");
        Console.WriteLine($"[hover] reads before {readsBefore:N0} ({readsBefore / Math.Max(1, inGame.TotalMinutes):N0}/min), now {readsNow:N0} ({readsNow / Math.Max(1, inGame.TotalMinutes):N0}/min): {100 - (100 * readsNow / Math.Max(1, readsBefore)):N0}% fewer");
    }

    /// <summary>Runs <see cref="CacheCleanup"/> on the data copy's cache and prints its size per folder before and after.</summary>
    public static void Cleanup()
    {
        void Sizes(string when)
        {
            foreach (var dir in new DirectoryInfo(AppPaths.Cache).EnumerateDirectories().Prepend(new DirectoryInfo(AppPaths.Cache)))
            {
                var files = dir.EnumerateFiles("*", dir.FullName == AppPaths.Cache ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories).ToList();
                Console.WriteLine($"[cleanup] {when,-6} {(dir.FullName == AppPaths.Cache ? "cache/" : dir.Name + "/"),-12} {files.Count,5} files {Mb(files.Sum(f => f.Length)),10}  (.tmp: {files.Count(f => f.Extension == ".tmp")})");
            }
        }

        Sizes("before");
        var started = Stopwatch.StartNew();
        var result = CacheCleanup.Run();
        Console.WriteLine($"[cleanup] removed {result.Files} files, {Mb(result.Bytes)} in {started.ElapsedMilliseconds} ms");
        Sizes("after");
    }

    public static void History(Options options)
    {
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var seed = SeedGames(options.Logs, cards);
        Console.WriteLine($"[history] {seed.Count} seed games ({seed.Count(g => g.IsBattlegrounds)} Battlegrounds)");
        if (seed.Count == 0)
        {
            return;
        }

        foreach (var n in new[] { 1000, 5000, options.Games }.Where(n => n > 0).Distinct())
        {
            var db = Path.Combine(Path.GetTempPath(), $"hp-bench-history-{Guid.NewGuid():N}.db");
            var store = new HistoryStore(db);
            var started = Stopwatch.StartNew();
            Fill(store, seed, n);
            Console.WriteLine($"[history n={n}] insert {started.ElapsedMilliseconds:N0} ms ({(double)started.ElapsedMilliseconds / n:N2} ms/game), db {Mb(new FileInfo(db).Length)}");

            T Time<T>(string name, Func<T> query)
            {
                var t = Stopwatch.StartNew();
                var a = GC.GetAllocatedBytesForCurrentThread();
                var value = query();
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[history n={n}] {name,-36} {t.Elapsed.TotalMilliseconds,8:N1} ms  alloc {Mb(GC.GetAllocatedBytesForCurrentThread() - a)}"));
                return value;
            }

            Time("GameCount", store.GameCount);
            Time("Recent(200)", () => store.Recent(200));
            var all = Time("Recent(all)", () => store.Recent(int.MaxValue));
            Time("HeroStats", () => store.HeroStats(h => h));
            Time("ClassStats", store.ClassStats);
            Time("DeckStats", store.DeckStats);
            Time("MatchupStats", () => store.MatchupStats());
            Time("RatingHistory", () => store.RatingHistory());
            Time("PlacementDistribution", () => store.PlacementDistribution());
            Time("BattlegroundsStats.Summary", () => BattlegroundsStats.Summary(all, false));
            Time("BattlegroundsStats.Heroes", () => BattlegroundsStats.Heroes(all, h => h, false));
            Time("ConstructedStats.Summary", () => ConstructedStats.Summary(all));
            Time("ConstructedStats.ByOpponentClass", () => ConstructedStats.ByOpponentClass(all));
            var versions = Time("DeckFamilies.Versions", () => DeckFamilies.Versions(all));
            Time("DeckFamilies.Build", () => DeckFamilies.Build(versions, store.DeckFamilies()));
            Time("WithCards(all)", () => store.WithCards(all));
            LogReplay.Delete(db);
        }
    }

    /// <summary>Real games: the data copy's history plus every game in the kept logs.</summary>
    public static List<GameRecord> SeedGames(string logs, CardDb cards)
    {
        var seed = File.Exists(AppPaths.Database) ? new HistoryStore(AppPaths.Database).Recent(int.MaxValue).ToList() : [];
        foreach (var (_, path, start) in LogReplay.Logs(logs))
        {
            seed.AddRange(LogBackfill.Parse(path, DateOnly.FromDateTime(start), cards));
        }

        return seed;
    }

    /// <summary>Clones the seed games, one hour apart, until the store holds <paramref name="n"/> games.</summary>
    public static void Fill(HistoryStore store, IReadOnlyList<GameRecord> seed, int n)
    {
        for (var i = store.GameCount(); i < n; i++)
        {
            var r = seed[i % seed.Count];
            store.Add(r with { Key = r.Key + "#" + i.ToString(CultureInfo.InvariantCulture), StartedAt = r.StartedAt.AddHours(-i), EndedAt = r.EndedAt.AddHours(-i) });
        }
    }
}
