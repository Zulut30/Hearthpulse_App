using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hearthpulse.Core.Battlegrounds.Site;
using Microsoft.Extensions.Time.Testing;

namespace Hearthpulse.Tests;

/// <summary>
/// The Battlegrounds lists of the site's open service (docs/bg-site-stats.md): the records parse from real answers
/// (Fixtures/BgSite, trimmed), the client asks five addresses of one host with nothing about the player, keeps the
/// answers for six hours, survives a failing address, an answer without data and a malformed one, and reads only lists
/// whose source is on the allow list. No network: a fake handler answers.
/// </summary>
public sealed class BgSiteStatsTests : IDisposable
{
    private const string TierLists = "/api/bg/tier-lists";
    private const string Heroes = "/api/bg/heroes";
    private const string Duos = "/api/bg/heroes?mode=duos&mmr=TOP_50_PERCENT";
    private const string MinionStats = "/api/bg/library/minion-stats";
    private const string Comps = "/api/bg-comps";

    /// <summary>Requests of one refresh; one less when the heroes answer was not fetched (no Duos slice is asked for then).</summary>
    private const int Refresh = 5;

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
        Assert.Equal(16, stats.Trinkets!.Entries.Count);
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
        Assert.Null(trinket.Race);
    }

    [Fact]
    public async Task A_trinket_listed_once_per_tribe_keeps_each_row_with_its_tribe()
    {
        var trinkets = (await Client(FakeSite.Real()).LoadAsync())!.Trinkets!;

        // One card id, two rows of the service: the tribe is what tells them apart, each with its own numbers.
        var rows = trinkets.Entries.Where(e => e.CardId == "BG30_MagicItem_426t").ToList();
        Assert.Equal(["Quilboar", "Mech"], rows.Select(e => e.Race));
        Assert.All(rows, e => Assert.Equal(("D", "LARGE", 2), (e.Tier, e.TrinketSize, e.Cost)));
        Assert.NotEqual(rows[0].AveragePlacement, rows[1].AveragePlacement);
        Assert.NotEqual(rows[0].NameRu, rows[1].NameRu);
        // The other trinkets, and every entry of the other lists, have none.
        Assert.All(trinkets.Entries.Where(e => e.CardId != "BG30_MagicItem_426t"), e => Assert.Null(e.Race));
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;
        Assert.All(stats.Heroes!.Entries.Concat(stats.Minions!.Entries).Concat(stats.HeroesDuos!.Entries), e => Assert.Null(e.Race));
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
    public async Task Comps_read_their_tier_cards_difficulty_tip_and_date()
    {
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;
        var comps = stats.Comps;

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
        Assert.Equal(new DateTime(2026, 11, 9, 17, 0, 12, DateTimeKind.Utc), stats.CompsFetchedAt!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(DateTimeKind.Utc, stats.CompsFetchedAt.Value.Kind);
    }

    [Fact]
    public async Task Without_comps_there_is_no_comps_date()
    {
        var site = FakeSite.Real();
        site.Fail(Comps, HttpStatusCode.InternalServerError);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Empty(stats.Comps);
        Assert.Null(stats.CompsFetchedAt);
    }

    [Fact]
    public async Task Updated_is_the_latest_fetch_time_of_the_loaded_parts()
    {
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;

        Assert.Equal(stats.Trinkets!.FetchedAt, stats.Updated);
        Assert.True(stats.Updated >= stats.Heroes!.FetchedAt && stats.Updated >= stats.Minions!.FetchedAt && stats.Updated >= stats.CompsFetchedAt);
    }

    // AC18

    [Fact]
    public async Task Within_six_hours_the_cache_answers_and_after_them_the_service_is_asked_again()
    {
        var site = FakeSite.Real();
        var client = Client(site);

        var first = (await client.LoadAsync())!;
        Assert.Equal(Refresh, site.Requests.Count);
        foreach (var part in new[] { "tier-lists", "heroes", "heroes-duos-top_50_percent", "minion-stats", "comps" })
        {
            Assert.True(File.Exists(Path.Combine(_cache, $"bgsite-{part}.json")), part);
        }

        var second = (await Client(site).LoadAsync())!;
        Assert.Equal(Refresh, site.Requests.Count);
        Assert.Equal(first.Heroes!.Entries, second.Heroes!.Entries);
        Assert.Equal(first.HeroesDuos!.Entries, second.HeroesDuos!.Entries);
        Assert.Equal(first.Updated, second.Updated);

        client.Clock = () => DateTime.UtcNow + BgSiteClient.MaxAge - TimeSpan.FromMinutes(5);
        await client.LoadAsync();
        Assert.Equal(Refresh, site.Requests.Count);

        client.Clock = () => DateTime.UtcNow + BgSiteClient.MaxAge + TimeSpan.FromMinutes(5);
        await client.LoadAsync();
        Assert.Equal(2 * Refresh, site.Requests.Count);
        Assert.Empty(Directory.GetFiles(_cache, "*.tmp"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task When_the_service_fails_the_old_files_answer_with_their_own_time(HttpStatusCode? failure)
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;

        var down = FakeSite.Down(failure);
        var stale = (await Later(down).LoadAsync())!;

        // The Duos slice is not asked for when the heroes address fails: its old file answers.
        Assert.Equal(Refresh - 1, down.Requests.Count);
        Assert.Equal(loaded.HeroesDuos!.Entries, stale.HeroesDuos!.Entries);
        AssertSame(loaded, stale);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(null)]
    public async Task When_the_service_fails_and_nothing_is_cached_there_are_no_stats(HttpStatusCode? failure)
    {
        var down = FakeSite.Down(failure);

        Assert.Null(await Client(down).LoadAsync());
        Assert.Equal(Refresh - 1, down.Requests.Count);
        AssertNoCacheFiles();
    }

    [Fact]
    public async Task An_unreadable_cache_file_is_asked_for_again()
    {
        var site = FakeSite.Real();
        await Client(site).LoadAsync();
        await File.WriteAllBytesAsync(Path.Combine(_cache, "bgsite-comps.json"), new byte[64]);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Equal(Refresh + 1, site.Requests.Count);
        Assert.Equal(3, stats.Comps.Count);
    }

    // AC18, Q1: a 200 answer without data is a failed load

    [Theory]
    [InlineData(TierLists, "tier-lists", "{}")]
    [InlineData(TierLists, "tier-lists", "{\"lists\": {}}")]
    [InlineData(TierLists, "tier-lists", "{\"detail\": \"maintenance\"}")]
    [InlineData(TierLists, "tier-lists", "{\"lists\": {\"minions\": {\"source\": \"hsreplay\", \"tiers\": {\"S\": [], \"A\": []}}}}")]
    [InlineData(Heroes, "heroes", "{}")]
    [InlineData(Heroes, "heroes", "{\"source_id\": \"hsreplay_battlegrounds_heroes\", \"ok\": false, \"site\": \"hsreplay\", \"view\": {\"blocked\": true, \"heroes\": []}}")]
    [InlineData(Heroes, "heroes", "{\"source_id\": \"hsreplay_battlegrounds_heroes\", \"ok\": false, \"view\": {\"heroes\": [{\"id\": \"BG_H\", \"dbfId\": 1}]}}")]
    [InlineData(Heroes, "heroes", "{\"source_id\": \"hsreplay_battlegrounds_heroes\", \"ok\": true, \"view\": {\"blocked\": true, \"heroes\": [{\"id\": \"BG_H\", \"dbfId\": 1}]}}")]
    [InlineData(MinionStats, "minion-stats", "{}")]
    [InlineData(MinionStats, "minion-stats", "{\"latest_run\": {\"source\": \"hsreplay_battlegrounds_minions\", \"state\": \"error\", \"error\": \"blocked\"}, \"minions\": []}")]
    [InlineData(Comps, "comps", "{}")]
    [InlineData(Comps, "comps", "{\"source\": \"HSReplay\", \"comps\": []}")]
    public async Task An_answer_without_data_does_not_replace_the_old_file(string address, string part, string body)
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        var file = Path.Combine(_cache, $"bgsite-{part}.json");
        var before = await File.ReadAllBytesAsync(file);
        var written = File.GetLastWriteTimeUtc(file);
        var mark = LogMark();

        var site = FakeSite.Real();
        site.Answer(address, body);
        var stale = (await Later(site).LoadAsync())!;

        AssertSame(loaded, stale);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Contains($"Battlegrounds site {part}: the answer has no data, not kept", LogSince(mark), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_answers_everywhere_leave_the_old_stats()
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;

        var stale = (await Later(FakeSite.Everywhere("{}")).LoadAsync())!;

        AssertSame(loaded, stale);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"detail\": \"maintenance\"}")]
    [InlineData("{\"ok\": false, \"lists\": {}, \"comps\": [], \"minions\": [], \"view\": {\"blocked\": true, \"heroes\": []}}")]
    public async Task Empty_answers_everywhere_and_no_cache_mean_no_stats(string body)
    {
        var site = FakeSite.Everywhere(body);

        Assert.Null(await Client(site).LoadAsync());
        Assert.Equal(Refresh - 1, site.Requests.Count);
        AssertNoCacheFiles();
    }

    [Fact]
    public async Task A_cache_file_without_data_is_asked_for_again()
    {
        var site = FakeSite.Real();
        await Client(site).LoadAsync();
        await File.WriteAllTextAsync(Path.Combine(_cache, "bgsite-heroes.json"), "{}");
        var asked = site.Requests.Count;

        var stats = (await Client(site).LoadAsync())!;

        Assert.Contains(site.Requests.Skip(asked), r => r.Uri.PathAndQuery == Heroes);
        Assert.Equal(12, stats.Heroes!.Entries.Count);
    }

    // AC18, Q2: nothing a handler or a body throws leaves LoadAsync

    [Theory]
    [InlineData("body", "InvalidDataException")]
    [InlineData("send", "InvalidOperationException")]
    [InlineData("disposed", "ObjectDisposedException")]
    public async Task Whatever_the_request_throws_the_old_files_answer(string kind, string logged)
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        var mark = LogMark();

        var broken = FakeSite.Broken(kind);
        var stale = (await Later(broken).LoadAsync())!;

        Assert.Equal(Refresh - 1, broken.Requests.Count);
        AssertSame(loaded, stale);
        Assert.Equal(loaded.HeroesDuos!.Entries, stale.HeroesDuos!.Entries);
        Assert.Contains($"Battlegrounds site comps: {logged}: ", LogSince(mark), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("send")]
    [InlineData("disposed")]
    public async Task Whatever_the_request_throws_without_a_cache_there_are_no_stats(string kind)
    {
        Assert.Null(await Client(FakeSite.Broken(kind)).LoadAsync());
        AssertNoCacheFiles();
    }

    [Fact]
    public async Task A_disposed_http_client_does_not_throw()
    {
        var http = new HttpClient(FakeSite.Real());
        http.Dispose();

        Assert.Null(await new BgSiteClient(http, cacheDirectory: _cache).LoadAsync());
    }

    // AC19

    [Theory]
    [InlineData(TierLists, "tier-lists")]
    [InlineData(Heroes, "heroes")]
    [InlineData(MinionStats, "minion-stats")]
    [InlineData(Comps, "comps")]
    public async Task One_failing_address_leaves_the_other_parts_loaded(string failing, string part)
    {
        var mark = LogMark();
        var site = FakeSite.Real();
        site.Fail(failing, HttpStatusCode.InternalServerError);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Equal(failing == Heroes ? Refresh - 1 : Refresh, site.Requests.Count);
        AssertPartsExcept(stats, failing);
        var log = LogSince(mark);
        Assert.Contains($"Battlegrounds site {part}: HTTP 500", log, StringComparison.Ordinal);
        // Only this client's lines: other classes run beside this one and can write an "HTTP 500" of their own.
        Assert.Single(log.Split('\n'), l => l.Contains("Battlegrounds site ", StringComparison.Ordinal) && l.Contains("HTTP 500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_address_that_does_not_answer_in_time_leaves_the_other_parts_loaded()
    {
        var mark = LogMark();
        var time = new FakeTimeProvider();
        var site = FakeSite.Real();
        site.Hang(MinionStats);
        var client = new BgSiteClient(new HttpClient(site), cacheDirectory: _cache) { Time = time };

        var load = client.LoadAsync();
        await site.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(30));
        time.Advance(BgSiteClient.Timeout + TimeSpan.FromSeconds(1));
        var stats = (await load.WaitAsync(TimeSpan.FromSeconds(30)))!;

        AssertPartsExcept(stats, MinionStats);
        Assert.Contains("Battlegrounds site minion-stats: no answer in 20 s", LogSince(mark), StringComparison.Ordinal);
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

    [Fact]
    public async Task A_cancelled_load_answers_from_the_old_files_without_a_failure_in_the_log()
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        var mark = LogMark();
        var site = FakeSite.Real();
        site.Hang(TierLists);
        site.Hang(Heroes);
        site.Hang(Duos);
        site.Hang(MinionStats);
        site.Hang(Comps);
        using var cancel = new CancellationTokenSource();

        var load = Later(site).LoadAsync(cancel.Token);
        await site.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await cancel.CancelAsync();
        var stale = (await load.WaitAsync(TimeSpan.FromSeconds(30)))!;

        AssertSame(loaded, stale);
        // A cancellation is not a failed load: no time-out and no error is written for it.
        var log = LogSince(mark);
        Assert.DoesNotContain("no answer in", log, StringComparison.Ordinal);
        Assert.DoesNotContain("not loaded", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_load_cancelled_before_it_starts_gives_no_stats_and_does_not_throw()
    {
        var site = FakeSite.Real();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        Assert.Null(await Client(site).LoadAsync(cancel.Token));
    }

    // AC20

    [Theory]
    [InlineData("{\"lists\": {\"minions\": ", "Battlegrounds site tier-lists: unreadable answer")]
    [InlineData("<html>maintenance</html>", "Battlegrounds site tier-lists: unreadable answer")]
    [InlineData("[1, 2, 3]", "Battlegrounds site tier-lists: the answer is not an object")]
    [InlineData("", "Battlegrounds site tier-lists: unreadable answer")]
    [InlineData("{\"lists\": 1, \"lists\": 2}", "Battlegrounds site tier-lists: ")]
    public async Task A_broken_answer_drops_its_part_only(string body, string logged)
    {
        var mark = LogMark();
        var site = FakeSite.Real();
        site.Answer(TierLists, body);

        var stats = (await Client(site).LoadAsync())!;

        AssertPartsExcept(stats, TierLists);
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-tier-lists.json")));
        // Only this load's lines are read, and no other part of it writes under the tier lists' name.
        Assert.Contains(logged, LogSince(mark), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answer_with_a_byte_order_mark_is_read()
    {
        var site = FakeSite.Real();
        site.Answer(Comps, "\uFEFF" + Fixture("comps.json"));

        Assert.Equal(3, (await Client(site).LoadAsync())!.Comps.Count);
    }

    [Fact]
    public async Task An_unknown_tier_letter_is_dropped_with_a_log_line()
    {
        var mark = LogMark();
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
        Assert.Contains("Battlegrounds site minions: unknown tier \"Z9\" dropped", LogSince(mark), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entries_without_a_card_id_are_dropped_with_a_log_line()
    {
        var mark = LogMark();
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

        var log = LogSince(mark);
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
        var mark = LogMark();
        var site = FakeSite.Real();
        site.Answer(Comps, "{\"source\": \"HSReplay\", \"comps\": [], \"pad\": \"" + new string('x', BgSiteClient.MaxBytes) + "\"}", declaresLength);

        var stats = (await Client(site).LoadAsync())!;

        AssertPartsExcept(stats, Comps);
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-comps.json")));
        Assert.Contains(declaresLength ? "Battlegrounds site comps: answer of " : "Battlegrounds site comps: answer over 8 MB ignored", LogSince(mark), StringComparison.Ordinal);
    }

    // AC21, L-m4

    [Fact]
    public async Task Only_plain_gets_to_the_service_with_nothing_about_the_player()
    {
        var site = FakeSite.Real();

        await Client(site).LoadAsync();

        Assert.True(site.Requests.Count <= 8);
        Assert.Equal(
            new[] { TierLists, Heroes, Duos, MinionStats, Comps }.Order(StringComparer.Ordinal),
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

    [Fact]
    public void The_clients_own_handler_keeps_no_cookies_and_follows_no_redirects()
    {
        using var handler = BgSiteClient.CreateHandler();

        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);

        using var http = BgSiteClient.CreateHttp();
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Theory]
    [InlineData("https://elsewhere.example/api/bg/heroes")]
    [InlineData("http://bg.hs-manacost.ru/api/bg/heroes")]
    [InlineData("https://bg.hs-manacost.ru:8443/api/bg/heroes")]
    public async Task An_answer_that_came_from_another_host_is_not_read(string answeredFrom)
    {
        // A shared client that follows redirects reports the last address in the response's request.
        var mark = LogMark();
        var site = FakeSite.Real();
        site.AnswerFrom(Heroes, new Uri(answeredFrom));

        var stats = (await Client(site).LoadAsync())!;

        AssertPartsExcept(stats, Heroes);
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-heroes.json")));
        Assert.Contains("Battlegrounds site heroes: answered from another host, ignored", LogSince(mark), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_redirect_is_a_failed_load()
    {
        var site = FakeSite.Real();
        site.Fail(Heroes, HttpStatusCode.Found);

        AssertPartsExcept((await Client(site).LoadAsync())!, Heroes);
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

    // AC22, L-M2: only allowed sources

    [Fact]
    public async Task Firestone_lists_and_comps_are_not_given_out()
    {
        var mark = LogMark();
        var stats = (await Client(FakeSite.Real()).LoadAsync())!;

        // The fixture's spells and strategies lists and two of its comps name Firestone as their source.
        Assert.Null(stats.Spells);
        Assert.Empty(stats.SpellStats);
        Assert.Equal(3, stats.Comps.Count);
        Assert.DoesNotContain(stats.Comps, c => c.Id.StartsWith("firestone", StringComparison.OrdinalIgnoreCase));
        var log = LogSince(mark);
        Assert.Contains("Battlegrounds site tier-lists: list \"spells\" of source \"firestone\" is not used", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site tier-lists: list \"strategies\" of source \"firestone\" is not used", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site comps: 2 comps of a source that is not used dropped", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_data_is_not_kept_in_the_cache_either()
    {
        await Client(FakeSite.Real()).LoadAsync();

        var kept = await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-tier-lists.json"));
        using var lists = JsonDocument.Parse(kept);
        Assert.Equal(["heroes", "minions", "trinkets"], lists.RootElement.GetProperty("lists").EnumerateObject().Select(l => l.Name));
        // The rows of the refused lists are gone (the fixture's are invented, under these ids).
        Assert.DoesNotContain("TEST_FS_", kept, StringComparison.Ordinal);
        var keptComps = await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-comps.json"));
        using var comps = JsonDocument.Parse(keptComps);
        Assert.All(comps.RootElement.GetProperty("comps").EnumerateArray(), c => Assert.Equal("HSReplay", c.GetProperty("source").GetString()));
        Assert.Equal(3, comps.RootElement.GetProperty("comps").GetArrayLength());
        Assert.DoesNotContain("TEST_FS_", keptComps, StringComparison.Ordinal);
        // Cyrillic is kept as it is, not as \u escapes that would double the file.
        Assert.Contains("Свинобразы", keptComps, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("firestone")]
    [InlineData("Firestone через api.kolodahearthstone.com")]
    [InlineData("static.ZeroToHeroes.com")]
    [InlineData("HSReplay + Firestone через api.kolodahearthstone.com")]
    [InlineData("hearthpulse")]
    [InlineData("heartharena")]
    [InlineData("hsreplay_premium")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Only_a_source_on_the_allow_list_is_given_out(string? source)
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
        // A comp that names its own, allowed source stays; one that only has the list's source goes with it. An unknown
        // tier letter of a comp is no tier.
        var comp = Assert.Single(stats.Comps);
        Assert.Equal(("b", (string?)null), (comp.Id, comp.Tier));

        Assert.DoesNotContain("BG_A", await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-tier-lists.json")), StringComparison.Ordinal);
        Assert.DoesNotContain("\"a\"", await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-comps.json")), StringComparison.Ordinal);
        // An answer with nothing left to show is not kept at all.
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-minion-stats.json")));
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-heroes.json")));
    }

    [Theory]
    [InlineData("hsreplay")]
    [InlineData("HSReplay")]
    [InlineData("  HsReplay ")]
    [InlineData("HS Replay")]
    public async Task The_allowed_source_is_read_whatever_its_case_and_spaces(string source)
    {
        var site = FakeSite.Real();
        site.Answer(TierLists, $$"""{"lists": {"minions": {"source": {{JsonSerializer.Serialize(source)}}, "tiers": {"S": [ {"id": "BG_A", "name": "A"} ]} } } }""");
        site.Answer(Comps, $$"""{"comps": [ {"key": "a", "source": {{JsonSerializer.Serialize(source)}}, "title": "A", "tier": "S"} ]}""");

        var stats = (await Client(site).LoadAsync())!;

        Assert.Equal("BG_A", Assert.Single(stats.Minions!.Entries).CardId);
        Assert.Equal(source, stats.Minions.Source);
        Assert.Equal("a", Assert.Single(stats.Comps).Id);
    }

    [Fact]
    public async Task An_allowed_source_over_firestone_data_is_refused()
    {
        // The service says where a list was taken from next to its source: Firestone there refuses it too.
        var site = FakeSite.Real();
        site.Answer(TierLists, """
            {"lists": {
              "minions": {"source": "hsreplay", "upstreamSource": "Firestone через api.kolodahearthstone.com", "tiers": {"S": [{"id": "BG_A", "name": "A"}]}},
              "trinkets": {"source": "hsreplay", "upstreamSource": "HSReplay trinkets + Blizzard card images", "tiers": {"S": [{"id": "BG_T", "name": "T"}]}}}}
            """);
        site.Answer(Comps, """
            {"source": "HSReplay", "comps": [
              {"key": "a", "source": "HSReplay", "sourceUrl": "https://www.firestoneapp.com/battlegrounds/comps", "title": "A"},
              {"key": "b", "source": "HSReplay", "sourceUrl": "https://static.zerotoheroes.com/x", "title": "B"},
              {"key": "c", "source": "HSReplay", "sourceUrl": "https://hsreplay.net/battlegrounds/comps/1/", "title": "C"}]}
            """);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.Minions);
        Assert.NotNull(stats.Trinkets);
        Assert.Equal("c", Assert.Single(stats.Comps).Id);
        Assert.DoesNotContain("BG_A", await File.ReadAllTextAsync(Path.Combine(_cache, "bgsite-tier-lists.json")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spells_list_of_the_allowed_source_is_given_out()
    {
        // The refusal is by source, not by list name: a spells list the service marks as HSReplay's is a list like the others.
        var site = FakeSite.Real();
        site.Answer(TierLists, """
            {"lists": {"spells": {"source": "hsreplay", "tiers": {"S": [{"id": "BG28_503", "name": "Fortify", "ruName": "Укрепление", "tavernTier": 1}]}}}}
            """);

        var spells = (await Client(site).LoadAsync())!.Spells!;

        Assert.Equal(BgSiteList.Spells, spells.Kind);
        Assert.Equal(("BG28_503", 1), (spells.Entries[0].CardId, spells.Entries[0].TavernTier));
    }

    // AC23: Duos and MMR slices. A heroes slice names no source: the answer of the same address without parameters vouches for it.

    private const string SoloTop20 = "/api/bg/heroes?mode=solo&mmr=TOP_20_PERCENT";
    private const string DuosCache = "bgsite-heroes-duos-top_50_percent.json";
    private const string DuosNotRead = "Battlegrounds site heroes-duos-top_50_percent: not read, the heroes answer names no allowed source";

    [Fact]
    public async Task The_duos_list_is_read_under_the_source_the_heroes_answer_names()
    {
        var site = FakeSite.Real();

        var stats = (await Client(site).LoadAsync())!;

        Assert.Contains(site.Requests, r => r.Uri.PathAndQuery == Duos);
        Assert.True(File.Exists(Path.Combine(_cache, DuosCache)));
        var duos = stats.HeroesDuos!;
        Assert.Equal(BgSiteList.Heroes, duos.Kind);
        // The slice names no source of its own: it carries the one the heroes answer named.
        Assert.Equal("hsreplay_battlegrounds_heroes", duos.Source);
        Assert.Equal("TOP_50_PERCENT", duos.Mmr);
        Assert.Equal("CURRENT_BATTLEGROUNDS_PATCH", duos.TimeRange);
        Assert.Equal(new DateTime(2026, 11, 9, 3, 35, 3, DateTimeKind.Utc), duos.FetchedAt, TimeSpan.FromSeconds(1));
        Assert.Equal(["S", "A", "B", "C", "D"], duos.AvailableTiers);
        Assert.Equal(["A", "A", "B", "B", "C", "C", "D", "D"], duos.Entries.Select(e => e.Tier));

        var hero = duos.Entries[0];
        Assert.Equal(("TB_BaconShop_HERO_34", 59397, "A", "Patchwerk", "Patchwerk"), (hero.CardId, hero.DbfId, hero.Tier, hero.NameRu, hero.NameEn));
        Assert.Equal(2.178, hero.AveragePlacement!.Value, 3);
        Assert.Equal(0.4851, hero.PickRate!.Value, 4);
        // Four team places in Duos: first, and the top half (places 1-2).
        Assert.Equal(0.3306, hero.FirstRate!.Value, 4);
        Assert.Equal(0.6356, hero.Top4Rate!.Value, 4);
        Assert.Null(hero.Games);
        // The solo list is a list of its own.
        Assert.NotEqual(stats.Heroes!.Entries[0].CardId, hero.CardId);
    }

    [Theory]
    [InlineData("no-source")]
    [InlineData("firestone")]
    [InlineData("site-firestone")]
    [InlineData("unknown-source")]
    [InlineData("empty")]
    [InlineData("failed")]
    public async Task Without_a_heroes_answer_that_names_the_allowed_source_the_duos_slice_is_not_even_asked_for(string heroes)
    {
        var mark = LogMark();
        var site = FakeSite.Real();
        switch (heroes)
        {
            case "failed":
                site.Fail(Heroes, HttpStatusCode.InternalServerError);
                break;
            case "empty":
                site.Answer(Heroes, "{}");
                break;
            default:
                site.Answer(Heroes, Changed("heroes.json", answer =>
                {
                    switch (heroes)
                    {
                        case "no-source":
                            answer.Remove("source_id");
                            answer.Remove("site");
                            break;
                        case "firestone":
                            answer["source_id"] = "firestone_battlegrounds_heroes";
                            answer["site"] = "firestone";
                            break;
                        case "site-firestone":
                            answer["site"] = "firestone";
                            break;
                        default:
                            answer["source_id"] = "hearthpulse_battlegrounds_heroes";
                            break;
                    }
                }));
                break;
        }

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.HeroesDuos);
        Assert.Null(stats.Heroes);
        Assert.NotNull(stats.Minions);
        // No request whose answer would not be used.
        Assert.DoesNotContain(site.Requests, r => r.Uri.PathAndQuery == Duos);
        Assert.Equal(Refresh - 1, site.Requests.Count);
        Assert.False(File.Exists(Path.Combine(_cache, DuosCache)));
        Assert.Contains(DuosNotRead, LogSince(mark), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ok-false")]
    [InlineData("ok-missing")]
    [InlineData("ok-text")]
    [InlineData("type-other")]
    [InlineData("type-missing")]
    [InlineData("blocked")]
    [InlineData("own-source-firestone")]
    [InlineData("no-rows")]
    public async Task A_duos_answer_that_is_not_an_ok_heroes_view_is_not_read(string slice)
    {
        var mark = LogMark();
        var site = FakeSite.Real();
        site.Answer(Duos, Changed("heroes-duos.json", answer =>
        {
            var view = answer["view"]!.AsObject();
            switch (slice)
            {
                case "ok-false":
                    answer["ok"] = false;
                    break;
                case "ok-missing":
                    answer.Remove("ok");
                    break;
                case "ok-text":
                    answer["ok"] = "true";
                    break;
                case "type-other":
                    view["type"] = "bg_comps";
                    break;
                case "type-missing":
                    view.Remove("type");
                    break;
                case "blocked":
                    view["blocked"] = true;
                    break;
                case "own-source-firestone":
                    answer["source_id"] = "firestone_battlegrounds_heroes";
                    break;
                default:
                    view["heroes"] = new JsonArray();
                    break;
            }
        }));

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.HeroesDuos);
        Assert.Equal(12, stats.Heroes!.Entries.Count);
        Assert.False(File.Exists(Path.Combine(_cache, DuosCache)));
        Assert.Contains("Battlegrounds site heroes-duos-top_50_percent: the answer has no data, not kept", LogSince(mark), StringComparison.Ordinal);
    }

    [Fact]
    public async Task When_the_heroes_answer_turns_to_another_source_the_cached_duos_list_is_dropped()
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        Assert.NotNull(loaded.HeroesDuos);
        var mark = LogMark();

        var site = FakeSite.Real();
        site.Answer(Heroes, Changed("heroes.json", answer =>
        {
            answer["source_id"] = "firestone_battlegrounds_heroes";
            answer["site"] = "firestone";
        }));
        var later = (await Later(site).LoadAsync())!;

        // The service says now that heroes come from elsewhere: nothing vouches for the slice, cached or new.
        Assert.Null(later.HeroesDuos);
        Assert.DoesNotContain(site.Requests, r => r.Uri.PathAndQuery == Duos);
        Assert.Contains(DuosNotRead, LogSince(mark), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task When_the_heroes_address_fails_the_old_duos_file_answers_and_no_new_slice_is_asked_for(HttpStatusCode? failure)
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;

        var site = FakeSite.Real();
        if (failure is { } status)
        {
            site.Fail(Heroes, status);
        }
        else
        {
            site.AnswerFrom(Heroes, new Uri("https://elsewhere.example/api/bg/heroes"));
        }

        var later = (await Later(site).LoadAsync())!;

        // The old heroes file vouches for the old Duos file, not for a slice fetched now.
        Assert.Equal(loaded.HeroesDuos!.Entries, later.HeroesDuos!.Entries);
        Assert.Equal(loaded.Heroes!.Entries, later.Heroes!.Entries);
        Assert.DoesNotContain(site.Requests, r => r.Uri.PathAndQuery == Duos);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_duos_slice_the_service_does_not_give_is_no_failure_of_the_refresh(HttpStatusCode status)
    {
        var site = FakeSite.Real();
        site.Fail(Duos, status);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.HeroesDuos);
        AssertPartsExcept(stats, failing: Duos);
        Assert.Equal(12, stats.Heroes!.Entries.Count);
    }

    [Fact]
    public async Task A_heroes_slice_is_asked_by_mode_and_mmr_after_the_heroes_answer_and_cached()
    {
        var site = FakeSite.Real();
        site.Answer(SoloTop20, Fixture("heroes-solo-top20.json"));
        var client = Client(site);

        var slice = (await client.LoadHeroesAsync(duos: false, "TOP_20_PERCENT"))!;
        var asked = site.Requests.Select(r => r.Uri.PathAndQuery).ToList();
        var again = (await client.LoadHeroesAsync(duos: false, "TOP_20_PERCENT"))!;

        // The heroes answer first (it names the source), then the slice; nothing else, and nothing the second time.
        Assert.Equal([Heroes, SoloTop20], asked);
        Assert.Equal(2, site.Requests.Count);
        Assert.True(File.Exists(Path.Combine(_cache, "bgsite-heroes-solo-top_20_percent.json")));
        Assert.Equal(slice.Entries, again.Entries);
        Assert.Equal(("TOP_20_PERCENT", "hsreplay_battlegrounds_heroes"), (slice.Mmr, slice.Source));
        Assert.Equal(9, slice.Entries.Count);
        var hero = slice.Entries[0];
        Assert.Equal(("BG27_HERO_801", "S", "Thorim, Stormlord"), (hero.CardId, hero.Tier, hero.NameEn));
        Assert.Equal(3.794, hero.AveragePlacement!.Value, 3);
        Assert.Equal(0.23, hero.FirstRate!.Value, 4);
        Assert.Equal(0.6154, hero.Top4Rate!.Value, 4);
    }

    [Fact]
    public async Task Heroes_a_slice_has_no_tier_for_are_left_out()
    {
        // In the top 1% the service has no tier and no placement for heroes with too few games.
        var site = FakeSite.Real();
        site.Answer("/api/bg/heroes?mode=solo&mmr=TOP_1_PERCENT", Fixture("heroes-solo-top1.json"));

        var slice = (await Client(site).LoadHeroesAsync(duos: false, "TOP_1_PERCENT"))!;

        Assert.Equal("TOP_1_PERCENT", slice.Mmr);
        Assert.Equal(9, slice.Entries.Count);
        Assert.DoesNotContain(slice.Entries, e => e.CardId is "TB_BaconShop_HERO_56" or "TB_BaconShop_HERO_59");
        Assert.Equal(("BG27_HERO_801", "S"), (slice.Entries[0].CardId, slice.Entries[0].Tier));
        Assert.Equal(2.781, slice.Entries[0].AveragePlacement!.Value, 3);
        Assert.Equal(0.4175, slice.Entries[0].FirstRate!.Value, 4);
    }

    [Fact]
    public async Task The_default_solo_slice_is_the_list_of_the_refresh()
    {
        var site = FakeSite.Real();
        var client = Client(site);

        var slice = (await client.LoadHeroesAsync(duos: false, BgSiteClient.HeroesSoloSlices.Default))!;

        Assert.Equal(new[] { Heroes, TierLists }, site.Requests.Select(r => r.Uri.PathAndQuery).Order(StringComparer.Ordinal));
        Assert.Equal((await client.LoadAsync())!.Heroes!.Entries, slice.Entries);
        Assert.Equal("TOP_50_PERCENT", slice.Mmr);
    }

    [Fact]
    public async Task A_heroes_slice_is_not_asked_for_when_the_heroes_answer_names_no_source()
    {
        var site = FakeSite.Real();
        site.Answer(Heroes, Changed("heroes.json", answer => answer.Remove("source_id")));
        site.Answer(SoloTop20, Fixture("heroes-solo-top20.json"));
        var client = Client(site);

        Assert.Null(await client.LoadHeroesAsync(duos: false, "TOP_20_PERCENT"));
        Assert.Null(await client.LoadHeroesAsync(duos: true, "TOP_20_PERCENT"));
        Assert.All(site.Requests, r => Assert.Equal(Heroes, r.Uri.PathAndQuery));
    }

    [Fact]
    public async Task A_trinket_slice_is_asked_by_mmr_and_reads_the_flat_list()
    {
        const string address = "/api/bg/tier-lists?list=trinkets&mmr=TOP_50_PERCENT&timeRange=LAST_7_DAYS";
        var site = FakeSite.Real();
        site.Answer(address, Fixture("trinkets-top50.json"));
        var client = Client(site);

        var slice = (await client.LoadTrinketsAsync("TOP_50_PERCENT"))!;
        await client.LoadTrinketsAsync("TOP_50_PERCENT");

        Assert.Equal(address, Assert.Single(site.Requests).Uri.PathAndQuery);
        Assert.True(File.Exists(Path.Combine(_cache, "bgsite-trinkets-top_50_percent.json")));
        Assert.Equal((BgSiteList.Trinkets, "hsreplay", "TOP_50_PERCENT", "LAST_7_DAYS"), (slice.Kind, slice.Source, slice.Mmr, slice.TimeRange));
        Assert.Equal(10, slice.Entries.Count);
        var trinket = slice.Entries[0];
        Assert.Equal(("BG36_MagicItem_216", "S", "Портрет вечности", "Eternal Portrait", "LARGE", 5, 2528), (trinket.CardId, trinket.Tier, trinket.NameRu, trinket.NameEn, trinket.TrinketSize, trinket.Cost, trinket.Games));
        Assert.Equal(3.47, trinket.AveragePlacement!.Value, 3);
        Assert.Equal(0.277, trinket.PickRate!.Value, 4);
    }

    [Fact]
    public async Task The_default_trinket_slice_is_the_list_of_the_refresh()
    {
        var site = FakeSite.Real();
        var client = Client(site);

        var slice = (await client.LoadTrinketsAsync(BgSiteClient.TrinketSlices.Default))!;

        Assert.Equal(TierLists, Assert.Single(site.Requests).Uri.PathAndQuery);
        Assert.Equal("TOP_1_PERCENT", slice.Mmr);
        Assert.Equal((await client.LoadAsync())!.Trinkets!.Entries, slice.Entries);
    }

    [Fact]
    public void Every_list_names_its_slices_and_the_one_the_service_gives_by_default()
    {
        string[] four = ["TOP_50_PERCENT", "TOP_20_PERCENT", "TOP_5_PERCENT", "TOP_1_PERCENT"];

        Assert.Equal(four, BgSiteClient.HeroesSoloSlices.Mmr);
        Assert.Equal("TOP_50_PERCENT", BgSiteClient.HeroesSoloSlices.Default);
        // Duos: the two slices checked against the service.
        Assert.Equal(["TOP_50_PERCENT", "TOP_20_PERCENT"], BgSiteClient.HeroesDuosSlices.Mmr);
        Assert.Equal("TOP_50_PERCENT", BgSiteClient.HeroesDuosSlices.Default);
        Assert.Equal(four, BgSiteClient.TrinketSlices.Mmr);
        Assert.Equal("TOP_1_PERCENT", BgSiteClient.TrinketSlices.Default);
        Assert.Same(BgSiteClient.HeroesDuosSlices, BgSiteClient.HeroSlices(duos: true));
        Assert.Same(BgSiteClient.HeroesSoloSlices, BgSiteClient.HeroSlices(duos: false));
        Assert.All(new[] { BgSiteClient.HeroesSoloSlices, BgSiteClient.HeroesDuosSlices, BgSiteClient.TrinketSlices }, s => Assert.True(s.Has(s.Default)));
        Assert.False(BgSiteClient.HeroesDuosSlices.Has("TOP_5_PERCENT"));
        Assert.False(BgSiteClient.TrinketSlices.Has(null));
    }

    [Theory]
    [InlineData(true, "TOP_5_PERCENT")]
    [InlineData(true, "TOP_1_PERCENT")]
    [InlineData(false, "ALL")]
    [InlineData(false, "top_50_percent")]
    [InlineData(false, "TOP_50_PERCENT&list=spells")]
    [InlineData(true, "")]
    public async Task A_slice_that_is_not_listed_is_no_data_and_no_request(bool duos, string mmr)
    {
        var site = FakeSite.Real();
        var client = Client(site);

        Assert.Null(await client.LoadHeroesAsync(duos, mmr));
        if (!BgSiteClient.TrinketSlices.Has(mmr))
        {
            Assert.Null(await client.LoadTrinketsAsync(mmr));
        }

        Assert.Empty(site.Requests);
    }

    [Fact]
    public async Task A_listed_slice_the_service_does_not_answer_for_is_no_data_not_an_error()
    {
        // Nothing is routed for these addresses: the fake answers 404, as a service without the slice would.
        var mark = LogMark();
        var client = Client(FakeSite.Real());

        Assert.Null(await client.LoadTrinketsAsync("TOP_5_PERCENT"));
        Assert.Null(await client.LoadHeroesAsync(duos: true, "TOP_20_PERCENT"));

        var log = LogSince(mark);
        Assert.Contains("Battlegrounds site trinkets-top_5_percent: HTTP 404", log, StringComparison.Ordinal);
        Assert.Contains("Battlegrounds site heroes-duos-top_20_percent: HTTP 404", log, StringComparison.Ordinal);
    }

    // Q5: the heroes answer alone is not stats

    [Fact]
    public async Task When_only_the_heroes_address_answers_there_are_no_stats()
    {
        // /api/bg/heroes gives card ids for the tier list and nothing to show by itself.
        var site = FakeSite.Real();
        site.Fail(TierLists, HttpStatusCode.InternalServerError);
        site.Fail(Duos, HttpStatusCode.InternalServerError);
        site.Fail(MinionStats, HttpStatusCode.InternalServerError);
        site.Fail(Comps, HttpStatusCode.InternalServerError);

        Assert.Null(await Client(site).LoadAsync());
        Assert.Equal(Refresh, site.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(null)]
    public async Task When_only_the_heroes_file_is_cached_and_the_service_is_down_there_are_no_stats(HttpStatusCode? failure)
    {
        await Client(FakeSite.Real()).LoadAsync();
        foreach (var file in Directory.GetFiles(_cache).Where(f => Path.GetFileName(f) != "bgsite-heroes.json"))
        {
            File.Delete(file);
        }

        Assert.Null(await Later(FakeSite.Down(failure)).LoadAsync());
        Assert.Null(await Client(FakeSite.Down(failure)).LoadAsync());
    }

    [Fact]
    public async Task The_heroes_answer_with_its_duos_slice_is_stats()
    {
        var site = FakeSite.Real();
        site.Fail(TierLists, HttpStatusCode.InternalServerError);
        site.Fail(MinionStats, HttpStatusCode.InternalServerError);
        site.Fail(Comps, HttpStatusCode.InternalServerError);

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.Heroes);
        Assert.Equal(8, stats.HeroesDuos!.Entries.Count);
        Assert.Equal(stats.HeroesDuos.FetchedAt, stats.Updated);
    }

    // Q6 / m9: a heroes answer that says nothing is a failed load, not another source

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"detail\": \"maintenance\"}")]
    [InlineData("{\"ok\": false}")]
    [InlineData("{\"ok\": false, \"view\": {\"blocked\": true, \"heroes\": []}}")]
    [InlineData("<html>maintenance</html>")]
    public async Task A_heroes_answer_that_says_nothing_leaves_the_cached_heroes_and_duos(string body)
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        Assert.NotNull(loaded.HeroesDuos);

        var site = FakeSite.Real();
        site.Answer(Heroes, body);
        var later = (await Later(site).LoadAsync())!;

        AssertSame(loaded, later);
        Assert.Equal(loaded.HeroesDuos!.Entries, later.HeroesDuos!.Entries);
        // The old heroes file opens the old Duos file only: no slice is fetched under it.
        Assert.DoesNotContain(site.Requests, r => r.Uri.PathAndQuery == Duos);
        Assert.NotNull(await Later(site).LoadHeroesAsync(duos: true, "TOP_50_PERCENT"));
    }

    [Fact]
    public async Task A_heroes_answer_with_heroes_and_no_source_drops_the_cached_duos()
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        Assert.NotNull(loaded.HeroesDuos);

        var site = FakeSite.Real();
        site.Answer(Heroes, Changed("heroes.json", answer =>
        {
            answer.Remove("source_id");
            answer.Remove("site");
        }));
        var later = (await Later(site).LoadAsync())!;

        // Heroes without a source is a statement, not a failure: nothing vouches for the slice any more.
        Assert.Null(later.HeroesDuos);
        Assert.DoesNotContain(site.Requests, r => r.Uri.PathAndQuery == Duos);
    }

    // m8: the heroes file within its six hours vouches for a slice asked now

    [Fact]
    public async Task A_fresh_heroes_file_vouches_for_a_slice_asked_now()
    {
        var site = FakeSite.Real();
        site.Answer(SoloTop20, Fixture("heroes-solo-top20.json"));
        var client = Client(site);
        await client.LoadAsync();
        var asked = site.Requests.Count;

        var slice = await client.LoadHeroesAsync(duos: false, "TOP_20_PERCENT");

        Assert.NotNull(slice);
        Assert.Equal(SoloTop20, Assert.Single(site.Requests.Skip(asked)).Uri.PathAndQuery);
    }

    [Fact]
    public async Task A_slice_on_demand_is_not_fetched_when_the_heroes_address_fails()
    {
        await Client(FakeSite.Real()).LoadAsync();
        var site = FakeSite.Real();
        site.Fail(Heroes, HttpStatusCode.InternalServerError);
        site.Answer(SoloTop20, Fixture("heroes-solo-top20.json"));

        // No file of this slice yet, and the old heroes file does not vouch for a slice fetched now.
        Assert.Null(await Later(site).LoadHeroesAsync(duos: false, "TOP_20_PERCENT"));
        Assert.DoesNotContain(site.Requests, r => r.Uri.PathAndQuery == SoloTop20);
    }

    // m10: the answer must be the slice that was asked for

    [Theory]
    [InlineData("solo-instead-of-duos")]
    [InlineData("another-mmr")]
    public async Task A_duos_answer_that_is_another_slice_is_not_read(string kind)
    {
        var mark = LogMark();
        var site = FakeSite.Real();
        site.Answer(Duos, kind == "another-mmr"
            ? Changed("heroes-duos.json", answer => answer["view"]!["filters"]!["mmr_percentile"] = "TOP_20_PERCENT")
            : Fixture("heroes-solo-top20.json"));

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.HeroesDuos);
        Assert.NotNull(stats.Heroes);
        Assert.False(File.Exists(Path.Combine(_cache, DuosCache)));
        Assert.Contains("Battlegrounds site heroes-duos-top_50_percent: the answer is another slice (", LogSince(mark), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_heroes_slice_answered_with_another_mmr_is_no_data()
    {
        var site = FakeSite.Real();
        site.Answer(SoloTop20, Fixture("heroes-solo-top1.json"));

        Assert.Null(await Client(site).LoadHeroesAsync(duos: false, "TOP_20_PERCENT"));
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-heroes-solo-top_20_percent.json")));
    }

    [Theory]
    [InlineData("mmr", "TOP_1_PERCENT")]
    [InlineData("list", "minions")]
    public async Task A_trinket_slice_answered_with_another_list_or_mmr_is_no_data(string field, string value)
    {
        const string address = "/api/bg/tier-lists?list=trinkets&mmr=TOP_50_PERCENT&timeRange=LAST_7_DAYS";
        var site = FakeSite.Real();
        site.Answer(address, Changed("trinkets-top50.json", answer => answer[field] = value));

        Assert.Null(await Client(site).LoadTrinketsAsync("TOP_50_PERCENT"));
        Assert.False(File.Exists(Path.Combine(_cache, "bgsite-trinkets-top_50_percent.json")));
    }

    // m14: a tier lists answer that lost a list the old file has is a failed load

    [Theory]
    [InlineData("trinkets", "empty")]
    [InlineData("trinkets", "missing")]
    [InlineData("minions", "empty")]
    [InlineData("heroes", "missing")]
    public async Task A_tier_lists_answer_without_one_of_the_old_lists_does_not_replace_the_old_file(string list, string how)
    {
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        var file = Path.Combine(_cache, "bgsite-tier-lists.json");
        var before = await File.ReadAllBytesAsync(file);
        var mark = LogMark();

        var site = FakeSite.Real();
        site.Answer(TierLists, Changed("tier-lists.json", answer =>
        {
            var lists = answer["lists"]!.AsObject();
            if (how == "missing")
            {
                lists.Remove(list);
            }
            else
            {
                lists[list]!["tiers"] = new JsonObject { ["S"] = new JsonArray(), ["A"] = new JsonArray() };
            }
        }));
        var later = (await Later(site).LoadAsync())!;

        AssertSame(loaded, later);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Contains($"Battlegrounds site tier-lists: the answer came without rows of \"{list}\", the old file is kept", LogSince(mark), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_list_cut_by_its_source_is_not_a_lost_list()
    {
        // The service marks the trinkets as Firestone's now: that is a refusal, and the rest of the answer is taken.
        var loaded = (await Client(FakeSite.Real()).LoadAsync())!;
        var file = Path.Combine(_cache, "bgsite-tier-lists.json");
        var before = await File.ReadAllBytesAsync(file);

        var site = FakeSite.Real();
        site.Answer(TierLists, Changed("tier-lists.json", answer => answer["lists"]!["trinkets"]!["source"] = "firestone"));
        var later = (await Later(site).LoadAsync())!;

        Assert.Null(later.Trinkets);
        Assert.Equal(loaded.Minions!.Entries, later.Minions!.Entries);
        Assert.NotEqual(before, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    public async Task Without_an_old_file_a_tier_lists_answer_with_some_of_the_lists_is_taken()
    {
        var site = FakeSite.Real();
        site.Answer(TierLists, Changed("tier-lists.json", answer => answer["lists"]!.AsObject().Remove("trinkets")));

        var stats = (await Client(site).LoadAsync())!;

        Assert.Null(stats.Trinkets);
        Assert.Equal(16, stats.Minions!.Entries.Count);
        Assert.Equal(12, stats.Heroes!.Entries.Count);
    }

    private BgSiteClient Client(FakeSite site) => new(new HttpClient(site), cacheDirectory: _cache);

    /// <summary>A client for which every cache file written so far is too old.</summary>
    private BgSiteClient Later(FakeSite site)
    {
        var client = Client(site);
        client.Clock = () => DateTime.UtcNow + TimeSpan.FromDays(3);
        return client;
    }

    private void AssertNoCacheFiles() => Assert.False(Directory.Exists(_cache) && Directory.GetFiles(_cache).Length > 0);

    /// <summary>The same lists, numbers and times as before.</summary>
    private static void AssertSame(BgSiteStats expected, BgSiteStats actual)
    {
        Assert.Equal(expected.Updated, actual.Updated);
        Assert.NotEqual(default, actual.Updated);
        Assert.Equal(expected.Heroes!.Entries, actual.Heroes!.Entries);
        Assert.Equal(expected.HeroesDuos?.Entries, actual.HeroesDuos?.Entries);
        Assert.Equal(expected.Minions!.Entries, actual.Minions!.Entries);
        Assert.Equal(expected.Trinkets!.Entries, actual.Trinkets!.Entries);
        Assert.Equal(expected.MinionStats.Count, actual.MinionStats.Count);
        Assert.Equal(expected.Run, actual.Run);
        Assert.Equal(expected.Comps.Select(c => c.Id), actual.Comps.Select(c => c.Id));
        Assert.Equal(expected.CompsFetchedAt, actual.CompsFetchedAt);
    }

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

    /// <summary>Where the log ends now: with <see cref="LogSince"/>, only what a test wrote after this is read.</summary>
    private static int LogMark() => TestDataFolder.ReadLog().Length;

    /// <summary>
    /// The log written after <paramref name="mark"/>. Tests of this class run one at a time and no other class writes
    /// "Battlegrounds site" lines, so a line found here was written by the calling test.
    /// </summary>
    private static string LogSince(int mark)
    {
        var log = TestDataFolder.ReadLog();
        return log.Length >= mark ? log[mark..] : log;
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BgSite", name));

    /// <summary>A fixture answer with something changed in it.</summary>
    private static string Changed(string fixture, Action<JsonObject> change)
    {
        var answer = JsonNode.Parse(Fixture(fixture))!.AsObject();
        change(answer);
        return answer.ToJsonString();
    }

    private sealed record SeenRequest(HttpMethod Method, Uri Uri, IReadOnlyList<string> Headers, bool HasBody);

    /// <summary>The service: answers by path and query, can fail, hang or break an address, and records what it was asked.</summary>
    private sealed class FakeSite : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body, bool DeclaresLength)> _answers = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Uri> _answeredFrom = new(StringComparer.Ordinal);
        private readonly HashSet<string> _hanging = new(StringComparer.Ordinal);
        private readonly List<SeenRequest> _requests = [];
        private HttpStatusCode? _down;
        private bool _offline;
        private string? _everywhere;
        private string? _broken;

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

        /// <summary>The trimmed real answers, as the service sends them.</summary>
        public static FakeSite Real()
        {
            var site = new FakeSite();
            site.Answer(TierLists, Fixture("tier-lists.json"));
            site.Answer(Heroes, Fixture("heroes.json"));
            site.Answer(Duos, Fixture("heroes-duos.json"));
            site.Answer(MinionStats, Fixture("minion-stats.json"));
            site.Answer(Comps, Fixture("comps.json"));
            return site;
        }

        /// <summary>Every address answers <paramref name="status"/>, or the connection fails when it is null.</summary>
        public static FakeSite Down(HttpStatusCode? status) => new() { _down = status, _offline = status is null };

        /// <summary>Every address answers 200 with <paramref name="body"/>.</summary>
        public static FakeSite Everywhere(string body) => new() { _everywhere = body };

        /// <summary>
        /// Every request fails in a way a network error does not: "body" - the body stream throws as a broken gzip does,
        /// "send" - the handler throws, "disposed" - the handler was disposed.
        /// </summary>
        public static FakeSite Broken(string kind) => new() { _broken = kind };

        public void Answer(string path, string body, bool declaresLength = true) => _answers[path] = (HttpStatusCode.OK, body, declaresLength);

        public void Fail(string path, HttpStatusCode status) => _answers[path] = (status, "{\"detail\": \"down\"}", true);

        public void Hang(string path) => _hanging.Add(path);

        /// <summary>The answer of <paramref name="path"/> reports it was served by <paramref name="from"/> (after a redirect).</summary>
        public void AnswerFrom(string path, Uri from) => _answeredFrom[path] = from;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var asked = request.RequestUri!.PathAndQuery;
            // Under a base address with a path of its own, the service's addresses follow it.
            var route = asked[Math.Max(0, asked.IndexOf("/api/", StringComparison.Ordinal))..];
            lock (_requests)
            {
                _requests.Add(new SeenRequest(request.Method, request.RequestUri, [.. request.Headers.Select(h => h.Key)], request.Content is not null));
            }

            if (_hanging.Contains(route))
            {
                Hanging.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            switch (_broken)
            {
                case "send":
                    throw new InvalidOperationException("The handler is in a state no request can be sent in.");
                case "disposed":
                    throw new ObjectDisposedException(nameof(HttpClient));
                case "body":
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) };
            }

            if (_offline)
            {
                throw new HttpRequestException("No such host is known.");
            }

            if (_down is { } down)
            {
                return new HttpResponseMessage(down) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            }

            if (_everywhere is { } everywhere)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(everywhere, Encoding.UTF8, "application/json") };
            }

            if (!_answers.TryGetValue(route, out var answer))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var bytes = Encoding.UTF8.GetBytes(answer.Body);
            HttpContent content = answer.DeclaresLength ? new ByteArrayContent(bytes) : new StreamContent(new UnsizedStream(bytes));
            var response = new HttpResponseMessage(answer.Status) { Content = content };
            if (_answeredFrom.TryGetValue(route, out var from))
            {
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, from);
            }

            return response;
        }
    }

    /// <summary>A body whose length is not known ahead (a chunked answer).</summary>
    private sealed class UnsizedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    /// <summary>A body that fails while it is read the way a damaged compressed answer does.</summary>
    private sealed class BrokenStream : MemoryStream
    {
        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidDataException("The archive entry was compressed using an unsupported compression method.");

        public override int Read(Span<byte> buffer) => throw new InvalidDataException("The archive entry was compressed using an unsupported compression method.");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidDataException("The archive entry was compressed using an unsupported compression method.");

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            throw new InvalidDataException("The archive entry was compressed using an unsupported compression method.");
    }
}
