using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Cards;

namespace Hearthpulse.Tests;

/// <summary>
/// The draft tracker on a real Arena.log (2026-10-28, an Underground Arena run of a Hunter (Sylvanas skin), a legendary with
/// its package; the client's Russian card names). The truth is the deck code Decks.log announced for the run's game.
/// </summary>
public sealed class ArenaDraftLiveLogTests
{
    /// <summary>The cards the tracker needs to know: the hero, and the legendary whose pick the client logs twice.</summary>
    private static readonly CardDb Cards = CardDb.Parse("ruRU", new MemoryStream("""
        [
          {"id":"HERO_05c","dbfId":1,"name":"Сильвана Ветрокрылая","type":"HERO","cardClass":"HUNTER"},
          {"id":"WW_379","dbfId":2,"name":"Флинт Огненная Рука","type":"MINION","rarity":"LEGENDARY","collectible":true},
          {"id":"WORK_018","dbfId":3,"name":"Рабочая лошадка","type":"MINION","rarity":"COMMON","collectible":true}
        ]
        """u8.ToArray()));

    /// <summary>
    /// The deck code of "Starting Arena Game With Deck" (Decks.log of the same session, deck id 2776251657), decoded:
    /// three Work Horses, two Furious Fowls, two Brutal Deathgulpers, Flint once with its package of three.
    /// </summary>
    private static readonly Dictionary<string, int> RealDeck = new(StringComparer.Ordinal)
    {
        ["BE_733"] = 1, ["CORE_DMF_067"] = 1, ["CORE_REV_308"] = 1, ["CORE_TRL_111"] = 1, ["DEEP_001"] = 1, ["END_013"] = 2,
        ["TIME_045"] = 1, ["TIME_059"] = 1, ["TIME_434"] = 1, ["TIME_602"] = 1, ["TTN_081"] = 1, ["TTN_302"] = 1, ["VAC_304"] = 1,
        ["VAC_406"] = 1, ["VAC_410"] = 2, ["VAC_432"] = 1, ["WORK_018"] = 3, ["WW_379"] = 1, ["WW_411"] = 1, ["WW_418"] = 1,
        ["WW_433"] = 1, ["WW_434"] = 1, ["WW_806"] = 1, ["WW_807"] = 1, ["WW_808"] = 1, ["YOG_506"] = 1,
    };

    private static string[] Log() => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Arena", "arena-2026-10-28.log"));

    [Fact]
    public void The_whole_real_log_gives_the_deck_the_client_played()
    {
        var tracker = new ArenaDraftTracker();
        foreach (var line in Log())
        {
            tracker.Feed(line, Cards);
        }

        var state = tracker.State;
        Assert.Equal("HERO_05c", state.HeroCardId);
        Assert.Equal(2776251657, state.DeckId);
        Assert.Equal("ACTIVE_DRAFT_DECK", state.Mode);
        Assert.Equal(30, state.Cards.Count);
        Assert.Equal(RealDeck, state.Cards.GroupBy(c => c, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal));
    }

    /// <summary>
    /// When the draft ends, before the client lists the deck: every pick once (Flint's second line is no pick, so its
    /// package, added from the offer, would not come twice), the package still to come.
    /// </summary>
    [Fact]
    public void At_the_end_of_the_draft_each_pick_counts_once()
    {
        var tracker = new ArenaDraftTracker();
        var picks = new List<int>();
        foreach (var line in Log().TakeWhile(l => !l.Contains("OnChoicesAndContents", StringComparison.Ordinal)))
        {
            tracker.Feed(line, Cards);
            picks.Add(tracker.PickCount);
        }

        var state = tracker.State;
        Assert.Equal(2776251657, state.DeckId);
        Assert.Equal(27, state.Cards.Count);
        Assert.Single(state.Cards, c => c == "WW_379");
        Assert.Equal(3, state.Cards.Count(c => c == "WORK_018"));
        // Hero (two lines) + 27 cards; Flint's repeat adds nothing.
        Assert.Equal(29, tracker.PickCount);
    }

