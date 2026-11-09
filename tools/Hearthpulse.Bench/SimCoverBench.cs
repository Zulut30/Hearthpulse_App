using System.Globalization;
using System.IO;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Effects;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Stats;
using Hearthpulse.Core.Tracking;
using static Hearthpulse.Bench.Measure;

namespace Hearthpulse.Bench;

/// <summary>
/// How much of a real Battlegrounds fight the combat simulator models (plan 1.5). Two reports:
/// <list type="bullet">
/// <item>Real fights: every Battlegrounds game in the logs (Power*.log under --logs, any depth) is replayed through
/// <see cref="BattlegroundsTracker"/> with the real card database, as the app does. Per fight: whether anything was
/// "not simulated" and what (card, side, golden, granted by which enchantment); then the share of fights without it,
/// the result accuracy the Battlegrounds page shows (<see cref="BattlegroundsStats.SimAccuracy"/>) and the Brier score of
/// the win/tie/loss odds.</item>
/// <item>Popularity: the community minion list the app caches (cache/api/bg-minions-all.json, share of boards with the
/// minion) against the effect registry: which combat-relevant minions have no effect, by popularity, and the chance
/// that a board (and a fight, two boards) holds none of them.</item>
/// </list>
/// </summary>
internal static class SimCoverBench
{
    public static void Run(Options options)
    {
        using var http = Http();
        // English: the relevance fallback reads English card text, and the report names stay readable in any console.
        var cards = CardDb.LoadAsync("enUS", http).GetAwaiter().GetResult();
        RealFights(options, cards);
        Popularity(cards);
    }

    private static void RealFights(Options options, CardDb cards)
    {
        var logs = Directory.Exists(options.Logs)
            ? Directory.EnumerateFiles(options.Logs, "Power*.log", SearchOption.AllDirectories)
                .Where(f => File.ReadLines(f).Any(l => l.Contains("GameType=GT_BATTLEGROUNDS", StringComparison.Ordinal)))
                .ToList()
            : [];
        var records = new List<GameRecord>();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        int fights = 0, clean = 0, scored = 0, noOdds = 0;
        double brier = 0;
        foreach (var log in logs)
        {
            var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
            var tracker = new BattlegroundsTracker { Cards = cards };
            parser.AddListener(tracker);
            var games = new List<IReadOnlyList<CombatRecord>>();
            IReadOnlyList<CombatRecord> combats = [];
            var captures = new List<BattlegroundsTracker.CombatCapture>();
            var count = 0;
            foreach (var line in File.ReadLines(log))
            {
                parser.Feed(line);
                if (tracker.LastCapture is { } capture && (captures.Count == 0 || !ReferenceEquals(captures[^1], capture)))
                {
                    captures.Add(capture);
                }

                // Views now and then, as the overlay builds them: that is when the background simulation's result is taken.
                if (++count % 200 == 0 && line.Contains(" TAG_CHANGE ", StringComparison.Ordinal) && parser.Game is { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
                {
                    parser.Flush();
                    tracker.BuildView(game);
                }

                if (tracker.Combats.Count < combats.Count)
                {
                    games.Add(combats); // the next game started
                    combats = [];
                }

                if (tracker.Combats.Count > 0)
                {
                    combats = tracker.Combats.ToList();
                }
            }

            // The last background simulation lands after the last line.
            Thread.Sleep(3000);
            if (parser.Game is { IsBattlegrounds: true, LocalPlayerId: > 0 } last)
            {
                parser.Flush();
                tracker.BuildView(last);
                combats = tracker.Combats.Count > 0 ? tracker.Combats.ToList() : combats;
            }

            games.Add(combats);
            foreach (var capture in captures)
            {
                foreach (var reason in Reasons(capture, cards))
                {
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                }
            }

            foreach (var game in games.Where(g => g.Count > 0))
            {
                records.Add(Record(game));
                foreach (var c in game)
                {
                    if (c.Odds is not { } o)
                    {
                        noOdds++;
                        continue;
                    }

                    fights++;
                    clean += o.Unsimulated == 0 ? 1 : 0;
                    var (w, t, l) = c.Result switch { "Win" => (1, 0, 0), "Loss" => (0, 0, 1), "Tie" => (0, 1, 0), _ => (-1, -1, -1) };
                    if (w >= 0)
                    {
                        scored++;
                        brier += Math.Pow(o.Win - w, 2) + Math.Pow(o.Tie - t, 2) + Math.Pow(o.Loss - l, 2);
                    }
                }
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[sim] {Path.GetFileName(Path.GetDirectoryName(log))}/{Path.GetFileName(log)}: {games.Count(g => g.Count > 0)} game(s), {games.Sum(g => g.Count)} fights, {captures.Count} captures"));
        }

        var accuracy = BattlegroundsStats.SimAccuracy(records);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[sim] fights without odds (not counted below) {noOdds}; fights with odds {fights}, without \"not simulated\" {clean} = {(fights > 0 ? clean / (double)fights : 0):P0} (Wilson 95 % {Wilson(clean, fights).Low:P0}..{Wilson(clean, fights).High:P0}); result hit {accuracy.ResultRate:P0} of {accuracy.Fights}; damage in range {accuracy.DamageRate:P0}; Brier {(scored > 0 ? brier / scored : 0):N3} over {scored}"));
        foreach (var (code, n) in reasons.GroupBy(r => r.Key[..r.Key.IndexOf(':', StringComparison.Ordinal)]).Select(g => (g.Key, g.Sum(r => r.Value))))
        {
            Console.WriteLine($"[sim]   reason {code}: {n}");
        }

        foreach (var (reason, n) in reasons.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"[sim]   {n,3} × {reason}");
        }
    }

