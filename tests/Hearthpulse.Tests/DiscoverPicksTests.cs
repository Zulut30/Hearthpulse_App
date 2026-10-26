using System.Net;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.CardPool;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Pool;
using Hearthpulse.Core.Power;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The Discover pick hint built on the stats pool: what is recorded from the player's own choices, what a shared
/// Constructed game holds, and how the pool's totals turn into the numbers on the card pool panel.
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
    public void The_hint_shows_numbers_only_with_enough_offers_and_marks_a_clear_favourite()
    {
        using var doc = JsonDocument.Parse("""
            {"format":"standard","days":30,"games":900,"sources":[
              {"source":"CORE_YOP_001","choices":300,"options":[["CARD_A",200,120,70],["CARD_B",180,40,15],["CARD_C",19,19,19],["BAD"],["CARD_D","x",1,1]]}
            ]}
            """);
        var stats = DiscoverPickStats.Parse(doc.RootElement);
        Assert.Equal(900, stats.Games);
        Assert.Equal(3, stats.Sources["CORE_YOP_001"].Options.Count);

        var view = DiscoverPicks.Build(stats, "CORE_YOP_001", ["CARD_C", "CARD_A", "CARD_B"], CardDb.Empty);
        Assert.Equal(["CARD_C", "CARD_A", "CARD_B"], view.Rows.Select(r => r.CardId));
        Assert.Equal(300, view.Choices);
        // Under 20 offers: no number for CARD_C.
        Assert.Null(view.Rows[0].PickRate);
        Assert.Equal(0.6, view.Rows[1].PickRate!.Value, 6);
        Assert.Equal(70.0 / 120, view.Rows[1].WinRate!.Value, 6);
        Assert.Equal(15.0 / 40, view.Rows[2].WinRate!.Value, 6);
        Assert.Equal([false, true, false], view.Rows.Select(r => r.Top));

        // A source the pool has not seen: the options without numbers, so the panel says there are too few games.
        var unknown = DiscoverPicks.Build(stats, "OTHER", ["CARD_A", "CARD_B"], CardDb.Empty);
        Assert.False(unknown.HasNumbers);
        Assert.Equal(0, unknown.Choices);
    }

    [Fact]
    public void Close_pick_rates_get_no_favourite_mark()
    {
        var options = new Dictionary<string, PickOption> { ["A"] = new(100, 40, 20), ["B"] = new(100, 37, 20) };
        var stats = new DiscoverPickStats("standard", 50, new Dictionary<string, SourcePicks> { ["S"] = new(100, options) });

        Assert.All(DiscoverPicks.Build(stats, "S", ["A", "B"], CardDb.Empty).Rows, r => Assert.False(r.Top));
    }

    [Fact]
    public async Task Without_a_pool_address_the_totals_are_never_requested()
    {
        var handler = new CountingHandler();
        var source = new DiscoverPickSource(new HttpClient(handler), () => string.Empty);

        Assert.Null(source.TryGet("standard"));
        await Task.Delay(50);
        Assert.Equal(0, handler.Requests);

        var loaded = new DiscoverPickSource(new HttpClient(handler), () => "https://pool.test");
        var changed = new TaskCompletionSource();
        loaded.Changed += () => changed.TrySetResult();
        Assert.Null(loaded.TryGet("wild"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("https://pool.test/v1/choices?format=wild", handler.LastUrl);
        Assert.Equal(12, loaded.TryGet("wild")!.Games);
        // One load for the whole format: asking again does not reach the server.
        Assert.Equal(1, handler.Requests);
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

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"format":"wild","days":30,"games":12,"sources":[]}"""),
            });
        }
    }
}
