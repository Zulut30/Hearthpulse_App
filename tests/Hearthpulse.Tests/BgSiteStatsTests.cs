using System.Net;
using System.Text;
using System.Text.Json;
using Hearthpulse.Core.Battlegrounds.Site;
using Microsoft.Extensions.Time.Testing;

namespace Hearthpulse.Tests;

/// <summary>
/// The Battlegrounds lists of the site's open service (docs/bg-site-stats.md): the records parse from real answers
/// (Fixtures/BgSite, trimmed), the client asks four addresses of one host with nothing about the player, keeps the
/// answers for six hours, survives a failing address and a malformed answer, and refuses Firestone lists by their
/// source. No network: a fake handler answers.
/// </summary>
public sealed class BgSiteStatsTests : IDisposable
{
    private const string TierLists = "/api/bg/tier-lists";
    private const string Heroes = "/api/bg/heroes";
    private const string MinionStats = "/api/bg/library/minion-stats";
    private const string Comps = "/api/bg-comps";

    private readonly string _cache = TestDataFolder.Scratch("hp-bgsite");

    public void Dispose() => TestDataFolder.TryDelete(_cache);

    // AC17

    [Fact]
    public async Task Every_tier_holds_as_many_entries_as_the_answer_counts()
    {
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;

        using var answer = JsonDocument.Parse(Fixture("tier-lists.json"));
        foreach (var list in new[] { stats.Heroes!, stats.Minions!, stats.Trinkets! })
        {
            var counts = answer.RootElement.GetProperty("lists").GetProperty(list.Kind.ToString().ToLowerInvariant()).GetProperty("tierCounts");
            foreach (var tier in new[] { "S", "A", "B", "C", "D" })
            {
                Assert.Equal(counts.GetProperty(tier).GetInt32(), list.Entries.Count(e => e.Tier == tier));
            }

            Assert.Equal(["S", "A", "B", "C", "D"], list.AvailableTiers);
            // The service's order: tier S first, D last.
            Assert.Equal(list.Entries.OrderBy(e => "SABCD".IndexOf(e.Tier, StringComparison.Ordinal)).Select(e => e.CardId), list.Entries.Select(e => e.CardId));
        }

        Assert.Equal(12, stats.Heroes!.Entries.Count);
        Assert.Equal(16, stats.Minions!.Entries.Count);
        Assert.Equal(15, stats.Trinkets!.Entries.Count);
    }

    [Fact]
    public async Task A_hero_takes_its_card_id_and_place_shares_from_the_heroes_answer()
    {
        var heroes = (await Client(FakeSite.Real()).LoadAsync())!.Heroes!;

        Assert.Equal(BgSiteList.Heroes, heroes.Kind);
        Assert.Equal("hsreplay", heroes.Source);
        Assert.Equal(new DateTime(2026, 11, 9, 17, 1, 9, DateTimeKind.Utc), heroes.FetchedAt, TimeSpan.FromSeconds(1));
        Assert.Equal(DateTimeKind.Utc, heroes.FetchedAt.Kind);
        Assert.Equal("TOP_50_PERCENT", heroes.Mmr);
        Assert.Equal("CURRENT_BATTLEGROUNDS_PATCH", heroes.TimeRange);
        Assert.Null(heroes.GamesNote);

        var hero = heroes.Entries[0];
        Assert.Equal("BG36_HERO_105", hero.CardId);
        Assert.Equal(132608, hero.DbfId);
        Assert.Equal("A", hero.Tier);
        Assert.Equal("Повелитель кошмаров Ксавий", hero.NameRu);
        Assert.Equal(string.Empty, hero.NameEn);
        Assert.Equal(0, hero.TavernTier);
        Assert.Equal(4.24, hero.AveragePlacement!.Value, 3);
        Assert.Equal(0.781, hero.PickRate!.Value, 4);
        Assert.Equal(0.1722, hero.FirstRate!.Value, 4);
        Assert.Equal(0.5407, hero.Top4Rate!.Value, 4);
        Assert.Null(hero.Games);
        Assert.Null(hero.TrinketSize);
        // A hero of the heroes answer that is in no tier does not appear.
        Assert.DoesNotContain(heroes.Entries, e => e.CardId == "TB_BaconShop_HERO_16");
    }

