using Hearthpulse.Core.Data;
using Hearthpulse.Core.Stats;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>The deck page's Versions card: undoing a merge, "Not the same deck", the "add a deck" list.</summary>
public sealed partial class DeckPageTests
{
    [Fact]
    public void A_merge_is_undone_back_to_the_families_and_dismissed_pairs_before_it()
    {
        var path = TempDb();
        var store = new HistoryStore(path);
        var alpha = store.MergeDecks(["a1", "a2"], "Alpha");
        var other = store.MergeDecks(["o1", "o2"], "Other");
        store.DismissDeckPair("a1", "b1");
        store.DismissDeckPair("b1", "x1");
        var before = store.DeckFamilies();
        var changes = 0;
        store.DeckFamiliesChanged += () => changes++;

        // A lone version into a family: the merge clears the "different decks" pair between them.
        var snapshot = store.SnapshotDeckFamilies(["a1", "a2", "b1"]);
        Assert.Equal(alpha, store.MergeDecks(["a1", "a2", "b1"], null, "a1", into: alpha));
        Assert.DoesNotContain(HistoryStore.DeckPair("a1", "b1"), store.DismissedDeckPairs());
        store.RestoreDeckFamilies(snapshot);
        AssertSameFamilies(before, store.DeckFamilies());
        Assert.Contains(HistoryStore.DeckPair("a1", "b1"), store.DismissedDeckPairs());
        Assert.Contains(HistoryStore.DeckPair("b1", "x1"), store.DismissedDeckPairs());

        // Two lone versions: the family the merge made goes away again.
        snapshot = store.SnapshotDeckFamilies(["c1", "c2"]);
        store.MergeDecks(["c1", "c2"], null, "c1");
        store.RestoreDeckFamilies(snapshot);
        AssertSameFamilies(before, store.DeckFamilies());

        // Two families: the folded one comes back with its id, name and main version.
        store.SetDeckFamilyMain(other, "o2");
        before = store.DeckFamilies();
        snapshot = store.SnapshotDeckFamilies(["a1", "a2", "o1", "o2"]);
        store.MergeDecks(["a1", "a2", "o1", "o2"], null, "a1", into: alpha);
        Assert.Single(store.DeckFamilies());
        store.RestoreDeckFamilies(snapshot);
        AssertSameFamilies(before, store.DeckFamilies());
        Assert.Equal("o2", store.DeckFamilies().Single(f => f.Id == other).MainKey);

        // The undo survives reopening the history like any other family change.
        store = new HistoryStore(path);
        AssertSameFamilies(before, store.DeckFamilies());
        Assert.Equal(7, changes);
        Drop(path);
    }

    private static void AssertSameFamilies(IReadOnlyList<DeckFamilyRecord> expected, IReadOnlyList<DeckFamilyRecord> actual)
    {
        static string Text(IEnumerable<DeckFamilyRecord> families) => string.Join("; ", families.OrderBy(f => f.Id)
            .Select(f => $"{f.Id}:{f.Name}:{f.MainKey}:{string.Join(",", f.Members.Order(StringComparer.Ordinal))}"));
        Assert.Equal(Text(expected), Text(actual));
    }

