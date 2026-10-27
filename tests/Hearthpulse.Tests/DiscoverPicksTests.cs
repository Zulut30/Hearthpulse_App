using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The Discover pick hint: what is recorded from the player's own choices, what a shared Constructed game holds, and
/// how the Hearthpulse API's card choices turn into the numbers on the card pool panel.
/// </summary>
public sealed class DiscoverPicksTests
{
    private const int Me = 1;
    private const int Opponent = 2;

    [Fact]
    public void Only_the_players_own_choice_is_recorded_with_what_was_taken()
    {
        var parser = new PowerParser(new DateOnly(2026, 10, 26));
        var picks = new ConstructedPicksTracker();
        var pool = new CardPoolTracker();
        parser.AddListener(picks);
        parser.AddListener(pool);
        Feed(parser,
        [
            .. Game(),
            // The starting hand tells the parser who the local player is, as in a real log.
            .. Entity(20, "CARD_IN_HAND", "HAND", Me),
            .. Entity(30, "CORE_YOP_001", "PLAY", Me),
            .. Entity(40, "CARD_A", "SETASIDE", Me),
            .. Entity(41, "CARD_B", "SETASIDE", Me),
            .. Entity(42, "CARD_C", "SETASIDE", Me),
            .. Entity(60, "OPP_SOURCE", "PLAY", Opponent),
            .. Entity(61, "OPP_A", "SETASIDE", Opponent),
            .. Entity(62, "OPP_B", "SETASIDE", Opponent),
            // The opponent's Discover: the parser never hands it to listeners.
            "DebugPrintEntityChoices() - id=6 Player=Opp#5678 TaskList=11 ChoiceType=GENERAL CountMin=1 CountMax=1",
            $"DebugPrintEntityChoices() -   Source={Ref(60, "OPP_SOURCE", "PLAY", Opponent)}",
            $"DebugPrintEntityChoices() -   Entities[0]={Ref(61, "OPP_A", "SETASIDE", Opponent)}",
            $"DebugPrintEntityChoices() -   Entities[1]={Ref(62, "OPP_B", "SETASIDE", Opponent)}",
            "DebugPrintEntityChoices() - id=7 Player=Me#1234 TaskList=12 ChoiceType=GENERAL CountMin=1 CountMax=1",
            $"DebugPrintEntityChoices() -   Source={Ref(30, "CORE_YOP_001", "PLAY", Me)}",
            $"DebugPrintEntityChoices() -   Entities[0]={Ref(40, "CARD_A", "SETASIDE", Me)}",
            $"DebugPrintEntityChoices() -   Entities[1]={Ref(41, "CARD_B", "SETASIDE", Me)}",
            $"DebugPrintEntityChoices() -   Entities[2]={Ref(42, "CARD_C", "SETASIDE", Me)}",
        ]);

        // While the choice is open the pool panel knows its options, in offer order.
        Assert.NotNull(pool.OpenChoice);
        var open = pool.OpenChoice!.Value;
        Assert.Equal("CORE_YOP_001", open.Source);
        Assert.Equal(["CARD_A", "CARD_B", "CARD_C"], open.Offered);
        Assert.Empty(picks.Picks);

        Feed(parser,
        [
            "DebugPrintEntitiesChosen() - id=7 Player=Me#1234 EntitiesCount=1",
            $"DebugPrintEntitiesChosen() -   Entities[0]={Ref(41, "CARD_B", "SETASIDE", Me)}",
        ]);

        var pick = Assert.Single(picks.Picks);
        Assert.Equal(5, pick.Turn);
        Assert.Equal("CORE_YOP_001", pick.Source);
        Assert.Equal(["CARD_A", "CARD_B", "CARD_C"], pick.Offered);
        Assert.Equal(["CARD_B"], pick.Picked);
        Assert.Null(pool.OpenChoice);
    }

    [Theory]
    [InlineData("GT_RANKED", "FT_STANDARD", "Win", "standard")]
    [InlineData("GT_CASUAL", "FT_WILD", "Loss", "wild")]
    [InlineData("GT_ARENA", "FT_WILD", "Win", null)]
    [InlineData("GT_VS_FRIEND", "FT_STANDARD", "Win", null)]
    [InlineData("GT_RANKED", "FT_TWIST", "Win", null)]
    [InlineData("GT_RANKED", "FT_STANDARD", "Tie", null)]
    public void Only_ranked_and_casual_standard_or_wild_games_with_a_result_are_shared(string gameType, string format, string result, string? expected)
    {
        var game = ConstructedPoolGame.From(Outcome(gameType, format, result), new string('a', 64));

        Assert.Equal(expected, game?.Format);
        if (game is not null)
        {
            Assert.Equal(result == "Win", game.Won);
            Assert.Matches("^[0-9a-f]{32}$", game.GameId);
        }
    }

