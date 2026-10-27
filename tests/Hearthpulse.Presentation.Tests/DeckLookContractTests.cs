using System.ComponentModel;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The deck page header (chips, big win rate on its 30–70 % scale), the main version's curve and tracker-style list,
/// and "Where you finish" on the Battlegrounds stats, in both compiled versions of the shared source.
/// </summary>
public sealed class DeckLookContractTests
{
    internal static readonly CardDb Cards = CardDb.Parse("enUS", new MemoryStream("""
        [
          {"id":"CARD_A","dbfId":1001,"name":"Alpha","type":"SPELL","cost":1,"rarity":"COMMON","cardClass":"MAGE"},
          {"id":"CARD_B","dbfId":1002,"name":"Bravo","type":"MINION","cost":2,"rarity":"RARE","cardClass":"MAGE"},
          {"id":"CARD_C","dbfId":1003,"name":"Charlie","type":"MINION","cost":2,"rarity":"EPIC","cardClass":"MAGE"},
          {"id":"CARD_D","dbfId":1004,"name":"Delta","type":"MINION","cost":9,"rarity":"LEGENDARY","cardClass":"MAGE"}
        ]
        """u8.ToArray()));

    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    private static GameRecord Game(int number, string result, string code) => new()
    {
        Key = "deck-look-game-" + number,
        StartedAt = new DateTime(2026, 10, 23, 10, 0, 0).AddMinutes(number * 10),
        EndedAt = new DateTime(2026, 10, 23, 10, 8, 0).AddMinutes(number * 10),
        Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = result,
        PlayerClass = "MAGE", OpponentClass = "WARRIOR", DeckName = "Curve Mage", DeckCode = code,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deck_header_shows_the_win_rate_on_its_scale_and_the_main_list_with_its_curve(bool wpf)
    {
        var code = Code((1001, 2), (1002, 2), (1003, 1), (1004, 1));
        var games = new[] { Game(0, "Win", code), Game(1, "Win", code), Game(2, "Loss", code), Game(3, "Win", code) };
        var history = History();
        foreach (var game in games)
        {
            Assert.True(history.Add(game));
        }

        dynamic owner = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        dynamic model = wpf ? (object)new Hearthpulse.App.Main.Decks.DeckDetailsViewModel(owner)
            : new Hearthpulse.UI.Main.Decks.DeckDetailsViewModel(owner);
        var families = DeckFamilies.Build(DeckFamilies.Versions(games), []);
        await Changed((INotifyPropertyChanged)model, "Games", () => model.Show(families.Single(), families, Array.Empty<MergeSuggestion>(), null, Cards));

        // 3–1: 75 % is past the scale's 70 % end, so the dot sits at its end; green from 53 %.
        Assert.Equal(0.75.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), (string)model.WinRateText);
        Assert.Equal(1.0, (double)model.WinScale);
        Assert.True((bool)model.HasWinScale);
        Assert.Equal(1, (int)model.WinSign);
        Assert.StartsWith("3–1", (string)model.WinRecord);
        Assert.Equal(2, ((IEnumerable<string>)model.Facts).Count());
        Assert.DoesNotContain(((IEnumerable<object>)model.Tiles).Cast<dynamic>(), t => (string)t.Label == Loc.T("Deck.Tile.WinRate"));

        // Six cards: 0 at cost 0, 2 at cost 1, 3 at cost 2, the 9-drop in 7+ (the tallest bar is 1.0).
        var curve = ((IEnumerable<object>)model.Curve).Cast<dynamic>().ToList();
        Assert.Equal(8, curve.Count);
        Assert.Equal(new[] { 0, 2, 3, 0, 0, 0, 0, 1 }, curve.Select(b => (int)b.Count));
        Assert.Equal("7+", (string)curve[7].Label);
        Assert.Equal(1.0, (double)curve[2].Fraction);
        Assert.True((bool)curve[0].Empty);

        // By cost then name; copies "2", nothing for one copy, a star for the legendary.
        var list = ((IEnumerable<object>)model.DeckList).Cast<dynamic>().ToList();
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie", "Delta" }, list.Select(r => (string)r.Name));
        Assert.Equal(new[] { "2", "2", string.Empty, "★" }, list.Select(r => (string)r.Count));
        Assert.Equal("RARE", (string)list[1].Rarity);
        Assert.True((bool)list[3].Legendary);
        Assert.Contains("6", (string)model.DeckListNote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Where_you_finish_has_a_bar_per_place_green_in_the_top_four(bool wpf)
    {
        var history = History();
        var places = new[] { 1, 1, 3, 6 };
        for (var i = 0; i < places.Length; i++)
        {
            Assert.True(history.Add(new GameRecord
            {
                Key = "deck-look-bg-" + i,
                StartedAt = DateTime.Now.AddMinutes(-60 + (i * 10)),
                EndedAt = DateTime.Now.AddMinutes(-52 + (i * 10)),
                Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = places[i], RatingAfter = 6000 + i,
            }));
        }

        dynamic model = wpf ? (object)new WpfMain(history, new AppSettings()) : new AvaMain(history, new AppSettings());
        await Changed((INotifyPropertyChanged)model, "BgPlaces", () => model.RefreshAsync());
        var rows = ((IEnumerable<object>)model.BgPlaces).Cast<dynamic>().ToList();
        Assert.Equal(8, rows.Count);
        Assert.Equal(1.0, (double)rows[0].Fraction);
        Assert.Equal(0.5, (double)rows[2].Fraction);
        Assert.Equal(0.0, (double)rows[1].Fraction);
        Assert.Equal(new[] { true, true, true, true, false, false, false, false }, rows.Select(r => (bool)r.Top));
        Assert.Equal(0.5.ToString("P0", System.Globalization.CultureInfo.CurrentCulture), (string)rows[0].Share);
    }

    /// <summary>A Standard deck code (Jaina) with the given cards.</summary>
    internal static string Code(params (int Dbf, int Copies)[] cards)
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

        Varint(0);
        Varint(1);
        Varint(2);
        Varint(1);
        Varint(637);
        foreach (var copies in new[] { 1, 2 })
        {
            var group = cards.Where(c => c.Copies == copies).ToList();
            Varint(group.Count);
            group.ForEach(c => Varint(c.Dbf));
        }

        Varint(0);
        return Convert.ToBase64String(bytes.ToArray());
    }

    /// <summary>Runs <paramref name="act"/> and waits until <paramref name="property"/> is raised (rebuilds run off the UI thread).</summary>
    private static async Task Changed(INotifyPropertyChanged model, string property, Action act)
    {
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == property)
            {
                raised.TrySetResult();
            }
        }

        model.PropertyChanged += OnChanged;
        try
        {
            act();
            await raised.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            model.PropertyChanged -= OnChanged;
        }
    }
}
