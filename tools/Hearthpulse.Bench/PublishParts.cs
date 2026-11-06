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
                _lastBattlegrounds = (game, battlegrounds);
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

    private (Hearthpulse.Core.Power.GameState Game, BattlegroundsTracker Tracker)? _lastBattlegrounds;

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

        // The same for the Battlegrounds view of the last Battlegrounds game seen.
        if (Environment.GetEnvironmentVariable("HP_BENCH_PUBLISH_TYPES") == "1" && _lastBattlegrounds is { } bg)
        {
            using var types = new AllocationTypes();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 3000; i++)
            {
                GC.KeepAlive(bg.Tracker.BuildView(bg.Game));
            }

            Console.WriteLine($"[publish {round}] Battlegrounds view of the last game, 3 000 builds: {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 3000 / 1024} KB each, {bg.Game.Entities.Count} entities");
            Thread.Sleep(500);
            types.Report($"publish {round} battlegrounds view");
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
            foreach (var (type, bytes) in _bytes.OrderByDescending(p => p.Value).Take(30))
            {
                Console.WriteLine($"[alloc] {label}: {100.0 * bytes / total,5:N1} %  {type}");
            }
        }
    }
}