    /// <summary>The client's list alone (the app opened after the draft): as listed, even though it misses copies.</summary>
    [Fact]
    public void Without_the_picks_the_list_is_the_deck()
    {
        var tracker = new ArenaDraftTracker();
        foreach (var line in Log().SkipWhile(l => !l.Contains("OnChoicesAndContents", StringComparison.Ordinal)))
        {
            tracker.Feed(line, Cards);
        }

        Assert.Equal(27, tracker.State.Cards.Count);
        Assert.Equal(2776251657, tracker.State.DeckId);
    }

    /// <summary>
    /// Without the card data (offline first start, a failed load) the legendary's repeat is still one pick: the whole
    /// log gives the client's 30 cards.
    /// </summary>
    [Fact]
    public void Without_the_card_data_the_whole_log_still_gives_the_deck()
    {
        var tracker = new ArenaDraftTracker();
        foreach (var line in Log())
        {
            tracker.Feed(line, CardDb.Empty);
        }

        Assert.Equal(30, tracker.State.Cards.Count);
        Assert.Single(tracker.State.Cards, c => c == "WW_379");
    }

    /// <summary>A card whose pick came with a package (from the offer) and is logged again is one pick, legendary or not.</summary>
    [Fact]
    public void A_repeat_after_a_package_is_one_pick()
    {
        var tracker = new ArenaDraftTracker();
        tracker.Feed("D 00:00:01.0 Client chooses: Сильвана Ветрокрылая (HERO_05c)", Cards);
        tracker.Feed("D 00:00:02.0 Client chooses: Рабочая лошадка (WORK_018)", Cards);
        tracker.AddPackage(["WW_808", "WW_411"]);
        var picks = tracker.PickCount;
        Assert.False(tracker.Feed("D 00:00:03.0 Client chooses: Рабочая лошадка (WORK_018)", Cards));

        Assert.Equal(picks, tracker.PickCount);
        Assert.Equal(["WORK_018", "WW_808", "WW_411"], tracker.State.Cards);
    }

    /// <summary>OnBegin again with the run's own deck id (back on the screen) keeps the run and its picks.</summary>
    [Fact]
    public void The_same_deck_begun_again_is_the_same_run()
    {
        var tracker = new ArenaDraftTracker();
        tracker.Feed("D 00:00:00.0 DraftManager.OnBegin - Got new draft deck with ID: 5", Cards);
        tracker.Feed("D 00:00:01.0 Client chooses: Сильвана Ветрокрылая (HERO_05c)", Cards);
        tracker.Feed("D 00:00:02.0 Client chooses: Рабочая лошадка (WORK_018)", Cards);
        tracker.Feed("D 00:01:00.0 DraftManager.OnBegin - Got new draft deck with ID: 5", Cards);
        Assert.Equal(["WORK_018"], tracker.State.Cards);
        Assert.Equal("HERO_05c", tracker.State.HeroCardId);

        tracker.Feed("D 00:02:00.0 DraftManager.OnBegin - Got new draft deck with ID: 6", Cards);
        Assert.Empty(tracker.State.Cards);
        Assert.Equal(6, tracker.State.DeckId);
    }

    /// <summary>A common taken twice in a row is two picks; only a legendary's repeat is the same pick.</summary>
    [Fact]
    public void Two_commons_in_a_row_are_two_picks()
    {
        var tracker = new ArenaDraftTracker();
        tracker.Feed("D 00:00:00.0 DraftManager.OnBegin - Got new draft deck with ID: 5", Cards);
        tracker.Feed("D 00:00:01.0 Client chooses: Сильвана Ветрокрылая (HERO_05c)", Cards);
        tracker.Feed("D 00:00:02.0 Client chooses: Рабочая лошадка (WORK_018)", Cards);
        tracker.Feed("D 00:00:03.0 Client chooses: Рабочая лошадка (WORK_018)", Cards);

        Assert.Equal(["WORK_018", "WORK_018"], tracker.State.Cards);
        Assert.Equal(5, tracker.State.DeckId);
    }
}
