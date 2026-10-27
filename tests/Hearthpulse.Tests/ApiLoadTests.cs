using System.Net;
using System.Text;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Tracking;
using Xunit;

namespace Hearthpulse.Tests;

/// <summary>
/// The loading paths over a fake server that answers with the real fixtures (Fixtures/Mulligan): the Constructed meta
/// (tier list first, HSReplay and HSGuru mulligan numbers, expansion period, empty slice, Wild), the mulligan guide
/// source (background lookup, Wild through HSGuru), the disk cache (404s remembered) and the meta fallback's class pool.
/// The cache goes to a scratch folder.
/// </summary>
public sealed class ApiLoadTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "hp-apiload-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    [Fact]
    public async Task Standard_meta_shows_the_tier_list_first_then_mulligan_from_hsreplay_and_hsguru()
    {
        var server = FixtureServer.Standard();
        var early = new List<ConstructedMeta>();

        var meta = await ConstructedMeta.LoadAsync(Data(server), CardDb.Empty, "standard", "legend", "past_week", "enUS", new Collect(early));

        var first = Assert.Single(early);
        Assert.Equal(20, first.Archetypes.Count);
        Assert.All(first.Archetypes, a => Assert.Empty(a.Mulligan));

        Assert.NotNull(meta);
        Assert.Equal(ApiConstructedMeta.Attribution, meta.Source);
        Assert.Equal(first.Archetypes.Select(a => a.Id).Order(), meta.Archetypes.Select(a => a.Id).Order());

        var quest = meta.Archetypes.Single(a => a.Key == "Quest Priest");
        Assert.Equal(56, quest.MulliganArchetypeId);
        Assert.NotEmpty(quest.Mulligan);
        Assert.Equal(MulliganGuide.Baseline(HearthpulseData.ParseMulligan(FixtureServer.Json("56-mulligan.json")))!.Value, quest.MulliganBaseline!.Value, 6);

        // HSReplay has no C'Thun archetype: matched to Void Demon Hunter by its list's cards.
        Assert.Equal(867, meta.Archetypes.Single(a => a.Key == "C'Thun DH").MulliganArchetypeId);

        // HSReplay has no Zoolock: HSGuru's own analysis gives the numbers, with the class results as baseline.
        var zoolock = meta.Archetypes.Single(a => a.Key == "Zoolock");
        Assert.Null(zoolock.MulliganArchetypeId);
        Assert.NotEmpty(zoolock.Mulligan);
        var analysis = HearthpulseData.ParseHsguruAnalysis(FixtureServer.Json("hsguru-analysis-standard-zoolock.json").GetProperty("data"));
        Assert.Equal(analysis.WinRate!.Value, zoolock.MulliganBaseline!.Value, 6);
        Assert.All(zoolock.Mulligan, m => Assert.True(m.InHand >= MulliganGuide.MinSample && m.Drawn >= MulliganGuide.MinSample));
        Assert.Equal(zoolock.Mulligan.OrderByDescending(m => m.InHandWinRate).Select(m => m.CardId), zoolock.Mulligan.Select(m => m.CardId));

        // Lists and core cards: only archetypes in the HSGuru catalog have them.
        Assert.Equal(3, meta.Decks.Count(d => d.ArchetypeId == quest.Id));
        Assert.DoesNotContain(meta.Decks, d => d.Archetype == "Shredslock");
    }

    [Fact]
    public async Task Expansion_and_patch_periods_resolve_through_a_first_slice()
    {
        var server = FixtureServer.Standard();

        var meta = await ConstructedMeta.LoadAsync(Data(server), CardDb.Empty, "standard", "legend", "expansion", "enUS");

        Assert.NotNull(meta);
        Assert.Contains(server.Requests, r => r.Contains("v1/hsguru/meta", StringComparison.Ordinal) && r.Contains("period=past_day", StringComparison.Ordinal));
        Assert.Contains(server.Requests, r => r.Contains("v1/hsguru/meta", StringComparison.Ordinal) && r.Contains("period=black_empire", StringComparison.Ordinal));
        Assert.Equal("expansion", meta.Period);
    }

    [Fact]
    public async Task An_empty_slice_is_an_empty_meta_and_a_failure_is_null()
    {
        var server = FixtureServer.Standard();
        server.Route("rank=top_100", """{"data": {"items": []}, "meta": {"fetched_at": "2026-10-23T10:00:01Z"}}""");

        var empty = await ConstructedMeta.LoadAsync(Data(server), CardDb.Empty, "standard", "top_100", "past_week", "enUS");
        var failed = await ConstructedMeta.LoadAsync(Data(new FixtureServer()), CardDb.Empty, "standard", "legend", "past_week", "enUS");

        Assert.NotNull(empty);
        Assert.Empty(empty.Archetypes);
        Assert.Null(failed);
    }

    [Fact]
    public async Task Wild_meta_takes_its_mulligan_from_hsguru_and_classes_from_names()
    {
        var server = FixtureServer.Wild();

        var meta = await ConstructedMeta.LoadAsync(Data(server), CardDb.Empty, "wild", "all", "past_week", "enUS");

        Assert.NotNull(meta);
        Assert.DoesNotContain(server.Requests, r => r.StartsWith("api/db/", StringComparison.Ordinal));
        var yogg = meta.Archetypes.Single(a => a.Key == "Yogg Shaman");
        Assert.NotEmpty(yogg.Mulligan);
        Assert.NotNull(yogg.MulliganBaseline);
        Assert.Equal("ROGUE", meta.Archetypes.Single(a => a.Key == "Mech Rogue").Class);
        Assert.Equal("MAGE", meta.Archetypes.Single(a => a.Key == "XL Freeze Mage").Class);
        Assert.Empty(meta.Archetypes.Single(a => a.Key == "Mech Rogue").Mulligan);
    }

    [Fact]
    public async Task The_guide_for_a_wild_deck_comes_from_hsguru()
    {
        var server = FixtureServer.Wild();
        var list = Assert.Single(HearthpulseData.ParseHsguruArchetypes(FixtureServer.Json("hsguru-archetypes-wild.json")), a => a.Name == "Yogg Shaman").Decks[0];
        var deck = DeckString.TryDecode(list.Code)!;
        var source = new MulliganGuideSource(Data(server));
        var changed = new TaskCompletionSource();
        source.Changed += () => changed.TrySetResult();

        Assert.Null(source.For("SHAMAN", deck, "FT_WILD", CardDb.Empty));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var guide = source.For("SHAMAN", deck, "FT_WILD", CardDb.Empty);

        Assert.NotNull(guide);
        Assert.Equal("Yogg Shaman", guide.Name);
        Assert.Equal(ApiConstructedMeta.HsguruMulliganSource, guide.Source);
        Assert.NotNull(guide.BaselineWinRate);
        Assert.Equal(11, guide.Matchups.Count);
        Assert.Null(source.For("SHAMAN", deck, "FT_CLASSIC", CardDb.Empty));
    }

    [Fact]
    public async Task Coin_and_class_slices_load_once_the_mulligan_knows_them()
    {
        var server = FixtureServer.Standard();
        foreach (var (query, file) in new[]
                 {
                     ("rank=legend&coin=any_player&opponent_class=any", "legend-any_player-any"),
                     ("rank=legend&coin=on_coin&opponent_class=any", "legend-on_coin-any"),
                     ("rank=legend&coin=any_player&opponent_class=WARLOCK", "legend-any_player-warlock"),
                 })
        {
            server.Route($"v1/hsguru/mulligan?archetype=Zoolock&format=standard&{query}", FixtureServer.Text($"hsguru-mulligan-zoolock-{file}.json"));
        }

        var list = Assert.Single(HearthpulseData.ParseHsguruArchetypes(FixtureServer.Json("hsguru-archetypes-standard.json")), a => a.Name == "Zoolock").Decks[0];
        var deck = DeckString.TryDecode(list.Code)!;
        var source = new MulliganGuideSource(Data(server));
        var changed = new SemaphoreSlim(0);
        source.Changed += () => changed.Release();

        // Before the mulligan: the guide with the all-games slice.
        Assert.Null(source.For("WARLOCK", deck, "FT_STANDARD", CardDb.Empty));
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        var guide = source.For("WARLOCK", deck, "FT_STANDARD", CardDb.Empty)!;
        Assert.Equal("Zoolock", guide.HsguruArchetype);
        var all = Assert.Single(guide.Slices);
        Assert.Equal(("legend", "any_player", "any", 51613), (all.Rank, all.Coin, all.OpponentClass, all.Games));

        // The mulligan on the coin against a Warlock: those slices follow, once.
        Assert.Same(guide, source.For("WARLOCK", deck, "FT_STANDARD", CardDb.Empty, "WARLOCK", false));
        Assert.True(await changed.WaitAsync(TimeSpan.FromSeconds(10)));
        guide = source.For("WARLOCK", deck, "FT_STANDARD", CardDb.Empty, "WARLOCK", false)!;
        Assert.Equal(3, guide.Slices.Count);
        Assert.Contains(guide.Slices, s => s is { Coin: "on_coin", Games: 25195 });
        Assert.Contains(guide.Slices, s => s.OpponentClass == "WARLOCK");
        var asked = server.Requests.Count(r => r.StartsWith("v1/hsguru/mulligan", StringComparison.Ordinal));
        Assert.Equal(6, asked); // Legend and all ranks for each of the three; all ranks are 404s here.

        // Cached on disk: a second lookup asks nothing.
        var again = await MulliganGuideSource.LoadGuideAsync(Data(server), "standard", "WARLOCK", deck.Cards, CardDb.Empty, "WARLOCK", false);
        Assert.Equal(3, again!.Slices.Count);
        Assert.Equal(asked, server.Requests.Count(r => r.StartsWith("v1/hsguru/mulligan", StringComparison.Ordinal)));

        var hand = new[] { new OfferedCard(1, "BE_085", "Ritual of Summoning", 1), new OfferedCard(2, "BE_151", "Vaultkeeper", 5) };
        var view = MulliganGuide.Compute(again, hand, null, "WARLOCK", false, again.Name)!;
        Assert.False(view.GoingFirst);
        Assert.Equal("WARLOCK", view.OpponentClass);
        Assert.Equal("legend", view.SliceRank);
    }

    [Fact]
    public async Task An_hsreplay_archetype_without_matchups_takes_hsguru_class_results()
    {
        var server = FixtureServer.Standard();
        server.Route("api/db/archetypes/56/matchups", """{"matchups": []}""");
        server.Route("v1/hsguru/class-matchups?archetype=Quest%20Priest&format=standard&rank=legend", FixtureServer.Text("hsguru-class-matchups-quest-priest-legend.json"));
        var list = Assert.Single(HearthpulseData.ParseHsguruArchetypes(FixtureServer.Json("hsguru-archetypes-standard.json")), a => a.Name == "Quest Priest").Decks[0];
        var deck = DeckString.TryDecode(list.Code)!;

        var guide = await MulliganGuideSource.LoadGuideAsync(Data(server), "standard", "PRIEST", deck.Cards, CardDb.Empty);

        Assert.NotNull(guide);
        Assert.Equal((56, "Quest Priest"), (guide.Id, guide.HsguruArchetype));
        Assert.Equal(11, guide.Matchups.Count);
        var warlock = Assert.Single(guide.Matchups, m => m.OpponentClass == "WARLOCK");
        Assert.Equal(7403, warlock.Games);
        Assert.Equal(0.474, warlock.WinRate, 6);
        var mean = guide.Matchups.Sum(m => m.WinRate * m.Games) / guide.Matchups.Sum(m => m.Games);
        Assert.Equal(0.474 - mean, MulliganGuide.ClassShift(guide.Matchups, "WARLOCK")!.Value, 6);
        Assert.Single(server.Requests, r => r.StartsWith("v1/hsguru/class-matchups", StringComparison.Ordinal));

        // Cached on disk: a second lookup asks nothing.
        await MulliganGuideSource.LoadGuideAsync(Data(server), "standard", "PRIEST", deck.Cards, CardDb.Empty);
        Assert.Single(server.Requests, r => r.StartsWith("v1/hsguru/class-matchups", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hsguru_class_results_are_asked_for_only_when_hsreplay_has_no_matchups()
    {
        var list = Assert.Single(HearthpulseData.ParseHsguruArchetypes(FixtureServer.Json("hsguru-archetypes-standard.json")), a => a.Name == "Quest Priest").Decks[0];
        var deck = DeckString.TryDecode(list.Code)!;

        // HSReplay's own matchups stay.
        var withMatchups = FixtureServer.Standard();
        var guide = await MulliganGuideSource.LoadGuideAsync(Data(withMatchups), "standard", "PRIEST", deck.Cards, CardDb.Empty);
        Assert.Equal(HearthpulseData.ParseMatchups(FixtureServer.Json("56-matchups.json")).Count, guide!.Matchups.Count);
        Assert.DoesNotContain(withMatchups.Requests, r => r.StartsWith("v1/hsguru/class-matchups", StringComparison.Ordinal));

        // Neither source has them (404 at Legend and all ranks): the guide still loads, without a class shift, and the
        // 404s are remembered.
        var neither = FixtureServer.Standard();
        neither.Route("api/db/archetypes/56/matchups", """{"matchups": []}""");
        guide = await MulliganGuideSource.LoadGuideAsync(Data(neither, "neither"), "standard", "PRIEST", deck.Cards, CardDb.Empty);
        Assert.NotNull(guide);
        Assert.Empty(guide.Matchups);
        Assert.Equal(2, neither.Requests.Count(r => r.StartsWith("v1/hsguru/class-matchups", StringComparison.Ordinal)));
        await MulliganGuideSource.LoadGuideAsync(Data(neither, "neither"), "standard", "PRIEST", deck.Cards, CardDb.Empty);
        Assert.Equal(2, neither.Requests.Count(r => r.StartsWith("v1/hsguru/class-matchups", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Hsguru_class_results_need_a_safe_rank_and_read_upper_case_fractions()
    {
        var server = new FixtureServer();

        Assert.Empty(await Data(server).LoadHsguruClassMatchupsAsync("Quest Priest", "standard", "../legend"));
        Assert.Empty(server.Requests);

        var rows = HearthpulseData.ParseHsguruClassMatchups(System.Text.Json.JsonDocument.Parse("""
            {"games": 900, "winrate": 55.0, "rows": [
              {"opponent_class": "demonhunter", "winrate": 60.5, "games": 600, "share_pct": 66.7},
              {"opponent_class": "mage", "winrate": 45.0, "games": 0, "share_pct": 0},
              {"winrate": 50.0, "games": 300}
            ]}
            """).RootElement);
        var row = Assert.Single(rows);
        Assert.Equal(("DEMONHUNTER", 600), (row.OpponentClass, row.Games));
        Assert.Equal(0.605, row.WinRate, 6);
    }

    [Fact]
    public async Task The_guide_source_starts_over_for_another_deck_and_ignores_a_late_answer()
    {
        var server = FixtureServer.Standard();
        var priestList = HearthpulseData.ParseArchetypeDecks(FixtureServer.Json("56-decks.json"))[0].Cards;
        var deck = new DeckDefinition("Quest", 1, string.Empty, "Standard", [], priestList);
        var other = deck with { Cards = new Dictionary<int, int>(priestList) { [1] = 1 } };
        var source = new MulliganGuideSource(Data(server));
        var changed = 0;
        source.Changed += () => Interlocked.Increment(ref changed);

        Assert.Null(source.For("PRIEST", deck, "FT_STANDARD", CardDb.Empty));
        Assert.Null(source.For("PRIEST", other, "FT_STANDARD", CardDb.Empty));

        // Only the second deck's lookup may land.
        for (var i = 0; i < 100 && source.For("PRIEST", other, "FT_STANDARD", CardDb.Empty) is null; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal("Quest Priest", source.For("PRIEST", other, "FT_STANDARD", CardDb.Empty)?.Name);
        Assert.Equal(1, Volatile.Read(ref changed));
    }

    [Fact]
    public async Task A_missing_answer_is_remembered_and_odd_periods_never_reach_the_server()
    {
        var server = new FixtureServer();
        var data = Data(server);

        Assert.Null(await data.LoadHsguruAnalysisAsync("Nobody", "standard"));
        Assert.Null(await data.LoadHsguruAnalysisAsync("Nobody", "standard"));
        Assert.Null(await data.LoadHsguruMetaAsync("standard", "legend", "../escape"));

        Assert.Single(server.Requests);
        Assert.DoesNotContain(Directory.EnumerateFiles(_cache), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Hsguru_rows_go_to_the_reprint_the_deck_holds()
    {
        var cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
            [
              {"id": "CS1_130", "dbfId": 279, "name": "Holy Smite", "cost": 1},
              {"id": "CORE_CS1_130", "dbfId": 69636, "name": "Holy Smite", "cost": 1},
              {"id": "EX1_197", "dbfId": 1361, "name": "Shadow Word: Ruin", "cost": 4}
            ]
            """)));
        var analysis = new HsguruAnalysis(
            [new HsguruCardStat("CS1_130", 279, 200, 120, 0.05, 0.02), new HsguruCardStat("EX1_197", 1361, 100, 10, null, -0.03)],
            [new GuideMatchup("MAGE", 300, 0.5), new GuideMatchup("ROGUE", 100, 0.6)]);

        var rows = ApiConstructedMeta.GuideCardsOf(analysis, cards, [69636]);

        Assert.Equal(0.525, analysis.WinRate!.Value, 6);
        var smite = Assert.Single(rows, r => r.CardId.EndsWith("CS1_130", StringComparison.Ordinal));
        Assert.Equal("CORE_CS1_130", smite.CardId);
        Assert.Equal(0.6, smite.KeepRate, 6);
        Assert.Equal(0.575, smite.OpeningHandWinRate!.Value, 6);
        Assert.Equal(1, smite.Cost);
        Assert.Null(rows.Single(r => r.CardId == "EX1_197").OpeningHandWinRate);
    }

    [Fact]
    public async Task A_slice_listing_original_and_reprint_keeps_the_row_with_more_games()
    {
        var cards = CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes("""
            [
              {"id": "CS1_130", "dbfId": 279, "name": "Holy Smite", "cost": 1},
              {"id": "CORE_CS1_130", "dbfId": 69636, "name": "Holy Smite", "cost": 1}
            ]
            """)));
        var server = new FixtureServer();
        server.Route("v1/hsguru/mulligan?archetype=Smite%20Priest&format=standard&rank=legend&coin=any_player&opponent_class=any", """
            {"data": {"rank": "legend", "coin": "any_player", "opponent_class": "any", "games": 9000, "rows": [
              {"card_id": "CS1_130", "dbf_id": 279, "mulligan_count": 300, "mulligan_impact": 1.0, "kept_count": 100, "kept_impact": 2.0, "drawn_count": 900, "drawn_impact": 0.5},
              {"card_id": "CORE_CS1_130", "dbf_id": 69636, "mulligan_count": 2000, "mulligan_impact": -1.0, "kept_count": 900, "kept_impact": -2.0, "drawn_count": 5000, "drawn_impact": -0.5}
            ]}}
            """);
        var guide = new GuideArchetype(1, "Smite Priest", "PRIEST", [], [], "test") { HsguruArchetype = "Smite Priest" };

        var loaded = await MulliganGuideSource.WithSlicesAsync(Data(server), guide, "standard", new Dictionary<int, int> { [69636] = 2 }, cards, null, null);

        var slice = Assert.Single(loaded.Slices);
        var smite = Assert.Single(slice.Cards);
        Assert.Equal(("CORE_CS1_130", 2000), (smite.CardId, smite.Offered));
    }

    [Fact]
    public void The_class_pool_counts_a_shared_hsreplay_archetype_once_and_uses_its_baseline()
    {
        MetaArchetype Archetype(int id, string name, int? hsreplay, double baseline) =>
            new(id, name, name, "PRIEST", 1000, 500, 0.6, 0.1, 0, 0, 0, 0, [], [new MetaMulliganCard("A", 100, 0.5, 80, baseline + 0.02)])
            {
                MulliganBaseline = baseline,
                MulliganArchetypeId = hsreplay,
            };
        var meta = new ConstructedMeta("standard", "all", "past_week", DateTime.UtcNow, 3000,
            [Archetype(1, "One", 56, 0.5), Archetype(2, "Two", 56, 0.5), Archetype(3, "Three", null, 0.48),
             Archetype(4, "Empty", null, 0.5) with { Mulligan = [] }], []);

        var pooled = Mulligan.FromMeta(meta, CardDb.Empty, "PRIEST", null);

        Assert.NotNull(pooled);
        Assert.Equal(200, pooled.Cards.Single().Offered);
        Assert.Null(pooled.BaselineWinRate);
        Assert.Equal(meta.Source, pooled.Source);
    }

    [Fact]
    public void Neutral_lists_do_not_make_a_class()
    {
        var neutral = new HsguruDeck("x", "Neutral", 10, 0.5, DateTime.MinValue);

        Assert.Null(ApiConstructedMeta.ClassOf("Odd Thing", [neutral]));
        Assert.Equal("ROGUE", ApiConstructedMeta.ClassOf("Mech Rogue", [neutral]));
    }

    [Fact]
    public void A_second_copy_without_data_says_so()
    {
        var guide = new GuideArchetype(1, "Test", "MAGE", [new GuideCard("A", 1, 1, 1000, 0.5, 1000, 0.5, 2)], [], "test");
        var hand = new[] { new OfferedCard(1, "B", "B", 3), new OfferedCard(2, "B", "B", 3), new OfferedCard(3, "A", "A", 1) };

        var view = MulliganGuide.Compute(guide, hand, null, null, null, "Test")!;

        Assert.Equal(new[] { MulliganCall.NoData, MulliganCall.NoData }, view.Cards.Take(2).Select(c => c.Call));
        Assert.Equal(0.5, view.CardBaseline, 6);
    }

    /// <param name="cache">A folder of its own under the scratch cache, for a second server in the same test.</param>
    private HearthpulseData Data(FixtureServer server, string? cache = null) =>
        new(new HearthpulseApi(new HttpClient(server), "https://api.test/"), cache is null ? _cache : Path.Combine(_cache, cache));

    private sealed class Collect(List<ConstructedMeta> into) : IProgress<ConstructedMeta>
    {
        public void Report(ConstructedMeta value) => into.Add(value);
    }

    /// <summary>Answers by path (and query fragments) with fixture files; anything else is a 404.</summary>
    [Fact]
    public async Task Standard_meta_carries_the_matchup_matrix_of_its_slice_with_the_rank_the_api_takes()
    {
        var server = FixtureServer.Standard();
        server.Route("v1/hsguru/matchups?format=standard", """
            {"data": {"total_games": 900, "archetypes": [
              {"archetype": "Quest Priest", "class": "PRIEST", "games": 300, "winrate": 48.0, "popularity_pct": 12.5},
              {"archetype": "Zoolock", "class": "WARLOCK", "games": 600, "winrate": 56.4, "popularity_pct": 18.9}],
             "matchups": [
              {"archetype": "Zoolock", "opponent": "Quest Priest", "winrate": 64.5, "games": 210},
              {"archetype": "Quest Priest", "opponent": "Zoolock", "winrate": 35.5, "games": 210}]}}
            """);

        var meta = await ConstructedMeta.LoadAsync(Data(server), CardDb.Empty, "standard", "top_100", "past_week", "enUS");

        Assert.NotNull(meta);
        Assert.Equal(["Zoolock", "Quest Priest"], meta.Matchups.Archetypes.Select(a => a.Name));
        Assert.Equal(900, meta.Matchups.Games);
        Assert.Equal(0.645, meta.Matchups.Cell("zoolock", "QUEST PRIEST")!.WinRate, 3);
        Assert.Null(meta.Matchups.Cell("Zoolock", "Zoolock"));
        // The meta's narrower ranks read the matrix's top-Legend slice (the endpoint has no top 100).
        Assert.Contains(server.Requests, r => r.Contains("v1/hsguru/matchups", StringComparison.Ordinal) && r.Contains("rank=top_legend", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_missing_matchup_matrix_leaves_the_tier_list_whole()
    {
        var meta = await ConstructedMeta.LoadAsync(Data(FixtureServer.Standard()), CardDb.Empty, "standard", "legend", "past_week", "enUS");

        Assert.NotNull(meta);
        Assert.Equal(20, meta.Archetypes.Count);
        Assert.Empty(meta.Matchups.Archetypes);
    }

    private sealed class FixtureServer : HttpMessageHandler
    {
        private readonly List<(string Fragment, string Body)> _routes = [];

        public List<string> Requests { get; } = [];

        public static System.Text.Json.JsonElement Json(string name)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mulligan", name)));
            return doc.RootElement.Clone();
        }

        public static FixtureServer Standard()
        {
            var server = new FixtureServer();
            server.Route("v1/hsguru/meta?format=standard", Fixture("hsguru-meta-standard-legend-past_week.json"));
            server.Route("v1/hsguru/archetypes?format=standard", "{\"data\": " + Fixture("hsguru-archetypes-standard.json") + "}");
            server.Route("analysis?archetype=Zoolock&format=standard", Fixture("hsguru-analysis-standard-zoolock.json"));
            server.Route("api/db/archetypes?", Fixture("archetypes.json"));
            foreach (var id in new[] { 56, 396, 826, 852, 867, 880 })
            {
                server.Route($"api/db/archetypes/{id}/decks", Fixture($"{id}-decks.json"));
                server.Route($"api/db/archetypes/{id}/mulligan", Fixture($"{id}-mulligan.json"));
                server.Route($"api/db/archetypes/{id}/matchups", Fixture($"{id}-matchups.json"));
            }

            return server;
        }

        public static FixtureServer Wild()
        {
            var server = new FixtureServer();
            server.Route("v1/hsguru/meta?format=wild", Fixture("hsguru-meta-wild-all-past_week.json"));
            server.Route("v1/hsguru/archetypes?format=wild", "{\"data\": " + Fixture("hsguru-archetypes-wild.json") + "}");
            server.Route("analysis?archetype=Yogg%20Shaman&format=wild", Fixture("hsguru-analysis-wild-yogg-shaman.json"));
            return server;
        }

        /// <summary>Later routes win, so a test can override a fixture.</summary>
        public void Route(string fragment, string body) => _routes.Insert(0, (fragment, body));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery.TrimStart('/');
            lock (Requests)
            {
                Requests.Add(path);
            }

            var route = _routes.FirstOrDefault(r => path.Contains(r.Fragment, StringComparison.Ordinal));
            return Task.FromResult(route.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"detail\": \"Not Found\"}", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(route.Body, Encoding.UTF8, "application/json") });
        }

        public static string Text(string name) => Fixture(name);

        private static string Fixture(string name) => System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mulligan", name));
    }
}
