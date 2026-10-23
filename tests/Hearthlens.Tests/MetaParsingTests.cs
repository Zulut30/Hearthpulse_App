using System.Text;
using System.Text.Json;
using Hearthlens.Core;
using Hearthlens.Core.Battlegrounds;
using Xunit;

namespace Hearthlens.Tests;

/// <summary>Each meta section must survive missing or malformed fields without dropping the whole file.</summary>
public sealed class MetaParsingTests
{
    [Fact]
    public void Cards_section_tolerates_missing_and_malformed_entries()
    {
        using var doc = JsonDocument.Parse("""
            { "cardStats": [
                { "cardId": "GOOD", "dataPoints": 500, "averagePlacement": 4.1, "totalPlayed": 300,
                  "turnStats": [ { "turn": 5, "totalPlayed": 120, "averagePlacement": 4.0 },
                                 { "turn": null, "totalPlayed": 197, "averagePlacement": 2.8 } ] },
                { "dataPoints": 10 },
                "not an object",
                { "cardId": "NO_AVERAGE" }
            ] }
            """);
        var cards = BattlegroundsMeta.ParseCards(doc.RootElement);

        var card = Assert.Contains("GOOD", cards);
        Assert.Equal(4.1, card.AveragePlacement, 3);
        Assert.Equal(120, card.Turns[5].Played);
        // A null turn is a real shape in the Firestone feed and must be skipped, not kill the whole section.
        Assert.Single(card.Turns);
        Assert.DoesNotContain("NO_AVERAGE", cards.Keys);

        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(BattlegroundsMeta.ParseCards(empty.RootElement));
    }

    [Fact]
    public void Heroes_section_skips_malformed_entries()
    {
        using var doc = JsonDocument.Parse("""
            { "heroStats": [
                { "heroCardId": "HERO_GOOD", "dataPoints": 900, "averagePosition": 3.6, "totalOffered": 200, "totalPicked": 90 },
                { "heroCardId": "", "dataPoints": 100 },
                { "dataPoints": 100 },
                { "heroCardId": "HERO_THIN" }
            ] }
            """);
        var heroes = BattlegroundsMeta.ParseHeroes(doc.RootElement);

        var hero = Assert.Contains("HERO_GOOD", heroes);
        Assert.Equal(0.45, hero.PickRate, 3);
        // Entries with an id but no stats are kept (the id is what the game matches on); entries without one are not.
        Assert.Equal(2, heroes.Count);
        Assert.Contains("HERO_THIN", heroes.Keys);
    }

    [Fact]
    public void Trinkets_section_skips_malformed_entries()
    {
        using var doc = JsonDocument.Parse("""
            { "trinketStats": [
                { "trinketCardId": "TR_GOOD", "dataPoints": 400, "averagePlacement": 4.2, "pickRate": 0.4,
                  "averagePlacementAtMmr": [ { "mmr": 25, "placement": 4.0, "dataPoints": 120 }, { "mmr": 100 } ] },
                { "dataPoints": 5 },
                { "trinketCardId": "" }
            ] }
            """);
        var trinkets = BattlegroundsMeta.ParseTrinkets(doc.RootElement);

        var trinket = Assert.Contains("TR_GOOD", trinkets);
        Assert.Equal(4.2, trinket.AveragePlacement, 3);
        Assert.Equal((4.0, 120), trinket.PlacementAtMmr[25]);
        Assert.Single(trinkets);
    }

    [Fact]
    public void Comps_section_skips_malformed_entries_and_infers_tribes()
    {
        using var doc = JsonDocument.Parse("""
            { "compStats": [
                { "archetype": "beast_test", "dataPoints": 900, "averagePlacement": 4.2,
                  "averagePlacementAtMmr": [ { "mmr": 25, "placement": 4.1, "dataPoints": 250 } ],
                  "heroStats": [ { "heroCardId": "HERO_GOOD", "dataPoints": 50, "averagePlacement": 3.9 } ] },
                { "archetype": "thin", "dataPoints": 10 },
                "junk"
            ] }
            """);
        var comps = BattlegroundsMeta.ParseComps(doc.RootElement, null).ToList();

        var comp = Assert.Single(comps);
        Assert.Equal("beast_test", comp.Id);
        Assert.Equal("Beast Test", comp.Name);
        Assert.Equal(["BEAST"], comp.Tribes);
        Assert.Equal(4.1, comp.PlacementAtMmr[25], 3);
        Assert.Equal(3.9, comp.HeroPlacement["HERO_GOOD"].Placement, 3);
    }

    /// <summary>
    /// The comp file is tens of megabytes and is read without building a document. The streamed result must be
    /// exactly the result the document parser produces.
    /// </summary>
    [Fact]
    public void Streamed_comp_parser_matches_the_document_parser()
    {
        var file = Path.Combine(AppPaths.Cache, "meta", "comp-stats.json");
        if (!File.Exists(file))
        {
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var fromDocument = BattlegroundsMeta.ParseComps(doc.RootElement, null).ToList();
        var fromStream = BattlegroundsMeta.ParseCompsFile(file, null);

        Assert.NotEmpty(fromDocument);
        Assert.Equal(fromDocument.Count, fromStream.Count);
        for (var i = 0; i < fromDocument.Count; i++)
        {
            Assert.Equal(fromDocument[i].Id, fromStream[i].Id);
            Assert.Equal(fromDocument[i].AveragePlacement, fromStream[i].AveragePlacement, 6);
            Assert.Equal(fromDocument[i].PlacementAtMmr.Count, fromStream[i].PlacementAtMmr.Count);
            Assert.Equal(fromDocument[i].HeroPlacement.Count, fromStream[i].HeroPlacement.Count);
            foreach (var (mmr, placement) in fromDocument[i].PlacementAtMmr)
            {
                Assert.Equal(placement, fromStream[i].PlacementAtMmr[mmr], 6);
            }
        }
    }
}
