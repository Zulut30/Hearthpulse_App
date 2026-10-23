using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The mulligan guide maths on real API answers (Fixtures/Mulligan: api.kolodahearthstone.com, Standard Legend EU,
/// fetched 2026-10-23 and trimmed to the fields the app reads): archetype matching, baseline, duplicates, curve and
/// the impact of a set of keeps. No network.
/// </summary>
public sealed class MulliganGuideTests
{
    /// <summary>The player's Demon Hunter deck from a real Decks.log (2026-10-23).</summary>
    private const string DemonHunterDeck = "AAECAfX+BwSongbDgweD2gfziQgN4fgF0J4G3v8GtpcHlb8HxN0Hxt0Hx90HteEHt+EHqvoHvoEI+4kIAAA=";

    private const int VoidDemonHunter = 867;
    private const int QuestPriest = 56;

    [Fact]
    public void Api_answers_parse_with_rates_as_fractions()
    {
        var voidSoul = Cards(VoidDemonHunter).Single(c => c.CardId == "JAIL_732");
        Assert.Equal(126662, voidSoul.DbfId);
        Assert.Equal(1, voidSoul.Cost);
        Assert.Equal(6382, voidSoul.Offered);
        Assert.Equal(0.9462, voidSoul.KeepRate, 4);
        Assert.Equal(0.5375, voidSoul.OpeningHandWinRate!.Value, 4);
        Assert.Equal(4.37, voidSoul.AvgTurnPlayed!.Value, 2);

        var decks = Decks(QuestPriest);
        Assert.NotEmpty(decks);
        Assert.All(decks, d => Assert.Equal(30, d.Cards.Values.Sum()));

        var matchups = HearthpulseData.ParseMatchups(Fixture($"{QuestPriest}-matchups.json"));
        Assert.NotEmpty(matchups);
        Assert.All(matchups, m => Assert.InRange(m.WinRate, 0, 1));
    }

    [Fact]
    public void A_real_deck_matches_its_archetype()
    {
        var deck = DeckString.TryDecode(DemonHunterDeck)!;

        var match = MulliganGuide.MatchArchetype(deck.Cards, Candidates("DEMONHUNTER"));

        Assert.Equal("Void Demon Hunter", match?.Archetype.Name);
        Assert.True(match!.Value.Score >= 1);
    }

    [Fact]
    public void An_archetype_without_lists_still_matches_by_its_mulligan_rows()
    {
        var hsreplayList = Decks(QuestPriest)[0].Cards;
        var withLists = Candidates("PRIEST").ToList();
        var withoutLists = withLists.Select(c => (c.Archetype, (IReadOnlyList<GuideDeck>)[], c.Cards)).ToList();

        Assert.Equal(QuestPriest, MulliganGuide.MatchArchetype(hsreplayList, withLists)?.Archetype.Id);
        Assert.Equal(2, MulliganGuide.MatchArchetype(hsreplayList, withLists)!.Value.Score, 3);
        Assert.Equal(QuestPriest, MulliganGuide.MatchArchetype(hsreplayList, withoutLists)?.Archetype.Id);
        Assert.InRange(MulliganGuide.Presence(hsreplayList, Cards(QuestPriest)), MulliganGuide.MinPresence, 1);
    }

    [Fact]
    public void A_deck_of_another_class_matches_nothing()
    {
        var priestList = Decks(QuestPriest)[0].Cards;

        Assert.Null(MulliganGuide.MatchArchetype(priestList, Candidates("DEMONHUNTER")));
    }

    [Fact]
    public void The_baseline_is_the_win_rate_of_the_same_sample()
    {
        // The quest starts in hand every game, so its opening-hand win rate is the deck's win rate in this sample.
        var cards = Cards(QuestPriest);
        var quest = cards.Single(c => c.CardId == "TLC_817");

        Assert.Equal(quest.OpeningHandWinRate!.Value, MulliganGuide.Baseline(cards)!.Value, 2);
    }

    [Fact]
    public void A_real_opening_hand_keeps_the_strong_cards()
    {
        var view = DemonHunterHand(["JAIL_732", "JAIL_730", "JAIL_719", "CORE_YOP_001"], "WARLOCK");
        var guide = Guide(VoidDemonHunter);

        Assert.Equal(new[] { MulliganCall.Keep, MulliganCall.Keep, MulliganCall.Keep, MulliganCall.Mulligan }, view.Cards.Select(c => c.Call));
        Assert.Equal(new[] { 1, 2, 3 }, view.Recommended);
        Assert.Equal("WARLOCK", view.OpponentClass);
        Assert.Equal(MulliganGuide.Baseline(guide.Cards)!.Value + view.ClassShift!.Value, view.BaseWinRate, 6);
        Assert.False(view.HasPairData);
        Assert.Empty(view.Synergies);

        var recommended = MulliganGuide.Expected(view, view.Recommended.ToHashSet());
        Assert.True(recommended > MulliganGuide.Expected(view, Set(1, 2, 3, 4)));
        Assert.True(recommended > MulliganGuide.Expected(view, Set()));
    }

    [Fact]
    public void Keeping_a_weak_card_costs_win_rate()
    {
        var view = DemonHunterHand(["JAIL_732", "JAIL_730", "JAIL_719", "CORE_YOP_001"], null);
        var illidariStudies = view.Cards[3];

        var withIt = MulliganGuide.Evaluate(view, [1, 2, 3, 4]);
        var without = MulliganGuide.Evaluate(view, [1, 2, 3]);

        // One card changes: its delta replaces the replacement's, shrunk like the whole hand.
        Assert.Equal((illidariStudies.Delta!.Value - view.ReplaceDelta) / 2, withIt.Expected - without.Expected, 6);
        Assert.True(withIt.Expected < without.Expected);
        Assert.Equal(without.Expected, without.Recommended, 6);
        Assert.Equal(3, without.Kept);
    }

    [Fact]
    public void A_full_mulligan_is_worth_the_replacements()
    {
        var view = DemonHunterHand(["JAIL_732", "JAIL_730", "CORE_YOP_001"], null);

        var impact = MulliganGuide.Evaluate(view, []);

        Assert.Equal(view.BaseWinRate + 3 * view.ReplaceDelta / Math.Sqrt(3), impact.FullMulligan, 6);
        Assert.Equal(impact.FullMulligan, impact.Expected, 6);
        Assert.Empty(impact.Warnings);
        Assert.InRange(view.ReplaceDelta, -0.05, 0.05);
    }

    [Fact]
    public void A_second_copy_counts_once_and_goes_back_unless_it_is_an_early_play()
    {
        // Stardust Scythe: 4 mana, usually played on turn 6. Void Soul: 1 mana, kept 95% of the time.
        var scythes = DemonHunterHand(["JAIL_730", "JAIL_730", "JAIL_732"], null);
        Assert.Equal(new[] { 1, 2 }, scythes.Cards.Select(c => c.Copy).Take(2));
        Assert.Equal(MulliganCall.Keep, scythes.Cards[0].Call);
        Assert.Equal(MulliganCall.Mulligan, scythes.Cards[1].Call);

        // Keeping the second copy adds nothing and loses the replacement.
        var both = MulliganGuide.Evaluate(scythes, [1, 2, 3]);
        var one = MulliganGuide.Evaluate(scythes, [1, 3]);
        Assert.Equal(-scythes.ReplaceDelta / Math.Sqrt(3), both.Expected - one.Expected, 6);
        Assert.Contains(both.Warnings, w => w.Contains("Both copies of", StringComparison.Ordinal));
        Assert.DoesNotContain(one.Warnings, w => w.Contains("Both copies of", StringComparison.Ordinal));

        var souls = DemonHunterHand(["JAIL_732", "JAIL_732", "CORE_YOP_001"], null);
        Assert.Equal(MulliganCall.Keep, souls.Cards[1].Call);
        Assert.DoesNotContain(MulliganGuide.Evaluate(souls, [1, 2]).Warnings, w => w.Contains("Both copies of", StringComparison.Ordinal));
    }

    [Fact]
    public void Curve_warnings_follow_the_kept_cards()
    {
        // Kayn Sunfury and Horn of Feasting cost 4 and are usually played on turn 6+; Void Soul costs 1.
        var view = DemonHunterHand(["CORE_BT_187", "DINO_136", "JAIL_732"], null);
        Assert.True(MulliganGuide.IsLate(view.Cards[0]));
        Assert.True(MulliganGuide.IsEarly(view.Cards[2]));

        var slow = MulliganGuide.Evaluate(view, [1, 2]);
        Assert.Contains(slow.Warnings, w => w.StartsWith("No early play kept", StringComparison.Ordinal));
        Assert.Contains(slow.Warnings, w => w.StartsWith("Top-heavy: 2", StringComparison.Ordinal));

        var curved = MulliganGuide.Evaluate(view, [1, 3]);
        Assert.Empty(curved.Warnings);
    }

    [Fact]
    public void Synergies_appear_only_with_pair_data()
    {
        var hand = new[] { "JAIL_732", "JAIL_719", "CORE_YOP_001" };
        var plain = Compute(Guide(VoidDemonHunter), hand, null);

        // Synthetic pair (no endpoint publishes pairs yet): together 3 points better than the two deltas add up to.
        var guide = Guide(VoidDemonHunter);
        var baseline = MulliganGuide.Baseline(guide.Cards)!.Value;
        var together = baseline + plain.Cards[0].Delta!.Value + plain.Cards[1].Delta!.Value + 0.03;
        var paired = Compute(guide with { Pairs = [new GuidePair("JAIL_719", "JAIL_732", 500, together)] }, hand, null);

        Assert.Empty(plain.Synergies);
        var synergy = Assert.Single(paired.Synergies);
        Assert.True(paired.HasPairData);
        Assert.Equal(0.03, synergy.Interaction, 6);
        Assert.Equal((1, 2), (synergy.EntityA, synergy.EntityB));
        Assert.Single(MulliganGuide.Evaluate(paired, [1, 2]).Synergies);
        Assert.Empty(MulliganGuide.Evaluate(paired, [1]).Synergies);
        Assert.Equal(0.03 / Math.Sqrt(3), MulliganGuide.Expected(paired, Set(1, 2)) - MulliganGuide.Expected(plain, Set(1, 2)), 6);

        // Too few games together: noise, not a synergy.
        var thin = Compute(guide with { Pairs = [new GuidePair("JAIL_719", "JAIL_732", 50, together)] }, hand, null);
        Assert.Empty(thin.Synergies);
    }

    [Fact]
    public void Near_ties_stay_kept()
    {
        // Synthetic archetype: one card is half a point worse than an average replacement, the other two points worse.
        var cards = new List<GuideCard>
        {
            new("A", 1, 1, 1000, 0.5, 1000, 0.500, 2),
            new("B", 2, 2, 1000, 0.5, 1000, 0.497, 2),
            new("C", 3, 3, 1000, 0.5, 1000, 0.480, 3),
            new("D", 4, 4, 1000, 0.5, 1000, 0.501, 4),
        };
        var guide = new GuideArchetype(1, "Test", "MAGE", cards, [], "test");
        var deck = new Dictionary<string, int>(StringComparer.Ordinal) { ["A"] = 2, ["B"] = 2, ["C"] = 2, ["D"] = 20 };

        var view = Compute(guide, ["B", "C"], deck);

        Assert.InRange(view.Cards[0].Delta!.Value - view.ReplaceDelta, -MulliganGuide.KeepMargin, 0);
        Assert.Equal(MulliganCall.Keep, view.Cards[0].Call);
        Assert.Equal(MulliganCall.Mulligan, view.Cards[1].Call);
    }

    private static MulliganView DemonHunterHand(IReadOnlyList<string> hand, string? opponentClass)
    {
        var guide = Guide(VoidDemonHunter);
        var ids = guide.Cards.GroupBy(c => c.DbfId).ToDictionary(g => g.Key, g => g.First().CardId);
        var deck = DeckString.TryDecode(DemonHunterDeck)!.Cards
            .Where(c => ids.ContainsKey(c.Key))
            .ToDictionary(c => ids[c.Key], c => c.Value, StringComparer.Ordinal);
        return Compute(guide, hand, deck, opponentClass);
    }

    private static MulliganView Compute(GuideArchetype guide, IReadOnlyList<string> hand, IReadOnlyDictionary<string, int>? deck, string? opponentClass = null)
    {
        var costs = guide.Cards.GroupBy(c => c.CardId).ToDictionary(g => g.Key, g => g.First().Cost);
        var offered = hand.Select((id, i) => new OfferedCard(i + 1, id, id, costs.GetValueOrDefault(id))).ToList();
        return MulliganGuide.Compute(guide, offered, deck, opponentClass, null, guide.Name)!;
    }

    private static GuideArchetype Guide(int id) =>
        new(id, "Archetype " + id, "TEST", Cards(id), HearthpulseData.ParseMatchups(Fixture($"{id}-matchups.json")), "test");

    private static IEnumerable<(MetaArchetype Archetype, IReadOnlyList<GuideDeck> Decks, IReadOnlyList<GuideCard> Cards)> Candidates(string playerClass) =>
        HearthpulseData.ParseArchetypes(Fixture("archetypes.json"))
            .Where(a => a.Class == playerClass)
            .Select(a => (a, (IReadOnlyList<GuideDeck>)Decks(a.Id), (IReadOnlyList<GuideCard>)Cards(a.Id)));

    private static List<GuideCard> Cards(int id) => HearthpulseData.ParseMulligan(Fixture($"{id}-mulligan.json"));

    private static List<GuideDeck> Decks(int id) => HearthpulseData.ParseArchetypeDecks(Fixture($"{id}-decks.json"));

    private static HashSet<int> Set(params int[] ids) => [.. ids];

    private static JsonElement Fixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mulligan", name)));
        return doc.RootElement.Clone();
    }
}