    [Fact]
    public async Task A_minion_entry_reads_its_tavern_tier_popularity_and_games()
    {
        var minions = (await Client(FakeSite.Real()).LoadAsync())!.Minions!;

        Assert.Equal("hsreplay", minions.Source);
        Assert.Null(minions.Mmr);
        var minion = minions.Entries[0];
        Assert.Equal(("BG36_341", 132929, "S", "Опытный бандит", "Veteran Brigand", 6), (minion.CardId, minion.DbfId, minion.Tier, minion.NameRu, minion.NameEn, minion.TavernTier));
        Assert.Equal(0.0608, minion.PickRate!.Value, 4);
        Assert.Equal(36014, minion.Games);
        Assert.Null(minion.AveragePlacement);
        Assert.Null(minion.Top4Rate);
        Assert.Null(minion.FirstRate);

        // No Russian name in the answer: the English one stands in.
        var unnamed = Assert.Single(minions.Entries, e => e.CardId == "BG33_890t");
        Assert.Equal("Magicfin Apprentice", unnamed.NameRu);
    }

    [Fact]
    public async Task A_trinket_entry_reads_its_size_cost_and_the_lists_slice()
    {
        var trinkets = (await Client(FakeSite.Real()).LoadAsync())!.Trinkets!;

        Assert.Equal("TOP_1_PERCENT", trinkets.Mmr);
        Assert.Equal("LAST_7_DAYS", trinkets.TimeRange);
        Assert.Contains("minimum integer sample", trinkets.GamesNote, StringComparison.Ordinal);
        var trinket = Assert.Single(trinkets.Entries, e => e.CardId == "BG35_MagicItem_754");
        Assert.Equal(("S", "Наклейка с драмамурлоком", "Dramaloc Sticker", "LARGE", 6, 10), (trinket.Tier, trinket.NameRu, trinket.NameEn, trinket.TrinketSize, trinket.Cost, trinket.Games));
        Assert.Equal(2.5, trinket.AveragePlacement!.Value, 3);
        Assert.Equal(0.131, trinket.PickRate!.Value, 4);
        Assert.Equal(0, trinket.TavernTier);
    }

