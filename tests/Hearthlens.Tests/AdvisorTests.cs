using System.Net;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Hearthlens.Core.Cards;
using Hearthlens.Core.Power;
using Hearthlens.Core.Tracking;
using Xunit.Abstractions;

namespace Hearthlens.Tests;

/// <summary>
/// Replays a kept Battlegrounds log through the advisor with live community data. No-op offline
/// or on machines without logs.
/// </summary>
public sealed class AdvisorTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Advisor_ranks_heroes_comps_and_shop()
    {
        var cardsPath = Path.Combine(Path.GetTempPath(), "hl", "cards.json");
        var log = Directory.Exists(@"D:\Hearthstone\Logs")
            ? Directory.EnumerateFiles(@"D:\Hearthstone\Logs", "Power*.log", SearchOption.AllDirectories)
                .FirstOrDefault(f => File.ReadLines(f).Take(20_000).Any(x => x.Contains("GameType=GT_BATTLEGROUNDS")))
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

        var advisor = new BattlegroundsAdvisor { Meta = meta, CompMode = CompMode.Auto };
        var parser = new PowerParser(DateOnly.FromDateTime(File.GetLastWriteTime(log)));
        var tracker = new BattlegroundsTracker { Cards = cards };
        parser.AddListener(tracker);

        BattlegroundsView? pick = null;
        BattlegroundsView? shop = null;
        var lines = 0;
        foreach (var line in File.ReadLines(log))
        {
            parser.Feed(line);
            if (++lines % 200 != 0 || parser.Game is not { IsBattlegrounds: true, LocalPlayerId: > 0 } game)
            {
                continue;
            }

            parser.Flush();
            var view = tracker.BuildView(game);
            if (view.IsHeroSelection && view.HeroOptions.Count > 0)
            {
                pick ??= view;
            }
            else if (view is { InCombat: false, Turn: >= 4, Shop.Count: >= 3 } && shop is null)
            {
                shop = view;
            }
        }

        Assert.NotNull(pick);
        var lobby = new[] { "BEAST", "DEMON", "MECHANICAL", "UNDEAD", "NAGA" };
        var enriched = advisor.Enrich(pick! with { Races = lobby }, 7000, cards);
        output.WriteLine(enriched.MetaSource);
        foreach (var h in enriched.HeroOptions)
        {
            output.WriteLine($"{(h.Recommended ? "*" : " ")} {h.Name,-24} {h.Meta?.Tier} avg={h.Meta?.AveragePosition:0.00} adj={h.Meta?.AdjustedPosition:0.00} comps=[{string.Join(", ", h.TopComps ?? [])}]");
        }

        Assert.Single(enriched.HeroOptions, h => h.Recommended);
        Assert.All(enriched.HeroOptions, h => Assert.NotNull(h.Meta));

        if (shop is not null)
        {
            var advice = advisor.Enrich(shop, 7000, cards);
            output.WriteLine($"Turn {shop.Turn} tier {shop.TechLevel} hero {cards.NameOf(shop.HeroCardId ?? "")} races [{string.Join(",", shop.Races)}]");
            foreach (var c in advice.Comps)
            {
                output.WriteLine($"  {(c.IsTarget ? ">" : " ")} {c.Name,-28} {c.PowerLevel} avg={c.AveragePlacement:0.00} hero={c.HeroPlacement:0.00} core={c.OwnedCore} cards={c.Cards.Count}");
            }

            foreach (var s in advice.ShopAdvice)
            {
                output.WriteLine($"  {(s.Best ? "BUY" : "   ")} {s.Name,-26} T{s.TechLevel} {s.Score,3} {s.Reason}");
            }

            Assert.NotEmpty(advice.Comps);
            Assert.Single(advice.Comps, c => c.IsTarget);
            Assert.Contains(advice.ShopAdvice, s => s.Best);
        }
    }
}
