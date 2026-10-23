using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// Constructed stats from the Hearthpulse API: HSGuru meta and decklists parse, archetype names give their class and
/// Russian label, HSGuru archetypes find their HSReplay counterpart (for mulligan numbers) by name or by cards, and old
/// Firestone filter keys keep working. Fixtures: real answers of 2026-10-23, trimmed (Fixtures/Mulligan).
/// </summary>
public sealed class ConstructedApiMetaTests
{
    [Fact]
    public void Hsguru_meta_parses_with_rates_as_fractions_and_current_periods()
    {
        var slice = HearthpulseData.ParseHsguruMeta(Fixture("hsguru-meta-standard-legend-past_week.json"));

        Assert.Equal(20, slice.Items.Count);
        var first = slice.Items[0];
        Assert.Equal("Shredslock", first.Archetype);
        Assert.Equal(0.603, first.WinRate, 3);
        Assert.Equal(0.023, first.Popularity, 3);
        Assert.Equal(9201, first.Games);
        Assert.Equal("black_empire", slice.CurrentPeriod);
        Assert.Equal("patch_36.6.3", slice.CurrentPatchPeriod);
        Assert.Equal(new DateTime(2026, 10, 23, 10, 0, 1, DateTimeKind.Utc), slice.FetchedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Hsguru_archetypes_parse_with_their_lists()
    {
        var archetypes = HearthpulseData.ParseHsguruArchetypes(Fixture("hsguru-archetypes-standard.json"));

        var quest = archetypes.Single(a => a.Name == "Quest Priest");
        Assert.Equal(3, quest.Decks.Count);
        Assert.All(quest.Decks, d => Assert.Equal("Priest", d.Class));
        Assert.All(quest.Decks, d => Assert.NotNull(DeckString.TryDecode(d.Code)));
        Assert.InRange(quest.WinRate, 0.3, 0.8);
        Assert.Equal("DEMONHUNTER", ApiConstructedMeta.ClassOf("C'Thun DH", archetypes.Single(a => a.Name == "C'Thun DH").Decks));
    }

    [Theory]
    [InlineData("Quest DH", "quest demon hunter", "DEMONHUNTER")]
    [InlineData("Harold DK", "herald death knight", "DEATHKNIGHT")]
    [InlineData("Zoolock", "zoo warlock", "WARLOCK")]
    [InlineData("C'Thun Warlock", "cthun warlock", "WARLOCK")]
    [InlineData("Void DemonHunter", "void demon hunter", "DEMONHUNTER")]
    [InlineData("Pure Paladin", "pure paladin", "PALADIN")]
    [InlineData("AYAYA Rogue", "ayaya rogue", "ROGUE")]
    [InlineData("End of Turnadin", "end of turnadin", null)]
    public void Archetype_names_normalise_and_name_their_class(string name, string normalised, string? heroClass)
    {
        Assert.Equal(normalised, ArchetypeNames.Normalize(name));
        Assert.Equal(heroClass, ArchetypeNames.ClassOf(name));
    }

    [Fact]
    public void Hsguru_and_hsreplay_names_match_once_normalised()
    {
        Assert.True(ArchetypeNames.SameArchetype("Harold DK", "Herald Death Knight"));
        Assert.True(ArchetypeNames.SameArchetype("Void DH", "Void Demon Hunter"));
        Assert.False(ArchetypeNames.SameArchetype("Void C'Thun DH", "Void Demon Hunter"));
    }

    [Theory]
    [InlineData("Quest DH", "Квест Охотник на демонов")]
    [InlineData("hand  warlock", "Хендлок")]
    [InlineData("Pirate Quest Hunter", "Квест Охотник (Pirate)")]
    [InlineData("Zoolock", "Zoolock")]
    public void Russian_labels_follow_the_site(string name, string label) =>
        Assert.Equal(label, ArchetypeNames.RussianLabel(name));

    [Fact]
    public void Archetypes_sharing_a_label_are_told_apart()
    {
        var labels = ArchetypeNames.Distinct(new Dictionary<string, string>
        {
            ["Harold DH"] = ArchetypeNames.RussianLabel("Harold DH"),
            ["Herald DH"] = ArchetypeNames.RussianLabel("Herald DH"),
            ["Quest DH"] = ArchetypeNames.RussianLabel("Quest DH"),
        });

        Assert.Equal("Охотник на демонов на возвещении", labels["Harold DH"]);
        Assert.Equal("Охотник на демонов на возвещении (Herald DH)", labels["Herald DH"]);
        Assert.Equal("Квест Охотник на демонов", labels["Quest DH"]);
    }

    [Fact]
    public void Hsguru_archetypes_find_their_hsreplay_counterpart_by_name_or_cards()
    {
        var hsreplay = HsreplayCandidates();
        var hsguru = HearthpulseData.ParseHsguruArchetypes(Fixture("hsguru-archetypes-standard.json"));
        IReadOnlyDictionary<int, int>? TopList(string name) =>
            DeckString.TryDecode(hsguru.Single(a => a.Name == name).Decks.OrderByDescending(d => d.Games).First().Code)?.Cards;

        // Same name and class.
        Assert.Equal(56, ApiConstructedMeta.Match("Quest Priest", "PRIEST", null, hsreplay)?.Archetype.Id);
        Assert.Equal(852, ApiConstructedMeta.Match("Harold DH", "DEMONHUNTER", null, hsreplay)?.Archetype.Id);

        // HSReplay has no C'Thun archetype: HSGuru's C'Thun DH lists are Void Demon Hunter lists.
        Assert.Equal(867, ApiConstructedMeta.Match("C'Thun DH", "DEMONHUNTER", TopList("C'Thun DH"), hsreplay)?.Archetype.Id);

        // A Warlock archetype has no Warlock candidate here: no mulligan rather than another class's numbers.
        Assert.Null(ApiConstructedMeta.Match("Zoolock", "WARLOCK", TopList("Zoolock"), hsreplay));
    }

    [Theory]
    [InlineData("past-7", "past_week")]
    [InlineData("past-3", "past_3_days")]
    [InlineData("last-patch", "patch")]
    [InlineData("past-20", "past_30_days")]
    [InlineData("nonsense", "past_day")]
    public void Old_firestone_period_keys_map_to_hsguru_periods(string saved, string expected) =>
        Assert.Equal(expected, ConstructedMeta.Key(ConstructedMeta.Periods, saved));

    [Theory]
    [InlineData("top-2000-legend", "top_5k")]
    [InlineData("platinum", "all")]
    [InlineData("diamond", "diamond_to_legend")]
    [InlineData("legend", "legend")]
    [InlineData("top_100", "top_100")]
    public void Old_firestone_rank_keys_map_to_hsguru_ranks(string saved, string expected) =>
        Assert.Equal(expected, ConstructedMeta.Key(ConstructedMeta.Ranks, saved));

    [Fact]
    public void Archetype_ids_are_stable_across_loads()
    {
        Assert.Equal(ApiConstructedMeta.StableId("Quest Priest"), ApiConstructedMeta.StableId("quest priest"));
        Assert.NotEqual(ApiConstructedMeta.StableId("Quest Priest"), ApiConstructedMeta.StableId("Thief Priest"));
        Assert.True(ApiConstructedMeta.StableId("Zoolock") >= 0);
    }

    private static List<(MetaArchetype Archetype, IReadOnlyList<GuideDeck> Decks, IReadOnlyList<GuideCard> Cards)> HsreplayCandidates() =>
        HearthpulseData.ParseArchetypes(Fixture("archetypes.json"))
            .Select(a => (a,
                (IReadOnlyList<GuideDeck>)HearthpulseData.ParseArchetypeDecks(Fixture($"{a.Id}-decks.json")),
                (IReadOnlyList<GuideCard>)HearthpulseData.ParseMulligan(Fixture($"{a.Id}-mulligan.json"))))
            .ToList();

    private static JsonElement Fixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mulligan", name)));
        return doc.RootElement.Clone();
    }
}
