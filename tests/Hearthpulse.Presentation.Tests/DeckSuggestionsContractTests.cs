using Hearthpulse.Core;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// "Your decks" in both hosts (<c>MainViewModel.Decks.cs</c>): two lists that look like versions of one deck are
/// suggested; "not the same" keeps them apart for good, Merge makes one deck of them; a deck opens on its page.
/// </summary>
public sealed class DeckSuggestionsContractTests
{
    private static readonly DateTime At = new(2026, 10, 23, 16, 0, 0);

    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    /// <summary>A Standard Death Knight deck code: 15 cards of two copies from <paramref name="first"/>, swaps 500 higher.</summary>
    private static string Code(int first, params int[] swaps)
    {
        var bytes = new List<byte>();
        void Varint(int value)
        {
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value > 0 ? (byte)(b | 0x80) : b);
            }
            while (value > 0);
        }

        foreach (var header in new[] { 0, 1, 2, 1, 78065, 0 })
        {
            Varint(header);
        }

        var cards = Enumerable.Range(first, 15).Select(d => swaps.Contains(d) ? d + 500 : d).ToList();
        Varint(cards.Count);
        cards.ForEach(Varint);
        Varint(0);
        return Convert.ToBase64String(bytes.ToArray());
    }

    private static GameRecord Game(string key, string deck, string code, long deckId, int minutes, string result = "Win") => new()
    {
        Key = key,
        StartedAt = At.AddMinutes(minutes),
        EndedAt = At.AddMinutes(minutes + 8),
        Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = result,
        PlayerClass = "DEATHKNIGHT", OpponentClass = "PRIEST", DeckName = deck, DeckCode = code, DeckId = deckId,
    };

    /// <summary>Two versions of one Death Knight list (28 of 30 cards in common), two games each.</summary>
    private static async Task<dynamic> TwoVersions(bool wpf, HistoryStore history)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var v2 = Code(1000);
        var v3 = Code(1000, 1014);
        Assert.True(history.Add(Game("g1", "Рыцарь смерти2", v2, 11, 0)));
        Assert.True(history.Add(Game("g2", "Рыцарь смерти2", v2, 11, 10, "Loss")));
        Assert.True(history.Add(Game("g3", "Рыцарь смерти3", v3, 12, 20)));
        Assert.True(history.Add(Game("g4", "Рыцарь смерти3", v3, 12, 30)));
        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        await model.RefreshAsync();
        await Until(() => ((IEnumerable<object>)model.DeckSuggestions).Any());
        return model;
    }

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(done(), "the decks were not rebuilt in 5 s");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_versions_are_suggested_and_not_the_same_keeps_them_apart(bool wpf)
    {
        var history = History();
        var model = await TwoVersions(wpf, history);
        dynamic suggestion = ((IEnumerable<dynamic>)model.DeckSuggestions).Single();
        // Names are shown tidied: "Рыцарь смерти3" reads "Рыцарь смерти 3".
        Assert.Equal("Рыцарь смерти 3 + Рыцарь смерти 2", (string)suggestion.Title);
        Assert.Equal(2, ((IEnumerable<object>)model.Decks).Count());

        model.DismissSuggested.Execute(suggestion);
        await Until(() => !((IEnumerable<object>)model.DeckSuggestions).Any());

        Assert.Single(history.DismissedDeckPairs());
        Assert.Equal(2, ((IEnumerable<object>)model.Decks).Count());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Merge_makes_one_deck_of_both_versions(bool wpf)
    {
        var history = History();
        var model = await TwoVersions(wpf, history);
        dynamic suggestion = ((IEnumerable<dynamic>)model.DeckSuggestions).Single();

        model.MergeSuggested.Execute(suggestion);
        await Until(() => ((IEnumerable<object>)model.Decks).Count() == 1);

        dynamic deck = ((IEnumerable<dynamic>)model.Decks).Single();
        Assert.Equal(4, (int)deck.Games);
        Assert.Equal("3–1", (string)deck.Record);
        Assert.Empty((IEnumerable<object>)model.DeckSuggestions);
        Assert.Equal(2, Assert.Single(history.DeckFamilies()).Members.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_deck_opens_on_its_page_and_an_unknown_one_does_not(bool wpf)
    {
        var model = await TwoVersions(wpf, History());
        dynamic deck = ((IEnumerable<dynamic>)model.Decks).First();

        Assert.False((bool)model.OpenDeckPage("no-such-deck"));
        Assert.False((bool)model.DeckOpen);

        model.OpenDeck.Execute(deck);
        Assert.True((bool)model.DeckOpen);
        Assert.Equal("FT_STANDARD", (string)model.ConstructedFormat);
        Assert.True((bool)model.GoBack("Standard"));
        Assert.False((bool)model.DeckOpen);
    }
}