    [Fact]
    public void Not_the_same_deck_is_remembered_for_every_version_and_after_reopening()
    {
        // "Рыцарь смерти2" + "Рыцарь смерти3" merged; "Рыцарь смерти4" looks like the same deck as both.
        var v2Code = Code(Deck(1000));
        var v3Code = Code(Deck(1000, 1014));
        var v4Code = Code(Deck(1000, 1013));
        var (v2, v3, v4) = (DeckRecords.CardListKey(v2Code), DeckRecords.CardListKey(v3Code), DeckRecords.CardListKey(v4Code));
        var versions = DeckFamilies.Versions(
        [
            Game("g1", "Рыцарь смерти2", v2Code, "Win", 0),
            Game("g2", "Рыцарь смерти3", v3Code, "Loss", 10),
            Game("g3", "Рыцарь смерти4", v4Code, "Loss", 20),
        ]);
        var path = TempDb();
        var store = new HistoryStore(path);
        store.MergeDecks([v2, v3], null, v2);
        var families = DeckFamilies.Build(versions, store.DeckFamilies());
        var family = families.Single(f => f.IsMerged);
        var lone = families.Single(f => !f.IsMerged);
        Assert.Single(DeckFamilies.Suggest(families, store.DismissedDeckPairs()));

        store.DismissDecks(family.Versions.Select(v => v.Key).ToList(), lone.Versions.Select(v => v.Key).ToList());

        // Kept in the history file: a restart still knows.
        store = new HistoryStore(path);
        var dismissed = store.DismissedDeckPairs();
        Assert.Contains(HistoryStore.DeckPair(v2, v4), dismissed);
        Assert.Contains(HistoryStore.DeckPair(v3, v4), dismissed);
        Assert.Empty(DeckFamilies.Suggest(DeckFamilies.Build(versions, store.DeckFamilies()), dismissed));

        // Splitting the family later does not bring the pair back for the version taken out.
        store.SplitDeck(v3);
        Assert.DoesNotContain(DeckFamilies.Suggest(DeckFamilies.Build(versions, store.DeckFamilies()), store.DismissedDeckPairs()),
            s => s.First.Key == v4 || s.Second.Key == v4);

        // The "add a deck" list still offers it, marked as another deck.
        var rebuilt = DeckFamilies.Build(versions, store.DeckFamilies());
        var candidate = Assert.Single(DeckFamilies.Candidates(rebuilt.Single(f => f.Key == v2), rebuilt, store.DismissedDeckPairs()), c => c.Family.Key == v4);
        Assert.True(candidate.Dismissed);
        Drop(path);
    }

    [Fact]
    public void Candidates_are_the_same_class_and_format_most_alike_first_and_searchable()
    {
        var mine = Code(Deck(1000));
        var close = Code(Deck(1000, 1014));
        var far = Code(Deck(1000, 1010, 1011, 1012, 1013));
        var games = new List<GameRecord>
        {
            Game("g1", "Рыцарь смерти2", mine, "Win", 0),
            Game("g2", "Far list", far, "Win", 50),
            Game("g3", "Рыцарь смерти3", close, "Loss", 10),
            Game("g4", "Old DK", string.Empty, "Win", 20),
            Game("g5", "Older DK", string.Empty, "Loss", 5),
            Game("g6", "Mage list", Code(Deck(1000, 1011)), "Win", 30, playerClass: "MAGE"),
            Game("g7", "Wild DK", Code(Deck(1000, 1012)), "Win", 40, format: "FT_WILD"),
        };
        var families = DeckFamilies.Build(DeckFamilies.Versions(games), []);
        var family = families.Single(f => f.Name == "Рыцарь смерти2");
        var none = new HashSet<(string, string)>();

        // Most cards in common first; decks without a card list last, the most recently played of them first.
        var candidates = DeckFamilies.Candidates(family, families, none);
        Assert.Equal(["Рыцарь смерти3", "Far list", "Old DK", "Older DK"], candidates.Select(c => c.Family.Name));
        Assert.Equal(28.0 / 30, candidates[0].Overlap, 3);
        Assert.Equal(22.0 / 30, candidates[1].Overlap, 3);
        Assert.Equal(0, candidates[2].Overlap);
        Assert.All(candidates, c => Assert.False(c.Dismissed));

        // Search: any part of the name, case ignored, spaces trimmed; nothing found is an empty list.
        Assert.Equal(["Old DK", "Older DK"], DeckFamilies.Candidates(family, families, none, " old ").Select(c => c.Family.Name));
        Assert.Equal(["Рыцарь смерти3"], DeckFamilies.Candidates(family, families, none, "РЫЦАРЬ").Select(c => c.Family.Name));
        Assert.Empty(DeckFamilies.Candidates(family, families, none, "zzzz"));

        // The texts the list and the suggestion show.
        Assert.Equal("no card list", DeckVersionsText.SharedShort(0));
        Assert.Equal("2 games · Today 16:09 – 16:33", DeckVersionsText.VersionDetail(2, "Today 16:09", "Today 16:33"));
        Assert.Equal("1 game · Today 16:09", DeckVersionsText.VersionDetail(1, "Today 16:09", "Today 16:09"));
        Assert.Equal("+2 / −2 cards", DeckVersionsText.CardChanges(2, 2));
    }
}