    [Fact]
    public async Task Minion_numbers_come_by_card_id_with_shares_and_the_run()
    {
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;

        Assert.Equal(19, stats.MinionStats.Count);
        var minion = stats.MinionStats["BG36_341"];
        Assert.Equal((132929, 6, false, 28615, 443926), (minion.DbfId, minion.TavernTier, minion.IsSpell, minion.GamesWith, minion.GamesWithout));
        Assert.Equal(0.6, minion.Impact!.Value, 3);
        Assert.Equal(0.6076, minion.CombatWinRate!.Value, 4);
        Assert.Equal(0.0606, minion.Popularity!.Value, 4);
        Assert.Equal(2.76, minion.AvgPlacementWith!.Value, 3);
        Assert.Equal(3.36, minion.AvgPlacementWithout!.Value, 3);

        // A minion the server has no numbers for keeps its row, with nulls.
        var empty = stats.MinionStats["BG26_137"];
        Assert.Null(empty.Impact);
        Assert.Null(empty.CombatWinRate);
        Assert.Null(empty.GamesWith);
        Assert.Null(empty.AvgPlacementWith);

        Assert.Equal(new BgSiteRun("TOP_50_PERCENT", "CURRENT_BATTLEGROUNDS_PATCH", stats.Run!.CompletedAt), stats.Run);
        Assert.Equal(new DateTime(2026, 11, 9, 2, 45, 10, DateTimeKind.Utc), stats.Run.CompletedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Comps_read_their_tier_cards_difficulty_and_tip()
    {
        var comps = (await Client(FakeSite.Real()).LoadAsync())!.Comps;

        Assert.Equal(["hsreplay-76", "hsreplay-95", "hsreplay-3"], comps.Select(c => c.Id));
        var comp = comps[0];
        Assert.Equal("Свинобразы - APM-усиление таверны", comp.NameRu);
        Assert.Equal(string.Empty, comp.NameEn);
        Assert.Equal("S", comp.Tier);
        Assert.Equal(["BG28_633", "BG35_883", "BG_LOE_077"], comp.CoreCardIds);
        Assert.Equal("easy", comp.Difficulty);
        Assert.Equal("medium", comps[1].Difficulty);
        Assert.Equal("Разыгрывайте заклинания, чтобы поглощать усиленную таверну.", comp.Tip);
        Assert.Empty(comp.Tribes);
        // The service publishes no placement and no sample for these comps.
        Assert.Null(comp.AveragePlacement);
        Assert.Null(comp.Games);
    }

    [Fact]
    public async Task Updated_is_the_latest_fetch_time_of_the_loaded_parts()
    {
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;

        Assert.Equal(stats.Trinkets!.FetchedAt, stats.Updated);
        Assert.True(stats.Updated >= stats.Heroes!.FetchedAt && stats.Updated >= stats.Minions!.FetchedAt);
    }

    // AC18

    [Fact]
    public async Task Within_six_hours_the_cache_answers_and_after_them_the_service_is_asked_again()
    {
        var site = FakeSite.Real();
        var client = Client(site);

        var first = (await client.LoadAsync())!;
        Assert.Equal(4, site.Requests.Count);
        foreach (var part in new[] { "tier-lists", "heroes", "minion-stats", "comps" })
        {
            Assert.True(File.Exists(Path.Combine(_cache, $"bgsite-{part}.json")), part);
        }

        var second = (await Client(site).LoadAsync())!;
        Assert.Equal(4, site.Requests.Count);
        Assert.Equal(first.Heroes!.Entries, second.Heroes!.Entries);
        Assert.Equal(first.Updated, second.Updated);

        client.Clock = () => DateTime.UtcNow + BgSiteClient.MaxAge - TimeSpan.FromMinutes(5);
        await client.LoadAsync();
        Assert.Equal(4, site.Requests.Count);

        client.Clock = () => DateTime.UtcNow + BgSiteClient.MaxAge + TimeSpan.FromMinutes(5);
        await client.LoadAsync();
        Assert.Equal(8, site.Requests.Count);
        Assert.Empty(Directory.GetFiles(_cache, "*.tmp"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task When_the_service_fails_the_old_files_answer_with_their_own_time(HttpStatusCode? failure)
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;

        var down = FakeSite.Down(failure);
        var client = Client(down);
        client.Clock = () => DateTime.UtcNow + TimeSpan.FromDays(3);
        var stale = (await client.LoadAsync())!;

        Assert.Equal(4, down.Requests.Count);
        Assert.Equal(loaded.Updated, stale.Updated);
        Assert.Equal(loaded.Heroes!.Entries, stale.Heroes!.Entries);
        Assert.Equal(loaded.Trinkets!.Entries, stale.Trinkets!.Entries);
        Assert.Equal(loaded.MinionStats.Count, stale.MinionStats.Count);
        Assert.Equal(loaded.Comps.Select(c => c.Id), stale.Comps.Select(c => c.Id));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(null)]
    public async Task When_the_service_fails_and_nothing_is_cached_there_are_no_stats(HttpStatusCode? failure)
    {
        var down = FakeSite.Down(failure);

        Assert.Null(await Client(down).LoadAsync());
        Assert.Equal(4, down.Requests.Count);
        Assert.False(Directory.Exists(_cache) && Directory.GetFiles(_cache).Length > 0);
    }

    [Fact]
    public async Task An_unreadable_cache_file_is_asked_for_again()
    {
        var site = FakeSite.Real();
        await Client(site).LoadAsync();
        await File.WriteAllBytesAsync(Path.Combine(_cache, "bgsite-comps.json"), new byte[64]);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Equal(5, site.Requests.Count);
        Assert.Equal(3, stats.Comps.Count);
    }

    // AC19

    [Theory]
    [InlineData(TierLists)]
    [InlineData(Heroes)]
    [InlineData(MinionStats)]
    [InlineData(Comps)]
    public async Task One_failing_address_leaves_the_other_parts_loaded(string failing)
    {
        var site = FakeSite.Real();
        site.Fail(failing, HttpStatusCode.InternalServerError);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Equal(4, site.Requests.Count);
        AssertPartsExcept(stats, failing);
        Assert.Contains("HTTP 500", TestDataFolder.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_that_does_not_answer_in_time_leaves_the_other_parts_loaded()
    {
        var time = new FakeTimeProvider();
        var site = FakeSite.Real();
        site.Hang(MinionStats);
        var client = new BgSiteClient(new HttpClient(site), cacheDirectory: _cache) { Time = time };

        var load = client.LoadAsync();
        await site.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(BgSiteClient.Timeout + TimeSpan.FromSeconds(1));
        var stats = (await load.WaitAsync(TimeSpan.FromSeconds(30)))!;

        AssertPartsExcept(stats, MinionStats);
        Assert.Contains("Battlegrounds site minion-stats: no answer in 20 s", TestDataFolder.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_load_does_not_throw()
    {
        var site = FakeSite.Real();
        site.Hang(Comps);
        using var cancel = new CancellationTokenSource();

        var load = Client(site).LoadAsync(cancel.Token);
        await site.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        var stats = await load.WaitAsync(TimeSpan.FromSeconds(30));

        // What had already answered may be there; nothing is thrown either way.
        Assert.Empty(stats?.Comps ?? []);
    }

    // AC20

    [Theory]
    [InlineData("{\"lists\": {\"minions\": ")]
    [InlineData("<html>maintenance</html>")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public async Task A_broken_answer_drops_its_part_only(string body)
    {
        var site = FakeSite.Real();
        site.Answer(TierLists, body);

        var stats = (await Client(site).LoadAsync())!;

        AssertPartsExcept(stats, TierLists);
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-tier-lists.json")));
        Assert.Contains("Battlegrounds site tier-lists: ", TestDataFolder.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_tier_letter_is_dropped_with_a_log_line()
    {
        var site = FakeSite.Real();
        site.Answer(TierLists, """
            {"lists": {"minions": {"source": "hsreplay", "fetchedAt": "2026-11-09T17:01:09+00:00",
              "tiers": {"S": [{"id": "BG_A", "name": "A", "tavernTier": 1}], "Z9": [{"id": "BG_Z", "name": "Z", "tavernTier": 2}], "D": [{"id": "BG_D", "name": "D", "tavernTier": 9}]}}}}
            """);

        var minions = (await Client(site).LoadAsync())!.Minions!;

        Assert.Equal(["BG_A", "BG_D"], minions.Entries.Select(e => e.CardId));
        Assert.Equal(["S", "D"], minions.AvailableTiers);
        // A tavern tier outside 1..7 is not a tavern tier.
        Assert.Equal(0, minions.Entries[1].TavernTier);
        Assert.Contains("Battlegrounds site minions: unknown tier \"Z9\" dropped", TestDataFolder.ReadLog(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_without_a_card_id_are_dropped_with_a_log_line()
    {
        var site = FakeSite.Real();
        site.Answer(TierLists, """
            {"lists": {
              "minions": {"source": "hsreplay", "tiers": {"A": [{"name": "No id"}, {"id": "../../evil", "name": "Bad id"}, {"id": 5}, "text", {"id": "BG_OK", "name": "Ok", "popularity": "250%", "games": -4}]}},
              "trinkets": {"source": "hsreplay", "tiers": {"B": [{"id": "", "name": "Empty id"}, {"id": "BG_T", "name": "T", "avgPlacement": 11, "size": "HUGE"}]}},
              "heroes": {"source": "hsreplay", "tiers": {"C": [{"hero": "Нет в ответе героев", "dbfId": 1}, {"hero": "Без dbf"}, {"hero": "Есть", "dbfId": 132608}]}}}}
            """);
        site.Answer(MinionStats, """
            {"latest_run": {"source": "hsreplay_battlegrounds_minions"}, "minions": [{"dbf_id": 1, "impact": 1}, {"card_id": "BG_OK", "impact": "NaN", "combat_winrate": 101}]}
            """);

        var stats = (await Client(site).LoadAsync())!;

        var minion = Assert.Single(stats.Minions!.Entries);
        Assert.Equal("BG_OK", minion.CardId);
        // Numbers that cannot be what they claim are left out, not shown.
        Assert.Null(minion.PickRate);
        Assert.Null(minion.Games);
        var trinket = Assert.Single(stats.Trinkets!.Entries);
        Assert.Equal("BG_T", trinket.CardId);
        Assert.Null(trinket.AveragePlacement);
        Assert.Null(trinket.TrinketSize);
        Assert.Equal("BG36_HERO_105", Assert.Single(stats.Heroes!.Entries).CardId);
        var stat = Assert.Single(stats.MinionStats).Value;
        Assert.Null(stat.Impact);
        Assert.Null(stat.CombatWinRate);

        var log = TestDataFolder.ReadLog();
        Assert.Contains("Battlegrounds site minions: 4 entries without a card id dropped", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site trinkets: 1 entries without a card id dropped", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site heroes: 2 entries without a card id dropped", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site minion stats: 1 rows without a card id dropped", log, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_over_eight_megabytes_is_dropped(bool declaresLength)
    {
        var site = FakeSite.Real();
        site.Answer(Comps, "{\"source\": \"HSReplay\", \"comps\": [], \"pad\": \"" + new string('x', BgSiteClient.MaxBytes) + "\"}", declaresLength);

        var stats = (await Client(site).LoadAsync())!;

        AssertPartsExcept(stats, Comps);
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-comps.json")));
        Assert.Contains(declaresLength ? "bytes ignored" : "Battlegrounds site comps: answer over 8 MB ignored", TestDataFolder.ReadLog(), StringComparison.Ordinal);
    }

    // AC21

    [Fact]
    public async Task Only_plain_gets_to_the_service_with_nothing_about_the_player()
    {
        var site = FakeSite.Real();

        await Client(site).LoadAsync();

        Assert.True(site.Requests.Count <= 8);
        Assert.Equal(
            new[] { TierLists, Heroes, MinionStats, Comps }.Order(StringComparer.Ordinal),
            site.Requests.Select(r => r.Uri.PathAndQuery).Order(StringComparer.Ordinal));
        foreach (var request in site.Requests)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal("bg.hs-manacost.ru", request.Uri.Host);
            Assert.False(request.HasBody);
            Assert.Equal(["Accept"], request.Headers);
        }
    }

    [Theory]
    [InlineData("http://bg.hs-manacost.ru/")]
    [InlineData("file:///c:/data/")]
    [InlineData("not an address")]
    public async Task An_address_that_is_not_https_is_not_used(string baseUrl)
    {
        var site = FakeSite.Real();

        await new BgSiteClient(new HttpClient(site), baseUrl, _cache).LoadAsync();

        Assert.All(site.Requests, r => Assert.StartsWith(BgSiteClient.DefaultBaseUrl, r.Uri.AbsoluteUri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Another_https_address_is_asked_under_its_own_path()
    {
        var site = FakeSite.Real();

        var stats = await new BgSiteClient(new HttpClient(site), "https://staging.example/bg", _cache).LoadAsync();

        Assert.NotNull(stats);
        Assert.All(site.Requests, r => Assert.StartsWith("https://staging.example/bg/api/", r.Uri.AbsoluteUri, StringComparison.Ordinal));
    }

    // AC22

    [Fact]
    public async Task Firestone_lists_and_comps_are_not_given_out()
    {
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;

        // The fixture's spells list and two of its comps name Firestone as their source.
        Assert.Null(stats.Spells);
        Assert.Empty(stats.SpellStats);
        Assert.DoesNotContain(stats.Comps, c => c.Id.StartsWith("firestone", StringComparison.OrdinalIgnoreCase));
        var log = TestDataFolder.ReadLog();
        Assert.Contains("Battlegrounds site tier-lists: list \"spells\" of source \"firestone\" is not used", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site tier-lists: list \"strategies\" of source \"firestone\" is not used", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site comps: 2 comps of a source that is not used dropped", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_data_is_not_kept_in_the_cache_either()
    {
        await Client(FakeSite.Real()).LoadAsync();

        using var lists = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-tier-lists.json")));
        Assert.Equal(["heroes", "minions", "trinkets"], lists.RootElement.GetProperty("lists").EnumerateObject().Select(l => l.Name));
        using var comps = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-comps.json")));
        Assert.All(comps.RootElement.GetProperty("comps").EnumerateArray(), c => Assert.Equal("HSReplay", c.GetProperty("source").GetString()));
        Assert.Equal(3, comps.RootElement.GetProperty("comps").GetArrayLength());
    }

    [Theory]
    [InlineData("firestone")]
    [InlineData("Firestone через api.kolodahearthstone.com")]
    [InlineData("static.ZeroToHeroes.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_list_is_refused_by_its_source_whatever_the_list(string? source)
    {
        var named = source is null ? string.Empty : $"\"source\": {JsonSerializer.Serialize(source)},";
        var site = FakeSite.Real();
        site.Answer(TierLists, $$"""
            {"lists": {
              "minions": { {{named}} "tiers": {"S": [ {"id": "BG_A", "name": "A"} ]} },
              "trinkets": {"source": "hsreplay", "tiers": {"S": [ {"id": "BG_T", "name": "T"} ]} } } }
            """);
        site.Answer(MinionStats, $$"""{"latest_run": { {{named}} "mmr_percentile": "TOP_50_PERCENT"}, "minions": [ {"card_id": "BG_A", "impact": 1} ]}""");
        site.Answer(Comps, $$"""{ {{named}} "comps": [ {"key": "a", "title": "A", "tier": "S"}, {"key": "b", "source": "HSReplay", "title": "B", "tier": "Q"} ]}""");
        site.Answer(Heroes, $$"""{"source_id": {{JsonSerializer.Serialize(source ?? string.Empty)}}, "view": {"heroes": [ {"id": "BG_H", "dbfId": 1} ]} }""");

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.Minions);
        Assert.Null(stats.Heroes);
        Assert.Empty(stats.MinionStats);
        Assert.Null(stats.Run);
        Assert.NotNull(stats.Trinkets);
        // A comp that names its own, allowed source stays; one that only has the list's refused source goes. An unknown
        // tier letter of a comp is no tier.
        var comp = Assert.Single(stats.Comps);
        Assert.Equal(("b", (string?)null), (comp.Id, comp.Tier));

        var cached = await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-tier-lists.json"));
        Assert.DoesNotContain("BG_A", cached, StringComparison.Ordinal);
        Assert.DoesNotContain("BG_A", await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-minion-stats.json")), StringComparison.Ordinal);
        Assert.DoesNotContain("BG_H", await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-heroes.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spells_list_of_an_allowed_source_would_be_given_out()
    {
        // The refusal is by source, not by list: when the server changes the origin, the list appears without an update.
        var site = FakeSite.Real();
        site.Answer(TierLists, """
            {"lists": {"spells": {"source": "hsreplay", "tiers": {"S": [{"id": "BG28_503", "name": "Fortify", "ruName": "Укрепление", "tavernTier": 1}]}}}}
            """);

        var spells = (await Client(site).LoadAsync())!.Spells!;

        Assert.Equal(BgSiteList.Spells, spells.Kind);
        Assert.Equal(("BG28_503", 1), (spells.Entries[0].CardId, spells.Entries[0].TavernTier));
    }

    // AC23

    [Fact]
    public async Task There_is_no_duos_slice()
    {
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;

        Assert.NotNull(stats.Heroes);
        Assert.Null(stats.HeroesDuos);
    }

    private BgSiteClient Client(FakeSite site) => new(new HttpClient(site), cacheDirectory: _cache);

    /// <summary>Every part is there except the ones the failing address feeds.</summary>
    private static void AssertPartsExcept(BgSiteStats stats, string failing)
    {
        Assert.Equal(failing == TierLists, stats.Minions is null);
        Assert.Equal(failing == TierLists, stats.Trinkets is null);
        // The heroes list needs both answers: the tiers and the card ids.
        Assert.Equal(failing is TierLists or Heroes, stats.Heroes is null);
        Assert.Equal(failing == MinionStats, stats.MinionStats.Count == 0);
        Assert.Equal(failing == MinionStats, stats.Run is null);
        Assert.Equal(failing == Comps, stats.Comps.Count == 0);
        Assert.NotEqual(default, stats.Updated);
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BgSite", name));

    private sealed record SeenRequest(HttpMethod Method, Uri Uri, IReadOnlyList<string> Headers, bool HasBody);

    /// <summary>The service: answers by path, can fail or hang one address, and records what it was asked.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body, bool DeclaresLength)> _answers = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hanging = new(StringComparer.Ordinal);
        private readonly List<SeenRequest> _requests = [];
        private HttpStatusCode? _down;
        private bool _offline;

        public IReadOnlyList<SeenRequest> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        /// <summary>Completes once a request to a hanging address is waiting.</summary>
        public TaskCompletionSource Hanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static FakeSite Real()
        {
            var site = new FakeSite();
            site.Answer(TierLists, Fixture("tier-lists.json"));
            site.Answer(Heroes, Fixture("heroes.json"));
            site.Answer(MinionStats, Fixture("minion-stats.json"));
            site.Answer(Comps, Fixture("comps.json"));
            return site;
        }

        /// <summary>Every address answers <paramref name="status"/>, or the connection fails when it is null.</summary>
        public static FakeSite Down(HttpStatusCode? status) => new() { _down = status, _offline = status is null };

        public void Answer(string path, string body, bool declaresLength = true) => _answers[path] = (HttpStatusCode.OK, body, declaresLength);

        public void Fail(string path, HttpStatusCode status) => _answers[path] = (status, "{\"detail\": \"down\"}", true);

        public void Hang(string path) => _hanging.Add(path);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            // Under a base address with a path of its own, the service's addresses follow it.
            var route = path[Math.Max(0, path.IndexOf("/api/", StringComparison.Ordinal))..];
            lock (_requests)
            {
                _requests.Add(new SeenRequest(request.Method, request.RequestUri, [.. request.Headers.Select(h => h.Key)], request.Content is not null));
            }

            if (_hanging.Contains(route))
            {
                Hanging.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (_offline)
            {
                throw new HttpRequestException("No such host is known.");
            }

            if (_down is { } down)
            {
                return new HttpResponseMessage(down) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }

            if (!_answers.TryGetValue(route, out var answer))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var bytes = Encoding.UTF8.GetBytes(answer.Body);
            HttpContent content = answer.DeclaresLength ? new ByteArrayContent(bytes) : new StreamContent(new UnsizedStream(bytes));
            return new HttpResponseMessage(answer.Status) { Content = content };
        }
    }

    /// <summary>A body whose length is not known ahead (a chunked answer).</summary>
    private sealed class UnsizedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
