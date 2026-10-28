using System.Text.Json;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;

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
    public void A_card_worse_than_its_replacement_is_never_kept()
    {
        // The bug: B is 0.6 points under the deck's average and 0.3 under an average replacement. A half-point margin in
        // favour of keeping made it "Keep" next to a negative number. Now the call follows the edge over the replacement.
        var cards = new List<GuideCard>
        {
            new("A", 1, 1, 1000, 0.5, 1000, 0.500, 2),
            new("B", 2, 2, 1000, 0.5, 1000, 0.494, 2),
            new("C", 3, 3, 1000, 0.5, 1000, 0.480, 3),
            new("D", 4, 4, 1000, 0.5, 1000, 0.491, 4),
            new("E", 5, 1, 1000, 0.5, 1000, 0.5025, 1),
        };
        var guide = new GuideArchetype(1, "Test", "MAGE", cards, [], "test") { BaselineWinRate = 0.5 };
        // Left in the deck: two A (0) and one D (−0.9): an average replacement is −0.3.
        var deck = new Dictionary<string, int>(StringComparer.Ordinal) { ["A"] = 2, ["B"] = 1, ["C"] = 1, ["D"] = 1, ["E"] = 1 };

        var view = Compute(guide, ["B", "C", "E"], deck);
        var b = view.Cards[0];

        Assert.Equal(-0.006, b.Delta!.Value, 6);
        Assert.Equal(-0.003, view.ReplaceDelta, 6);
        Assert.Equal(-0.003, b.Edge!.Value, 6);
        Assert.InRange(-b.Edge.Value, 0, MulliganGuide.CloseCall);
        Assert.Equal(new[] { MulliganCall.Mulligan, MulliganCall.Mulligan, MulliganCall.Keep }, view.Cards.Select(c => c.Call));
    }

    [Fact]
    public void The_call_follows_the_sign_of_the_edge_on_real_hands()
    {
        foreach (var hand in new[] { new[] { "JAIL_732", "JAIL_730", "JAIL_719", "CORE_YOP_001" }, ["CORE_BT_187", "DINO_136", "JAIL_732"], ["JAIL_730", "JAIL_719", "JAIL_732"] })
        {
            var view = DemonHunterHand(hand, "WARLOCK");
            foreach (var card in view.Cards.Where(c => c is { Copy: 1, Edge: not null }))
            {
                Assert.Equal(card.Edge >= 0 ? MulliganCall.Keep : MulliganCall.Mulligan, card.Call);
                Assert.Equal(card.Delta!.Value - view.ReplaceDelta, card.Edge!.Value, 9);
            }
        }
    }

    [Fact]
    public void Coin_and_class_slices_move_a_card_by_its_offered_win_rate()
    {
        // B: +1 point in all games; on the coin 4 points worse than in all games, against Warlock 1 point better.
        var guide = SliceGuide(
            Slice("legend", MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent, 10000, B(0.01, 0.02, -0.01)),
            Slice("legend", "on_coin", MulliganGuide.AnyOpponent, 5000, B(-0.03, -0.01, -0.05)),
            Slice("all", MulliganGuide.AnyCoin, "WARLOCK", 3000, B(0.03, 0.04, 0.0)),
            Slice("all", MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent, 30000, B(0.02, 0.03, 0.0)),
            // Too few games: never used.
            Slice("legend", MulliganGuide.AnyCoin, "WARLOCK", 500, B(0.5, 0.5, 0.5)));
        var hand = new[] { new OfferedCard(1, "A", "A", 1), new OfferedCard(2, "B", "B", 2) };

        var plain = MulliganGuide.Compute(guide, hand, null, null, null, "Test")!;
        var coin = MulliganGuide.Compute(guide, hand, null, null, false, "Test")!;
        var first = MulliganGuide.Compute(guide, hand, null, null, true, "Test")!;
        var warlock = MulliganGuide.Compute(guide, hand, null, "WARLOCK", null, "Test")!;

        Assert.Equal(MulliganCall.Keep, plain.Cards[1].Call);
        Assert.Null(plain.Cards[1].CoinShift);

        // On the coin: −3 − (+1) at Legend.
        Assert.Equal(-0.04, coin.Cards[1].CoinShift!.Value, 6);
        Assert.Equal(MulliganCall.Mulligan, coin.Cards[1].Call);
        Assert.False(coin.GoingFirst);
        Assert.Equal("on_coin", coin.Cards[1].Slice?.Coin);
        Assert.Equal(0.4, coin.Cards[1].KeepRate!.Value, 6);
        Assert.Equal(-0.01, coin.Cards[1].KeptDelta!.Value, 6);
        Assert.Equal((-0.03 * 1000 + 0.01 * 400) / 600, coin.Cards[1].ReplacedDelta!.Value, 6);
        Assert.Equal(-0.05, coin.Cards[1].DrawnDelta!.Value, 6);

        // No going-first slice: nothing moves.
        Assert.Null(first.Cards[1].CoinShift);
        Assert.Null(first.GoingFirst);

        // Against Warlock: the Legend class slice is too thin, all ranks say +3 − (+2).
        Assert.Equal(0.01, warlock.Cards[1].OpponentShift!.Value, 6);
        Assert.Equal("WARLOCK", warlock.OpponentClass);
        Assert.Equal("all", warlock.Cards[1].Slice?.Rank);
    }

    [Fact]
    public void A_coin_shift_of_both_cards_is_no_synergy()
    {
        // The review's case: A and B together win exactly as their own numbers add up (over all games); on the coin both
        // are 3 points worse. Measured against the shifted deltas the pair looked like a +6 synergy and kept both at −0.7.
        var cards = new List<GuideCard>
        {
            new("A", 1, 1, 1000, 0.5, 1000, 0.51, 2),
            new("B", 2, 2, 1000, 0.5, 1000, 0.51, 2),
            new("C", 3, 3, 1000, 0.5, 1000, 0.50, 3),
        };
        SliceCard Row(string id, int dbf, double offered) => new(id, dbf, 1000, offered, 400, offered, 3000, offered);
        var guide = new GuideArchetype(1, "Test", "MAGE", cards, [], "test")
        {
            BaselineWinRate = 0.5,
            Pairs = [new GuidePair("A", "B", 500, 0.52)],
            Slices =
            [
                new MulliganSlice("legend", MulliganGuide.AnyCoin, MulliganGuide.AnyOpponent, 10000, [Row("A", 1, 0), Row("B", 2, 0)]),
                new MulliganSlice("legend", "on_coin", MulliganGuide.AnyOpponent, 5000, [Row("A", 1, -0.03), Row("B", 2, -0.03)]),
            ],
        };
        var hand = new[] { new OfferedCard(1, "A", "A", 1), new OfferedCard(2, "B", "B", 2), new OfferedCard(3, "C", "C", 3) };

        var all = MulliganGuide.Compute(guide, hand, null, null, null, "Test")!;
        var coin = MulliganGuide.Compute(guide, hand, null, null, false, "Test")!;

        Assert.Empty(all.Synergies);
        Assert.Empty(coin.Synergies);
        Assert.Equal(new[] { MulliganCall.Mulligan, MulliganCall.Mulligan }, coin.Cards.Take(2).Select(c => c.Call));
        Assert.All(coin.Cards.Take(2), c => Assert.True(c.Edge < 0));
        AssertCallsFollowTheEdge(all);
        AssertCallsFollowTheEdge(coin);
    }

    [Fact]
    public void A_first_copy_next_to_a_kept_second_shows_what_it_adds()
    {
        // A (1 mana, kept 80%) twice: the second copy is kept by the duplicate rule, so the first adds nothing a
        // replacement (+1 point) would not beat. It reads "Replace" with a negative number, never "Replace +2.0".
        var cards = new List<GuideCard>
        {
            new("A", 1, 1, 1000, 0.8, 1000, 0.53, 1),
            new("C", 3, 3, 1000, 0.5, 1000, 0.50, 3),
            new("X", 4, 4, 1000, 0.5, 1000, 0.51, 4),
        };
        var guide = new GuideArchetype(1, "Test", "MAGE", cards, [], "test") { BaselineWinRate = 0.5 };
        var deck = new Dictionary<string, int>(StringComparer.Ordinal) { ["A"] = 2, ["C"] = 1, ["X"] = 10 };

        var view = Compute(guide, ["A", "A", "C"], deck);

        Assert.Equal(0.01, view.ReplaceDelta, 6);
        Assert.Equal(MulliganCall.Mulligan, view.Cards[0].Call);
        Assert.Equal(-0.01, view.Cards[0].Edge!.Value, 6);
        Assert.Equal(MulliganCall.Keep, view.Cards[1].Call);
        Assert.Null(view.Cards[1].Edge);
        AssertCallsFollowTheEdge(view);
    }

    [Fact]
    public void A_real_synergy_shows_in_the_number_of_the_card_it_keeps()
    {
        // B alone is worse than a replacement, but with A it wins 3 points more than the two add up to: keeping B is
        // worth that, and its number says so.
        var cards = new List<GuideCard>
        {
            new("A", 1, 1, 1000, 0.5, 1000, 0.530, 2),
            new("B", 2, 2, 1000, 0.5, 1000, 0.495, 2),
            new("C", 3, 3, 1000, 0.5, 1000, 0.500, 3),
        };
        var guide = new GuideArchetype(1, "Test", "MAGE", cards, [], "test")
        {
            BaselineWinRate = 0.5,
            Pairs = [new GuidePair("A", "B", 500, 0.5 + 0.03 - 0.005 + 0.03)],
        };

        var view = Compute(guide, ["A", "B", "C"], null);
        var b = view.Cards[1];

        Assert.Equal(0.03, Assert.Single(view.Synergies).Interaction, 6);
        Assert.True(b.Delta - view.ReplaceDelta < 0);
        Assert.Equal(MulliganCall.Keep, b.Call);
        Assert.Equal(b.Delta!.Value - view.ReplaceDelta + 0.03, b.Edge!.Value, 6);
        AssertCallsFollowTheEdge(view);
    }

    [Fact]
    public void The_old_mulligan_strip_placement_is_dropped()
    {
        var settings = new Hearthpulse.Core.AppSettings();
        settings.Panels["mulligan-strip"] = new Hearthpulse.Core.PanelPlacement { X = 0.3, Y = 0.65, Scale = 1.3 };
        settings.Panels["deck"] = new Hearthpulse.Core.PanelPlacement { X = 0.8, Y = 0.1 };

        var loaded = Hearthpulse.Core.AppSettings.FromJson(settings.ToJson());

        Assert.False(loaded.Panels.ContainsKey("mulligan-strip"));
        Assert.Equal(0.8, loaded.Panels["deck"].X, 6);
    }

    private static void AssertCallsFollowTheEdge(MulliganView view)
    {
        foreach (var card in view.Cards.Where(c => c.Edge is not null))
        {
            Assert.Equal(card.Edge >= 0 ? MulliganCall.Keep : MulliganCall.Mulligan, card.Call);
        }
    }

    [Fact]
    public void Hsguru_mulligan_slices_parse_with_impacts_as_fractions()
    {
        var slice = HearthpulseData.ParseHsguruMulligan(Fixture("hsguru-mulligan-zoolock-legend-on_coin-any.json").GetProperty("data"))!;

        Assert.Equal(("legend", "on_coin", "any", 25195), (slice.Rank, slice.Coin, slice.OpponentClass, slice.Games));
        var ritual = slice.Card("BE_085")!;
        Assert.Equal((10821, 7092, 25379), (ritual.Offered, ritual.Kept, ritual.Drawn));
        Assert.Equal(0.077, ritual.OfferedImpact!.Value, 6);
        Assert.Equal(0.079, ritual.KeptImpact!.Value, 6);
        Assert.Equal(-0.013, ritual.DrawnImpact!.Value, 6);
        Assert.Equal(7092.0 / 10821, ritual.KeepRate!.Value, 6);
        Assert.Equal((0.077 * 10821 - 0.079 * 7092) / (10821 - 7092), ritual.ReplacedImpact!.Value, 6);
        Assert.Equal("WARLOCK", HearthpulseData.ParseHsguruMulligan(Fixture("hsguru-mulligan-zoolock-legend-any_player-warlock.json").GetProperty("data"))!.OpponentClass);
    }

    private static GuideArchetype SliceGuide(params MulliganSlice[] slices) =>
        new(1, "Test", "MAGE", [new GuideCard("A", 1, 1, 1000, 0.5, 1000, 0.50, 2), new GuideCard("B", 2, 2, 1000, 0.5, 1000, 0.51, 2)], [], "test")
        {
            BaselineWinRate = 0.5,
            Slices = slices,
        };

    private static MulliganSlice Slice(string rank, string coin, string opponent, int games, SliceCard card) => new(rank, coin, opponent, games, [card]);

    /// <summary>Card B offered 1000 times and kept 400 times in a slice.</summary>
    private static SliceCard B(double offered, double kept, double drawn) => new("B", 2, 1000, offered, 400, kept, 3000, drawn);

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