    /// <summary>
    /// Why a captured fight is "not simulated": one line per unsimulated minion and per player-level card, as
    /// <see cref="CombatSimulator.Explain"/> counts them, led by the reason (missing_effect, missing_input, unknown).
    /// </summary>
    private static IEnumerable<string> Reasons(BattlegroundsTracker.CombatCapture capture, CardDb cards)
    {
        foreach (var (side, isOpponent) in new[] { (capture.Ours, false), (capture.Theirs, true) })
        {
            foreach (var item in CombatSimulator.Explain(side, isOpponent))
            {
                var theirs = isOpponent ? " theirs" : string.Empty;
                if (item.Extra)
                {
                    var known = CombatRelevance.Find(item.CardId) is null ? " (not in table)" : string.Empty;
                    yield return $"{item.Code}: extra {item.CardId} {cards.Get(item.CardId)?.Name}{known}{theirs}";
                    continue;
                }

                var m = side.Minions.First(x => x.HasUnsimulatedEffect && x.CardId == item.CardId && x.Golden == item.Golden);
                var row = CombatRelevance.Find(m.CardId);
                var what = row is null ? "not in table" : $"{row.Relevance} {row.Category}";
                var enchantments = m.Enchantments.Count > 0 ? $" [{string.Join(' ', m.Enchantments)}]" : string.Empty;
                yield return $"{item.Code}: {m.CardId}{(m.Golden ? " golden" : string.Empty)} {cards.Get(m.CardId)?.Name} ({what}){enchantments}{theirs}";
            }
        }
    }

    /// <summary>Wilson score interval (95 %) of a share <paramref name="k"/> / <paramref name="n"/>.</summary>
    private static (double Low, double High) Wilson(int k, int n)
    {
        if (n == 0)
        {
            return (0, 0);
        }

        const double z = 1.959964;
        var p = k / (double)n;
        var center = (p + z * z / (2 * n)) / (1 + z * z / n);
        var half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / (1 + z * z / n);
        return (Math.Max(0, center - half), Math.Min(1, center + half));
    }

    private static void Popularity(CardDb cards)
    {
        if (Environment.GetEnvironmentVariable("HEARTHPULSE_DATA") is not { Length: > 0 } data)
        {
            Console.WriteLine("[pop] HEARTHPULSE_DATA is not set: no community minion list to read");
            return;
        }

        var path = Path.Combine(data, "cache", "api", "bg-minions-all.json");
        if (!File.Exists(path))
        {
            Console.WriteLine($"[pop] no {path}");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var missing = new List<(double Share, string Id, string Why)>();
        var boardClean = 1.0;
        var minions = 0;
        foreach (var row in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var id = row.GetProperty("card_id").GetString()!;
            var share = row.GetProperty("normal").TryGetProperty("popularity", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() / 100 : 0;
            minions++;
            var card = cards.Get(id);
            if (!CombatRelevance.IsCombatRelevant(id, card, cards.Locale) || EffectRegistry.Default.Implements(id, golden: false))
            {
                continue;
            }

            var table = CombatRelevance.Find(id);
            missing.Add((share, id, $"{card?.Name} ({(table is null ? "not in table" : $"{table.Relevance} {table.Category}")})"));
            boardClean *= 1 - share;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[pop] {minions} minions, {missing.Count} combat-relevant without an effect; a board holds none of them {boardClean:P0}, a fight {boardClean * boardClean:P0}"));
        foreach (var (share, id, why) in missing.OrderByDescending(m => m.Share))
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[pop]   {share * 100,5:N2} % {id,-14} {why}"));
        }
    }

    private static GameRecord Record(IReadOnlyList<CombatRecord> combats) => new()
    {
        Key = Guid.NewGuid().ToString(),
        Mode = "Battlegrounds",
        CombatResults = JsonSerializer.Serialize(combats.Select(c => new
            { turn = c.Turn, result = c.Result, opponent = c.OpponentHeroCardId, taken = c.DamageTaken, dealt = c.DamageDealt })),
        CombatOdds = GameRecords.CombatOddsJson(combats),
    };
}
