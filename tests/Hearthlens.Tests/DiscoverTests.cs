using System.Net;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthlens.Tests;

/// <summary>
/// Replays a kept Battlegrounds log that used the Dark Gift button and checks the Discover advice.
/// No-op offline or on machines without such a log.
/// </summary>
public sealed class DiscoverTests(ITestOutputHelper output)
{
    private const string DarkGiftDiscover = "BG36_MidGameEffect_010";

    [Fact]
    public async Task Dark_gift_discover_is_tracked_and_scored()
    {
        var cardsPath = Path.Combine(Path.GetTempPath(), "hl", "cards.json");
        var log = Directory.Exists(@"D:\Hearthstone\Logs")
            ? Directory.EnumerateFiles(@"D:\Hearthstone\Logs", "Power*.log", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault(f => ReadShared(f).Any(x => x.Contains(DarkGiftDiscover, StringComparison.Ordinal)))
            : null;
        if (!File.Exists(cardsPath) || log is null)
        {
            return;
        }

        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        var meta = await BattlegroundsMeta.LoadAsync(http);
        if (meta is null)
        {
            return;
        }

        CardDb cards;
        using (var stream = File.OpenRead(cardsPath))
        {
            cards = CardDb.Parse("itIT", stream);
        }

        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
        var tracker = new BattlegroundsTracker { Cards = cards };
        parser.AddListener(tracker);

        BattlegroundsView? discover = null;
        BattlegroundsView? gift = null;
        var lines = 0;
        foreach (var line in ReadShared(log))
        {
            parser.Feed(line);
            if (++lines % 50 != 0 || parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                continue;
            }

            parser.Flush();
            var view = tracker.BuildView(game);
            if (view.DiscoverOptions.Count > 1)
            {
                discover ??= view;
                if (view.DiscoverOptions.Any(d => d.DarkGift is not null))
                {
                    gift = view;
                    break;
                }
            }
        }

        Assert.NotNull(discover);
        var target = gift ?? discover!;
        var advice = new BattlegroundsAdvisor { Meta = meta, CompMode = CompMode.Auto }.Enrich(target, 7000, cards);
        foreach (var pick in advice.DiscoverAdvice.OrderBy(p => p.Slot))
        {
            var option = target.DiscoverOptions[pick.Slot];
            output.WriteLine($"{(pick.Best ? "*" : " ")} [{pick.Slot}] {option.Name,-28} T{option.TechLevel} gift={option.DarkGift} score={pick.Score} {pick.Reason}");
        }

        Assert.Equal(target.DiscoverOptions.Count, advice.DiscoverAdvice.Count);
        Assert.Equal(Enumerable.Range(0, target.DiscoverOptions.Count), advice.DiscoverAdvice.Select(p => p.Slot).Order());
        Assert.Contains(advice.DiscoverAdvice, p => p.Best);
        if (gift is not null)
        {
            Assert.All(gift.DiscoverOptions, d => Assert.False(string.IsNullOrEmpty(d.DarkGift)));
        }
    }

    /// <summary>
    /// Every "pick one card" choice in recent games (hero powers, spells, triple rewards, Dark Gift, trinkets)
    /// must be open in the view right before the player picks, and ranked with community data where it exists.
    /// </summary>
    [Fact]
    public async Task Every_pick_one_choice_is_tracked_and_ranked()
    {
        var cardsPath = Path.Combine(Path.GetTempPath(), "hl", "cards.json");
        if (!File.Exists(cardsPath) || !Directory.Exists(@"D:\Hearthstone\Logs"))
        {
            return;
        }

        using var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All });
        var meta = await BattlegroundsMeta.LoadAsync(http);
        if (meta is null)
        {
            return;
        }

        CardDb cards;
        using (var stream = File.OpenRead(cardsPath))
        {
            cards = CardDb.Parse("itIT", stream);
        }

        var advisor = new BattlegroundsAdvisor { Meta = meta, CompMode = CompMode.Auto };
        int picks = 0, trinkets = 0, discovers = 0, ranked = 0, options = 0, withData = 0;
        var missed = new List<string>();
        var kinds = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var log in Directory.EnumerateFiles(@"D:\Hearthstone\Logs", "Power*.log", SearchOption.AllDirectories)
                     .OrderByDescending(File.GetLastWriteTime).Take(4))
        {
            var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
            var tracker = new BattlegroundsTracker { Cards = cards };
            parser.AddListener(tracker);
            var general = false;
            foreach (var line in ReadShared(log))
            {
                if (line.Contains("GameState.DebugPrintEntityChoices() - id=", StringComparison.Ordinal))
                {
                    general = line.Contains("ChoiceType=GENERAL", StringComparison.Ordinal);
                }
                else if (general && line.Contains("GameState.DebugPrintEntitiesChosen() - id=", StringComparison.Ordinal)
                         && parser.Game is { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
                {
                    parser.Flush();
                    var view = tracker.BuildView(game);
                    picks++;
                    if (view.TrinketOptions.Count > 0)
                    {
                        trinkets++;
                    }
                    else if (view.DiscoverOptions.Count > 0)
                    {
                        discovers++;
                        var advice = advisor.Enrich(view, 7000, cards).DiscoverAdvice;
                        options += advice.Count;
                        withData += advice.Count(p => p.Placement is not null);
                        if (advice.Any(p => p.Best)) ranked++;
                        var kind = view.DiscoverSource ?? "?";
                        kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                        if (kinds[kind] == 1)
                        {
                            var best = advice.FirstOrDefault(p => p.Best);
                            output.WriteLine($"{kind}: {string.Join(" | ", advice.OrderBy(p => p.Slot).Select(p => $"{(p.Best ? "*" : "")}{p.Name} {p.Placement:0.00}"))}");
                        }
                    }
                    else
                    {
                        missed.Add($"T{view.Turn} {line[..Math.Min(line.Length, 40)]}");
                    }

                    general = false;
                }

                parser.Feed(line);
            }
        }

        output.WriteLine($"picks={picks} trinkets={trinkets} discovers={discovers} ranked={ranked} options={options} withData={withData} missed={missed.Count}");
        foreach (var (kind, count) in kinds) output.WriteLine($"  {count,3} {kind}");
        foreach (var miss in missed.Take(10)) output.WriteLine($"  missed {miss}");
        Assert.True(picks == 0 || missed.Count <= picks / 20, $"{missed.Count} of {picks} choices were not tracked");
        Assert.True(discovers == 0 || ranked >= discovers * 0.8, $"only {ranked} of {discovers} discovers got a pick");
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
