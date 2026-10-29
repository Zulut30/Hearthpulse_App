using System.Diagnostics;
using System.Globalization;
using System.IO;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Hearthstone;
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
        var (gen0, gen1, gen2, paused) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
        using var large = LargeAllocations.StartIfAsked();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        large?.Report("cards");
        Console.WriteLine($"[cards] CardDb.LoadAsync ruRU: {started.ElapsedMilliseconds} ms, alloc {Mb(GC.GetTotalAllocatedBytes(precise: true) - allocated)}, {cards.Count} cards, "
                          + $"GCs gen0 {GC.CollectionCount(0) - gen0} gen1 {GC.CollectionCount(1) - gen1} gen2 {GC.CollectionCount(2) - gen2}, paused {(GC.GetTotalPauseDuration() - paused).TotalMilliseconds:N0} ms");
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
        // HP_BENCH_PUBLISH=1: after each publish, its parts are built again on their own and timed (doubles the work).
        var parts = Environment.GetEnvironmentVariable("HP_BENCH_PUBLISH") == "1" ? new PublishParts() : null;
        for (var round = 0; round < options.Rounds; round++)
        {
            var publish = new List<double>();
            var parse = new List<double>();
            long lines = 0, publishAlloc = 0, parseAlloc = 0;
            var gen0 = GC.CollectionCount(0);
            var gen1 = GC.CollectionCount(1);
            var gen2 = GC.CollectionCount(2);
            var wall = Stopwatch.StartNew();
            var logTime = TimeSpan.Zero;
            foreach (var (dir, path, start) in logs)
            {
                var (session, sessionClock, db) = LogReplay.NewSession(cards);
                // The log time the replay covers: what "a second of the game" in the per-second cost below means.
                DateTime? first = null;
                var last = DateTime.MinValue;
                void SetClock(DateTime t)
                {
                    first ??= t;
                    last = t;
                    sessionClock(t);
                }

                session.SetMeta(meta);
                session.SetCardPoolCatalog(catalog);
                var published = false;
                session.SnapshotChanged += _ => published = true;
                parts?.Watch(session, cards);
                lines += LogReplay.Run(session, SetClock, dir, path, start,
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
                logTime += last - (first ?? last);
                LogReplay.Delete(db);
            }

            Console.WriteLine($"[replay r{round}] {logs.Count} logs, {lines:N0} lines, wall {wall.ElapsedMilliseconds:N0} ms, {lines / Math.Max(0.001, wall.Elapsed.TotalSeconds):N0} lines/s");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[replay r{round}] log time {logTime.TotalMinutes:N1} min: {(parse.Sum() + publish.Sum()) / Math.Max(1, logTime.TotalSeconds):N3} ms of parse + publish per second of log, wall {wall.Elapsed.TotalMilliseconds / Math.Max(1, logTime.TotalSeconds):N3} ms/s"));
            Console.WriteLine($"[replay r{round}] parse {parse.Count:N0} batches: {Spread(parse)}; alloc {Mb(parseAlloc)} ({parseAlloc / Math.Max(1, lines)} B/line)");
            Console.WriteLine($"[replay r{round}] publish {publish.Count:N0}: {Spread(publish)}; alloc {Mb(publishAlloc)} ({publishAlloc / Math.Max(1, publish.Count) / 1024} KB/publish)");
            Console.WriteLine($"[replay r{round}] GCs gen0 {GC.CollectionCount(0) - gen0}, gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}");
            parts?.Report($"r{round}");
        }

        // The parser alone, no listeners, lines already in memory: what reading Power.log costs before the trackers.
        var all = logs.Select(l => (l.Start, Lines: File.ReadAllLines(l.Path))).ToList();
        for (var round = 0; round < Math.Max(2, options.Rounds); round++)
        {
            long count = 0;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            foreach (var (start, lines) in all)
            {
                var parser = new Hearthpulse.Core.Power.PowerParser(DateOnly.FromDateTime(start));
                foreach (var line in lines)
                {
                    parser.Feed(line);
                }

                count += lines.Length;
            }

            Console.WriteLine($"[parser r{round}] {count:N0} lines in {watch.ElapsedMilliseconds:N0} ms ({count / Math.Max(0.001, watch.Elapsed.TotalSeconds):N0} lines/s), alloc {Mb(GC.GetAllocatedBytesForCurrentThread() - allocated)} ({(GC.GetAllocatedBytesForCurrentThread() - allocated) / Math.Max(1, count)} B/line)");
        }

        // What a parsed game keeps alive: the parser of the longest log, held until its last line.
        var longest = all.MaxBy(l => l.Lines.Length);
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var kept = new Hearthpulse.Core.Power.PowerParser(DateOnly.FromDateTime(longest.Start));
        foreach (var line in longest.Lines)
        {
            kept.Feed(line);
        }

        var held = GC.GetTotalMemory(forceFullCollection: true) - before;
        Console.WriteLine($"[parser] live after the longest log ({longest.Lines.Length:N0} lines, game of {kept.Game?.Entities.Count ?? 0} entities): {Mb(held)}");
        GC.KeepAlive(kept);
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
    /// <summary>
    /// The log pipeline the app runs all day: a LogWatcher on the installed game's logs feeding a GameSession that asks
    /// whether Hearthstone runs, as in the app (memory is not read). CPU time of this process over 20 s after it caught
    /// up; run it while Hearthstone is closed (idle) or open in the menu.
    /// </summary>
    public static void Watch()
    {
        using var http = Http();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var install = HearthstoneInstall.FindInstallDirectory(null);
        var db = Path.Combine(Path.GetTempPath(), $"hp-bench-{Guid.NewGuid():N}.db");
        var session = new GameSession(new HistoryStore(db), new Core.Memory.HearthstoneMemory()) { ReadsMemory = false, Cards = cards };
        var snapshots = 0;
        session.SnapshotChanged += _ => snapshots++;
        using (var watcher = new Core.Logs.LogWatcher(session, () => install))
        {
            watcher.Start();
            Thread.Sleep(5000);
            using var process = Process.GetCurrentProcess();
            void Window(string what)
            {
                process.Refresh();
                var cpu = process.TotalProcessorTime;
                var user = process.UserProcessorTime;
                var published = snapshots;
                var gen0 = GC.CollectionCount(0);
                var allocated = GC.GetTotalAllocatedBytes();
                Thread.Sleep(20_000);
                process.Refresh();
                var used = process.TotalProcessorTime - cpu;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[watch] {what}: CPU {used.TotalMilliseconds:N0} ms in 20 s ({used.TotalMilliseconds / 20:N1} ms/s, user {(process.UserProcessorTime - user).TotalMilliseconds:N0} ms), "
                    + $"alloc {(GC.GetTotalAllocatedBytes() - allocated) / 1024.0 / 20:N1} KB/s, gen0 {GC.CollectionCount(0) - gen0}, snapshots {snapshots - published}"));
            }

            Window($"install {install ?? "none"}, game running {HearthstoneInstall.IsRunning()}");
            // The same without the "is Hearthstone running" check: what the log files alone cost.
            session.IsHearthstoneRunning = () => false;
            Window("without the process check");
        }

        LogReplay.Delete(db);
    }

    /// <summary>
    /// What one idle pass of the log watcher costs (the four tails read with nothing new, the unread bytes and the
    /// Power.log size it reports) and one "is Hearthstone running" with the game closed, on this thread's CPU clock.
    /// Builds against the commit before stage 4 too (the sizes the read saw are looked up by name).
    /// </summary>
    /// <summary>
    /// What the app loads after the log watcher starts: Battlegrounds meta, Constructed meta (the settings' slice) and
    /// the card pool catalog, from the cache of the data copy (a stale or missing answer is downloaded). Twice each.
    /// </summary>
    public static void Meta()
    {
        using var http = Http();
        var settings = AppSettings.Load();
        var cards = CardDb.LoadAsync("ruRU", http).GetAwaiter().GetResult();
        var api = new HearthpulseApi(http);
        void Time(string what, Func<object?> load)
        {
            for (var pass = 1; pass <= 2; pass++)
            {
                var allocated = GC.GetTotalAllocatedBytes(precise: true);
                var watch = Stopwatch.StartNew();
                var result = load();
                Console.WriteLine($"[meta] {what,-22} pass {pass}: {watch.ElapsedMilliseconds,5:N0} ms, alloc {Mb(GC.GetTotalAllocatedBytes(precise: true) - allocated),9}{(result is null ? " (none)" : "")}");
            }
        }

        Time("Battlegrounds meta", () => BattlegroundsMeta.LoadAsync(new HearthpulseData(api)).GetAwaiter().GetResult());
        Time("Constructed meta", () => Hearthpulse.Core.Constructed.ConstructedMeta.LoadAsync(new HearthpulseData(api), cards, settings.MetaFormat,
            settings.MetaRank, settings.MetaPeriod, cards.Locale).GetAwaiter().GetResult());
        Time("card pool catalog", () => CardPoolCatalog.LoadAsync(api, http).GetAwaiter().GetResult());
        Memory("after meta");

        // The hosts ask MemoryTrimmer for a collection after every Constructed meta load: opening the other format's page
        // must not leave its parsing garbage behind (stage 9). The real trimmer, on a clock past its waits.
        var trimmer = new MemoryTrimmer();
        var clock = DateTime.UtcNow;
        var kept = new List<object?>();
        foreach (var format in new[] { "standard", "wild" })
        {
            kept.Add(Hearthpulse.Core.Constructed.ConstructedMeta.LoadAsync(new HearthpulseData(api), cards, format,
                settings.MetaRank, settings.MetaPeriod, cards.Locale).GetAwaiter().GetResult());
            Private($"{format} loaded");
            trimmer.NoteActivity(clock);
            clock += MemoryTrimmer.MinInterval;
            trimmer.Tick(clock, busy: false);
            Private($"{format} + memory trim");
        }

        GC.KeepAlive(kept);
    }

    /// <summary>Private bytes and the managed heap as they are, without a collection of its own (a trim step measures the trim).</summary>
    private static void Private(string label)
    {
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[meta] {label,-26} private {process.PrivateMemorySize64 / 1048576,4} MB  WS {process.WorkingSet64 / 1048576,4} MB  managed {GC.GetTotalMemory(false) / 1048576,4} MB  GC committed {GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576,4} MB"));
    }

    public static void Poll()
    {
        var install = HearthstoneInstall.FindInstallDirectory(null);
        var latest = install is null ? null : HearthstoneInstall.LatestLogSession(install);
        if (latest is null)
        {
            Console.WriteLine("[poll] no Hearthstone log session found");
            return;
        }

        var tails = new[] { "LoadingScreen.log", "Decks.log", "Power.log", "Arena.log" }.Select(n => new Core.Logs.LogTail(n)).ToArray();
        foreach (var tail in tails)
        {
            tail.Reset(latest, Core.Logs.TailStart.End);
        }

        var type = typeof(Core.Logs.LogTail);
        var pending = type.GetProperty("SeenPendingBytes") ?? type.GetProperty(nameof(Core.Logs.LogTail.PendingBytes))!;
        var size = type.GetProperty("SeenLength") ?? type.GetProperty(nameof(Core.Logs.LogTail.FileLength))!;
        long sink = 0;
        void Pass()
        {
            foreach (var tail in tails)
            {
                sink += tail.ReadNewLines().Count;
            }

            foreach (var tail in tails)
            {
                sink += (long)pending.GetValue(tail)!;
            }

            sink += (long)size.GetValue(tails[2])!;
        }

        Console.WriteLine($"[poll] {Path.GetFileName(latest)}: {string.Join(", ", tails.Select(t => $"{t.FileName} {(File.Exists(Path.Combine(latest, t.FileName)) ? "present" : "missing")}"))}; reads {pending.Name}/{size.Name}");
        CpuTimed("idle watcher pass", 2000, Pass);
        CpuTimed($"IsRunning, game {(Process.GetProcessesByName("Hearthstone").Length > 0 ? "RUNNING" : "closed")}", 50, () => sink += HearthstoneInstall.IsRunning() ? 1 : 0);
        GC.KeepAlive(sink);
    }

    /// <summary>Wall time and this thread's kernel and user time per call.</summary>
    private static void CpuTimed(string what, int calls, Action action)
    {
        action();
        ThreadTimes(out var kernel, out var user);
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < calls; i++)
        {
            action();
        }

        sw.Stop();
        ThreadTimes(out var kernel2, out var user2);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[poll] {what,-32} wall {sw.Elapsed.TotalMilliseconds * 1000 / calls,8:N1} us  kernel {(kernel2 - kernel) / 10.0 / calls,8:N1} us  user {(user2 - user) / 10.0 / calls,8:N1} us per call ({calls} calls)"));
    }

    private static void ThreadTimes(out long kernel, out long user)
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out kernel, out user);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

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
            seed.AddRange(LogBackfill.Parse(path, start, cards));
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