    [Fact]
    public void A_shared_game_holds_its_picks_and_nothing_that_names_the_player_or_the_deck()
    {
        var outcome = Outcome("GT_RANKED", "FT_STANDARD", "Win");
        var game = ConstructedPoolGame.From(outcome, new string('a', 64))!;
        // The same game always gets the same id, and not the id its key would get as a Battlegrounds game.
        Assert.Equal(game.GameId, ConstructedPoolGame.From(outcome, new string('a', 64))!.GameId);
        Assert.NotEqual(PoolGame.Id(outcome.Key, new string('a', 64)), game.GameId);
        Assert.Null(ConstructedPoolGame.From(outcome with { ConstructedPicks = [] }, new string('a', 64)));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(PoolClient.ConstructedBody([game])));
        Assert.Equal(1, json.RootElement.GetProperty("v").GetInt32());
        var sent = json.RootElement.GetProperty("games")[0];
        Assert.Equal(["id", "day", "format", "won", "src", "picks"], sent.EnumerateObject().Select(p => p.Name));
        Assert.Equal("""[[4,"CORE_YOP_001",["CARD_A","CARD_B","CARD_C"],["CARD_B"]]]""", sent.GetProperty("picks").GetRawText());
    }

    [Fact]
    public void Picks_are_shared_only_after_agreeing_to_the_list_that_names_them()
    {
        var settings = new AppSettings { PoolEndpoint = "https://pool.test", PoolConsent = true, PoolConsentVersion = 3 };
        Assert.True(PoolClient.SharingAll(settings));
        Assert.False(PoolClient.SharingPicks(settings));

        settings.PoolConsentVersion = PoolClient.ConsentVersion;
        Assert.True(PoolClient.SharingPicks(settings));
        settings.PoolConsent = false;
        Assert.False(PoolClient.SharingPicks(settings));
    }

    [Fact]
    public void A_card_choices_answer_turns_percents_into_shares_and_keeps_the_few_games_mark()
    {
        using var doc = JsonDocument.Parse("""
            {"source":"hsreplay","card":{"card_id":"BE_555","dbf_id":129823,"name":"Reload"},"format":"standard","rank":"legend",
             "stats":{"state":"ok","as_of":"2026-10-26T03:57:45Z","options_total":4},
             "options":[
               {"dbf_id":111365,"card_id":"CORE_CS2_024","name":"Frostbolt","popularity":80.0,"win_rate":50.0,"in_pool":false,"low_sample":false,"undervalued":false},
               {"dbf_id":118554,"card_id":"DINO_414","popularity":46.66,"win_rate":0.0,"low_sample":true,"undervalued":false},
               {"dbf_id":0,"card_id":"","popularity":10.0},
               {"dbf_id":5,"popularity":"x","win_rate":120.0}
             ],
             "pool":{"kind":"discover","total":45,"cards":[]},"available":[{"format":"standard","rank":"legend"}],"premium_session":{"state":"ok"}}
            """);
        var choices = HearthpulseData.ParseCardChoices(doc.RootElement)!;

        Assert.Equal(("BE_555", "standard", "legend"), (choices.CardId, choices.Format, choices.Rank));
        Assert.Equal(new DateTime(2026, 10, 26, 3, 57, 45, DateTimeKind.Utc), choices.AsOf);
        // The row without an id is dropped; a number that is not a percent becomes "unknown".
        Assert.Equal(3, choices.Options.Count);
        Assert.Equal(new CardChoiceOption("CORE_CS2_024", 111365, 0.8, 0.5, false), choices.Options[0]);
        Assert.Equal(0.4666, choices.Options[1].PickRate!.Value, 6);
        Assert.True(choices.Options[1].LowSample);
        Assert.Equal(new CardChoiceOption(string.Empty, 5, null, null, false), choices.Options[2]);

        // A card the server has not collected: no options, so no hint.
        using var empty = JsonDocument.Parse("""{"card":{"card_id":"AV_107"},"format":"wild","rank":"legend","stats":{"state":"not_collected","as_of":null},"options":[]}""");
        var none = HearthpulseData.ParseCardChoices(empty.RootElement)!;
        Assert.Empty(none.Options);
        Assert.Null(DiscoverPicks.Build(none, "AV_107", ["A", "B"], CardDb.Empty));
    }

    [Fact]
    public void The_card_list_says_which_cards_have_numbers_in_which_formats()
    {
        using var doc = JsonDocument.Parse("""
            [{"card_id":"AV_107","dbf_id":67192,"kind":"discover","formats":["wild"],"has_hsreplay_stats":true,"collected":[]},
             {"card_id":"BE_555","dbf_id":129823,"formats":["standard","wild"],"has_hsreplay_stats":false},
             {"dbf_id":1},"x"]
            """);
        var cards = HearthpulseData.ParseChoiceCards(doc.RootElement);

        Assert.Equal(["AV_107", "BE_555"], cards.Select(c => c.CardId));
        Assert.Equal(67192, cards[0].DbfId);
        Assert.True(cards[0].HasStats);
        Assert.Equal(["wild"], cards[0].Formats);
        Assert.False(cards[1].HasStats);
    }

    [Fact]
    public void Offered_cards_match_the_api_options_by_card_id_then_by_dbf_id()
    {
        // The log names a card the API lists under another id but the same dbfId.
        var cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
            [{"id":"CARD_A","dbfId":101,"name":"Alpha"},{"id":"REPRINT_B","dbfId":102,"name":"Beta"},{"id":"CARD_C","dbfId":103,"name":"Gamma"}]
            """)));
        var choices = new CardChoices("SRC", "standard", "legend",
        [
            new CardChoiceOption("CARD_A", 101, 0.60, 0.58, false),
            new CardChoiceOption("OTHER_ID_B", 102, 0.22, 0.38, false),
            new CardChoiceOption("CARD_D", 104, 0.90, 0.70, false),
        ], null);

        var view = DiscoverPicks.Build(choices, "SRC", ["CARD_C", "CARD_A", "REPRINT_B"], cards)!;

        Assert.Equal(["CARD_C", "CARD_A", "REPRINT_B"], view.Rows.Select(r => r.CardId));
        Assert.Equal(["Gamma", "Alpha", "Beta"], view.Rows.Select(r => r.Name));
        // Not among the API's options: no numbers.
        Assert.Null(view.Rows[0].PickRate);
        Assert.Equal(0.60, view.Rows[1].PickRate);
        Assert.Equal(0.38, view.Rows[2].WinRate);
        Assert.Equal([false, true, false], view.Rows.Select(r => r.Top));
        Assert.Equal("legend", view.Rank);
        Assert.False(view.HasLowSample);
    }

    [Fact]
    public void Few_games_and_close_pick_rates_get_no_favourite_mark()
    {
        var low = new CardChoices("S", "standard", "legend", [new("A", 0, 0.70, 0.5, true), new("B", 0, 0.30, 0.5, false), new("C", 0, 0.28, 0.5, false)], null);
        var view = DiscoverPicks.Build(low, "S", ["A", "B", "C"], CardDb.Empty)!;
        Assert.All(view.Rows, r => Assert.False(r.Top));
        Assert.True(view.HasLowSample);

        var close = new CardChoices("S", "standard", "legend", [new("A", 0, 0.40, 0.5, false), new("B", 0, 0.37, 0.5, false)], null);
        Assert.All(DiscoverPicks.Build(close, "S", ["A", "B"], CardDb.Empty)!.Rows, r => Assert.False(r.Top));
    }

    [Fact]
    public async Task The_source_loads_the_list_once_then_one_card_when_its_choice_opens_and_keeps_it_on_disk()
    {
        var cache = Path.Combine(TestDataFolder.Path, "card-choices-" + Guid.NewGuid().ToString("N"));
        var server = new ChoicesServer();
        var source = new DiscoverChoiceSource(new HearthpulseData(new HearthpulseApi(new HttpClient(server), "https://api.test/"), cache));

        var changed = new SemaphoreSlim(0);
        source.Changed += () => changed.Release();
        Assert.Null(source.TryGet("standard", "BE_555", CardDb.Empty));
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(["https://api.test/v1/hsreplay/card-choices/cards"], server.Urls);

        // Cards the API has no numbers for (or not in this format) are never asked for.
        Assert.Null(source.TryGet("standard", "NO_STATS", CardDb.Empty));
        Assert.Null(source.TryGet("standard", "WILD_ONLY", CardDb.Empty));
        Assert.Null(source.TryGet("standard", "UNKNOWN", CardDb.Empty));

        Assert.Null(source.TryGet("standard", "BE_555", CardDb.Empty));
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(5)));
        var choices = source.TryGet("standard", "BE_555", CardDb.Empty);
        Assert.Equal(0.8, Assert.Single(choices!.Options).PickRate);
        // Every later publish answers from memory.
        source.TryGet("standard", "BE_555", CardDb.Empty);
        Assert.Equal(["https://api.test/v1/hsreplay/card-choices/cards", "https://api.test/v1/hsreplay/card-choices?card=BE_555&format=standard&rank=legend"], server.Urls);

        // A new session reads both answers from the disk cache, without the network; only what is read is kept.
        var offline = new HearthpulseData(new HearthpulseApi(new HttpClient(new ChoicesServer { Offline = true }), "https://api.test/"), cache);
        Assert.Equal(4, (await offline.LoadChoiceCardsAsync()).Count);
        Assert.Equal(0.5, Assert.Single((await offline.LoadCardChoicesAsync("BE_555", "standard"))!.Options).WinRate);
        Assert.DoesNotContain("Frostbolt", File.ReadAllText(Path.Combine(cache, "card-choices-standard-legend-BE_555.json")));
    }

    private static GameOutcome Outcome(string gameType, string format, string result) =>
        new("Ranked", result, 0, null, null, "HERO_08")
        {
            Key = "20261026120000|Me#1234|" + gameType,
            GameType = gameType,
            Format = format,
            ConstructedPicks = [new ConstructedPick(4, "CORE_YOP_001", ["CARD_A", "CARD_B", "CARD_C"], ["CARD_B"])],
        };

    private static string[] Game() =>
    [
        "DebugPrintPower() - CREATE_GAME",
        "DebugPrintPower() -     GameEntity EntityID=4",
        "DebugPrintPower() -         tag=CARDTYPE value=GAME",
        "DebugPrintPower() -         tag=TURN value=5",
        $"DebugPrintPower() -     Player EntityID=14 PlayerID={Me} GameAccountId=[hi=144115198130930503 lo=11]",
        $"DebugPrintPower() -         tag=CONTROLLER value={Me}",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        $"DebugPrintPower() -     Player EntityID=15 PlayerID={Opponent} GameAccountId=[hi=144115198130930503 lo=22]",
        $"DebugPrintPower() -         tag=CONTROLLER value={Opponent}",
        "DebugPrintPower() -         tag=CARDTYPE value=PLAYER",
        "DebugPrintGame() - GameType=GT_RANKED",
        "DebugPrintGame() - FormatType=FT_STANDARD",
        $"DebugPrintGame() - PlayerID={Me}, PlayerName=Me#1234",
        $"DebugPrintGame() - PlayerID={Opponent}, PlayerName=Opp#5678",
    ];

    private static void Feed(PowerParser parser, params string[] bodies)
    {
        foreach (var body in bodies)
        {
            parser.Feed("D 12:30:00.0000000 GameState." + body);
        }

        parser.Flush();
    }

    private static string[] Entity(int id, string cardId, string zone, int controller) =>
    [
        $"DebugPrintPower() - FULL_ENTITY - Creating ID={id} CardID={cardId}",
        $"DebugPrintPower() -     tag=ZONE value={zone}",
        $"DebugPrintPower() -     tag=CONTROLLER value={controller}",
        "DebugPrintPower() -     tag=CARDTYPE value=SPELL",
    ];

    private static string Ref(int id, string cardId, string zone, int player) =>
        $"[entityName=X id={id} zone={zone} zonePos=0 cardId={cardId} player={player}]";

    private sealed class ChoicesServer : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        public bool Offline { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Offline)
            {
                throw new HttpRequestException("offline");
            }

            var url = request.RequestUri!.ToString();
            lock (Urls)
            {
                Urls.Add(url);
            }

            var body = url.EndsWith("/cards", StringComparison.Ordinal)
                ? """{"data":[{"card_id":"BE_555","dbf_id":129823,"name":"Reload","formats":["standard","wild"],"has_hsreplay_stats":true},{"card_id":"NO_STATS","dbf_id":2,"formats":["standard"],"has_hsreplay_stats":false},{"card_id":"WILD_ONLY","dbf_id":3,"formats":["wild"],"has_hsreplay_stats":true},{"card_id":"X","dbf_id":4,"formats":["standard"],"has_hsreplay_stats":true}],"meta":{"count":4}}"""
                : """{"data":{"card":{"card_id":"BE_555","name":"Reload","text":"Discover a spell"},"format":"standard","rank":"legend","stats":{"state":"ok","as_of":"2026-10-26T03:57:45Z"},"options":[{"dbf_id":111365,"card_id":"CORE_CS2_024","name":"Frostbolt","popularity":80.0,"win_rate":50.0,"low_sample":false}],"pool":{"cards":[]}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