/// <summary>
/// HP_BENCH_PUBLISH=1: what a snapshot's parts cost. After each publish (on its thread, inside the publish's own time),
/// the Constructed view, its counters alone and the Battlegrounds view are built again from the same game and timed.
/// </summary>
internal sealed class PublishParts
{
    private static readonly System.Reflection.FieldInfo ConstructedField =
        typeof(GameSession).GetField("_constructed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private static readonly System.Reflection.FieldInfo BattlegroundsField =
        typeof(GameSession).GetField("_battlegrounds", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    private readonly Dictionary<string, (List<double> Ms, long Bytes)> _parts = new(StringComparer.Ordinal);

    public void Watch(GameSession session, CardDb cards)
    {
        var constructed = (ConstructedTracker)ConstructedField.GetValue(session)!;
        var battlegrounds = (BattlegroundsTracker)BattlegroundsField.GetValue(session)!;
        session.SnapshotChanged += snapshot =>
        {
            if (session.Game is not { } game)
            {
                return;
            }

            if (snapshot.Constructed is not null)
            {
                _last = (game, constructed, cards);
                Time("Constructed view", () => constructed.BuildView(game, cards));
                Time("  counters", () => Hearthpulse.Core.Tracking.Counters.Counters.Build(game, constructed.Counters, cards, constructed.Deck));
            }
            else if (snapshot.Battlegrounds is not null)
            {
                Time("Battlegrounds view", () => battlegrounds.BuildView(game));
            }
        };
    }

    private void Time(string part, Func<object?> build)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        GC.KeepAlive(build());
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        if (!_parts.TryGetValue(part, out var entry))
        {
            entry = ([], 0);
        }

        entry.Ms.Add(ms);
        _parts[part] = (entry.Ms, entry.Bytes + bytes);
    }

    private (Hearthpulse.Core.Power.GameState Game, ConstructedTracker Tracker, CardDb Cards)? _last;

    public void Report(string round)
    {
        // HP_BENCH_PUBLISH_TYPES=1: what the counters allocate, by type (runtime allocation ticks, one per ~100 KB),
        // from 3 000 builds of the last Constructed game seen.
        if (Environment.GetEnvironmentVariable("HP_BENCH_PUBLISH_TYPES") == "1" && _last is { } last)
        {
            using var types = new AllocationTypes();
            for (var i = 0; i < 3000; i++)
            {
                GC.KeepAlive(Hearthpulse.Core.Tracking.Counters.Counters.Build(last.Game, last.Tracker.Counters, last.Cards, last.Tracker.Deck));
            }

            Thread.Sleep(500);
            types.Report($"publish {round} counters");
        }

        foreach (var (part, (ms, bytes)) in _parts)
        {
            Console.WriteLine($"[publish {round}] {part,-20} {ms.Count,5:N0}x {Spread(ms)}; {bytes / Math.Max(1, ms.Count) / 1024,4} KB each");
        }

        _parts.Clear();
    }
}

/// <summary>Allocation ticks by type (all sizes), for <see cref="PublishParts"/>.</summary>
internal sealed class AllocationTypes : System.Diagnostics.Tracing.EventListener
{
    private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
        {
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("GCAllocationTick", StringComparison.Ordinal) != true || e.Payload is null)
        {
            return;
        }

        var type = e.Payload[e.PayloadNames!.IndexOf("TypeName")] as string ?? "?";
        var amount = Convert.ToInt64(e.Payload[e.PayloadNames.IndexOf("AllocationAmount64")], CultureInfo.InvariantCulture);
        lock (_bytes)
        {
            _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
        }
    }

    public void Report(string label)
    {
        lock (_bytes)
        {
            var total = Math.Max(1, _bytes.Values.Sum());
            foreach (var (type, bytes) in _bytes.OrderByDescending(p => p.Value).Take(20))
            {
                Console.WriteLine($"[alloc] {label}: {100.0 * bytes / total,5:N1} %  {type}");
            }
        }
    }
}
