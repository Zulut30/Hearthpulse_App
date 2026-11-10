using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Site;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Pool;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The Battlegrounds tier tabs on the lists of the site's service (TASK-2026-11-09-light-ui-site-stats, PC2): the
/// tiers and the order are the service's, each tab names its source, slice and date, a part that is missing empties
/// only its own tab, and heroes and trinkets change their MMR slice on demand. The view model of the Avalonia UI, on
/// the fixtures of <see cref="BgSiteStatsTests"/> (no request leaves the tests); the WPF twin shares the source, its
/// contract is in <c>Hearthpulse.Presentation.Tests/BgSiteTabsContractTests</c>.
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class BgSiteTabsTests
{
    private static readonly string[] Letters = ["S", "A", "B", "C", "D"];

    public BgSiteTabsTests() => LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(Loc.English));

    // ---- AC24: the service's tiers and order ----------------------------------------------------

    [Fact]
    public void Heroes_and_trinkets_are_the_services_entries_in_its_tiers_and_order() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        vm.SetBgSiteStats(stats);

        AssertFlat(stats.Heroes!, stats.Heroes!.Entries, vm.HeroTierRows, r => ((MetaHeroRow)r).CardId, r => ((MetaHeroRow)r).Tier, "Main.Bg.TierHeroes");
        // Trinkets: the same, except that the rows of one trinket (the service lists a tribe trinket once per tribe)
        // stand together inside their tier, at the place of the first of them.
        var together = stats.Trinkets!.Entries.GroupBy(e => e.Tier).SelectMany(tier => tier.GroupBy(e => e.CardId).SelectMany(rows => rows)).ToList();
        Assert.NotEqual(stats.Trinkets.Entries.Select(e => e.CardId), together.Select(e => e.CardId));
        AssertFlat(stats.Trinkets, together, vm.TrinketTierRows, r => ((MetaTrinketRow)r).CardId, r => ((MetaTrinketRow)r).Tier, "Main.Bg.TierTrinkets");

        // The fixture's heroes have no S tier: the service's own empty tier keeps its head, marked empty.
        var s = Assert.IsType<TierHeadRow>(vm.HeroTierRows[0]);
        Assert.Equal(("S", true, Loc.T("Main.Bg.TierEmpty")), (s.Tier, s.IsEmpty, s.Caption));

        // The rank is the entry's place in the service's list, through the tiers.
        Assert.Equal(Enumerable.Range(1, stats.Heroes.Entries.Count), vm.HeroTierRows.OfType<MetaHeroRow>().Select(r => r.Rank));
        Assert.Equal(Enumerable.Range(1, stats.Trinkets.Entries.Count), vm.TrinketTierRows.OfType<MetaTrinketRow>().Select(r => r.Rank).Order());
    });

    [Fact]
    public void A_tier_letter_is_the_services_not_one_computed_from_the_average_place() => LocBindingTests.Headless.Run(() =>
    {
        // The advisor would call 3.0 an S and 6.5 a D: the service says otherwise, and the service is shown.
        var vm = Model();
        vm.SetBgSiteStats(Stats(heroes: List(BgSiteList.Heroes, [Hero("H_BAD", "D", 3.0), Hero("H_GOOD", "S", 6.5)], tiers: ["S", "D"])));

        Assert.Equal(["S", "H_GOOD", "D", "H_BAD"], vm.HeroTierRows.Select(r => r is TierHeadRow head ? head.Tier : ((MetaHeroRow)r).CardId));
        Assert.Equal("S", vm.HeroTierRows.OfType<MetaHeroRow>().Single(r => r.CardId == "H_GOOD").Tier);
        Assert.NotEqual(BattlegroundsAdvisor.TierOf(6.5), vm.HeroTierRows.OfType<MetaHeroRow>().Single(r => r.CardId == "H_GOOD").Tier);
    });

    [Fact]
    public void Cards_show_the_minions_of_the_chosen_tavern_tier_in_the_services_tiers() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        vm.SetBgSiteStats(stats);

        var seen = 0;
        for (var tavern = 1; tavern <= 7; tavern++)
        {
            vm.SetCardTier.Execute(tavern.ToString(CultureInfo.InvariantCulture));
            var entries = stats.Minions!.Entries.Where(e => e.TavernTier == tavern).ToList();
            seen += entries.Count;
            if (entries.Count == 0)
            {
                // A tavern tier without entries: one empty state, no heads of tiers our own filter emptied.
                Assert.Empty(vm.CardTierRows);
                Assert.Equal((false, Loc.T("Main.Bg.NoCardData")), (vm.CardTab.HasRows, vm.CardTab.EmptyText));
                continue;
            }

            Assert.True(vm.CardTab.HasRows);
            Assert.Equal(entries.Select(e => e.CardId), vm.CardTierRows.OfType<MetaCardRow>().Select(r => r.CardId));
            Assert.Equal(Enumerable.Range(1, entries.Count), vm.CardTierRows.OfType<MetaCardRow>().Select(r => r.Rank));
            // Heads only of the tiers that have a row of this tavern tier, in the service's order.
            Assert.Equal(Letters.Where(l => entries.Any(e => e.Tier == l)), vm.CardTierRows.OfType<TierHeadRow>().Select(h => h.Tier));
            Assert.All(vm.CardTierRows.OfType<TierHeadRow>(), h => Assert.False(h.IsEmpty));
        }

        Assert.Equal(stats.Minions!.Entries.Count, seen);
    });

    [Fact]
    public void Spells_are_an_empty_state_until_the_service_gives_an_allowed_list_and_then_the_same_list() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        vm.SetBgSiteStats(stats);
        vm.SetCardTier.Execute("0");

        // The fixture's spells are Firestone's: Core gives no list, and nothing of it is on the tab.
        Assert.Null(stats.Spells);
        Assert.Empty(vm.CardTierRows);
        Assert.Equal((false, Loc.T("Main.Bg.SpellsEmpty")), (vm.CardTab.HasRows, vm.CardTab.EmptyText));

        // The day the service marks the list with an allowed source it shows with no app update: tiers, the tavern
        // tier as the detail, the popularity; without numbers the three stat columns are not there.
        vm.SetBgSiteStats(stats with
        {
            Spells = List(BgSiteList.Spells, [Entry("SPELL_1", "A", tavern: 3, pick: 0.123), Entry("SPELL_2", "B", tavern: 0, pick: null)], mmr: "TOP_50_PERCENT", range: "LAST_7_DAYS"),
        });
        var rows = vm.CardTierRows.OfType<MetaCardRow>().ToList();
        Assert.Equal(["SPELL_1", "SPELL_2"], rows.Select(r => r.CardId));
        Assert.Equal([Loc.F("Main.Bg.TavernLevel", 3), string.Empty], rows.Select(r => r.Detail));
        Assert.Equal([0.123.ToString("P1", CultureInfo.CurrentCulture), "—"], rows.Select(r => r.Popularity));
        Assert.All(rows, r => Assert.False(r.ShowsStats));
        Assert.False(vm.CardStatsShown);
        Assert.Contains(Loc.T("Main.Bg.TierSpells").Split('|')[0].Replace("{0:N0}", "1", StringComparison.Ordinal), vm.CardTierRows.OfType<TierHeadRow>().Select(h => h.Caption));
        Assert.StartsWith($"{HearthpulseData.Attribution} · {Loc.F("Main.Bg.SliceMmr", 0.5)} · {Loc.P("Main.Bg.RangeDays", 7)}", vm.CardTab.Source, StringComparison.Ordinal);
    });

    // ---- AC25: the card columns -----------------------------------------------------------------

    [Fact]
    public void A_minion_shows_impact_combat_win_rate_placement_with_and_without_and_popularity() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        vm.SetBgSiteStats(Stats(
            minions: List(BgSiteList.Minions,
            [
                Entry("BG_BEAST", "S", tavern: 1, pick: 0.05, games: 40_000),
                Entry("M_NEG", "A", tavern: 1, pick: 0.04),
                Entry("M_THIN", "A", tavern: 1, pick: 0.03),
                Entry("M_GAPS", "B", tavern: 1, pick: 0.02),
                Entry("M_NONE", "B", tavern: 1, pick: null),
            ]),
            minionStats:
            [
                new("BG_BEAST", 1, 1, false, 0.554, 0.5743, 0.0797, 28_615, 700_000, 3.17, 3.37),
                new("M_NEG", 2, 1, false, -0.151, 0.4, 0.01, 500, 900, 4.6, 4.45),
                new("M_THIN", 3, 1, false, 0.9, 0.7, 0.001, BattlegroundsMeta.MinCardGames - 1, 900, 3.0, 3.9),
                new("M_GAPS", 4, 1, false, null, null, null, null, null, 4.0, null),
            ]));

        var rows = vm.CardTierRows.OfType<MetaCardRow>().ToDictionary(r => r.CardId);
        var beast = rows["BG_BEAST"];
        Assert.Equal("Sewer Rat", beast.Name);
        // Tribes from the card database, games from the numbers; the tier list's own games are another run.
        Assert.Equal($"{Races.Display("BEAST", "enUS")} · {Races.Display("MECHANICAL", "enUS")} · {Loc.P("Main.Bg.TribeGames", 28_615)}", beast.Detail);
        Assert.Equal(("+" + 0.55.ToString("0.00", CultureInfo.CurrentCulture), 1), (beast.Impact, beast.ImpactSign));
        Assert.Equal(0.5743.ToString("P1", CultureInfo.CurrentCulture), beast.CombatWinRate);
        Assert.Equal($"{3.17.ToString("0.00", CultureInfo.CurrentCulture)} · {3.37.ToString("0.00", CultureInfo.CurrentCulture)}", beast.PlacementSplit);
        // The popularity of the numbers, not the tier list's 5 %.
        Assert.Equal(0.0797.ToString("P1", CultureInfo.CurrentCulture), beast.Popularity);

        // A minus is the typographic one; the sign colours the number.
        Assert.Equal(("−" + 0.15.ToString("0.00", CultureInfo.CurrentCulture), -1), (rows["M_NEG"].Impact, rows["M_NEG"].ImpactSign));
        // A thin sample keeps its row and its number; only the sign's colour goes.
        Assert.Equal(("+" + 0.9.ToString("0.00", CultureInfo.CurrentCulture), 0), (rows["M_THIN"].Impact, rows["M_THIN"].ImpactSign));
        // No number: a dash; one of the two placements: a dash in its place; popularity falls back on the tier list.
        var gaps = rows["M_GAPS"];
        Assert.Equal(("—", 0, "—"), (gaps.Impact, gaps.ImpactSign, gaps.CombatWinRate));
        Assert.Equal($"{4.0.ToString("0.00", CultureInfo.CurrentCulture)} · —", gaps.PlacementSplit);
        Assert.Equal(0.02.ToString("P1", CultureInfo.CurrentCulture), gaps.Popularity);
        // No numbers at all for the card, and no popularity anywhere: dashes.
        var none = rows["M_NONE"];
        Assert.Equal(("—", "—", "—", "—", string.Empty), (none.Impact, none.CombatWinRate, none.PlacementSplit, none.Popularity, none.Detail));

        Assert.True(vm.CardStatsShown);
        Assert.All(rows.Values, r => Assert.True(r.ShowsStats));
    });

    [Fact]
    public void Without_minion_numbers_the_tier_list_stays_and_the_stat_columns_go() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        vm.SetBgSiteStats(stats with { MinionStats = new Dictionary<string, BgSiteCardStat>(), Run = null });
        var tavern = stats.Minions!.Entries[0].TavernTier;
        vm.SetCardTier.Execute(tavern.ToString(CultureInfo.InvariantCulture));

        var rows = vm.CardTierRows.OfType<MetaCardRow>().ToList();
        Assert.NotEmpty(rows);
        Assert.False(vm.CardStatsShown);
        Assert.All(rows, r => Assert.False(r.ShowsStats));
        Assert.Equal(
            stats.Minions.Entries.Where(e => e.TavernTier == tavern).Select(e => e.PickRate!.Value.ToString("P1", CultureInfo.CurrentCulture)),
            rows.Select(r => r.Popularity));
        // The run names the slice: without it the line has the source and the date only.
        Assert.Equal(Loc.F("Main.Bg.MetaUpdated", HearthpulseData.Attribution, stats.Minions.FetchedAt.ToLocalTime()), vm.CardTab.Source);
    });

    // ---- AC26: comps ----------------------------------------------------------------------------

    [Fact]
    public void Comps_are_the_services_by_tier_then_the_pools_tribes_then_an_empty_state() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        var comps = new BgSiteComp[]
        {
            new("c1", "Свинобразы", string.Empty, "A", [], ["BG_BEAST", "UNKNOWN_CARD"], null, null, "medium", "Play spells."),
            new("c2", "Мехи", string.Empty, "S", [], [], null, null, null, null),
            new("c3", "Без буквы", string.Empty, null, [], ["BG_BEAST"], null, null, "hard", string.Empty),
            new("c4", "Демоны", string.Empty, "A", [], [], null, null, "easy", "Buy demons."),
        };
        var at = new DateTime(2026, 11, 9, 17, 0, 12, DateTimeKind.Utc);
        vm.SetBgSiteStats(Stats(comps: comps, compsAt: at));

        // Tiers S to D, inside a tier the service's order; a comp without a letter goes last, under its own head.
        Assert.Equal(["S", "Мехи", "A", "Свинобразы", "Демоны", "–", "Без буквы"], vm.CompTiers.Select(r => r is TierHeadRow h ? h.Tier : ((MetaCompRow)r).Name));
        var heads = vm.CompTiers.OfType<TierHeadRow>().ToList();
        Assert.Equal([Loc.P("Main.Bg.TierComps", 1), Loc.P("Main.Bg.TierComps", 2), Loc.P("Main.Bg.TierComps", 1)], heads.Select(h => h.Caption));
        Assert.Equal(Loc.T("Main.Bg.CompsNoTier"), heads[2].Title);

        var rows = vm.CompTiers.OfType<MetaCompRow>().ToDictionary(r => r.Name);
        // Core cards by their names in the card database; a card it lacks is left out, and without any the line is not there.
        Assert.Equal(Loc.T("Main.Bg.Core") + "Sewer Rat", rows["Свинобразы"].Core);
        Assert.Equal(string.Empty, rows["Мехи"].Core);
        Assert.Equal([Loc.T("Main.Bg.CompDifficulty.Medium"), string.Empty, Loc.T("Main.Bg.CompDifficulty.Hard"), Loc.T("Main.Bg.CompDifficulty.Easy")],
            new[] { "Свинобразы", "Мехи", "Без буквы", "Демоны" }.Select(n => rows[n].Side));
        // The tips are not shown until the owner decides (The_comp_tips_are_shown_or_not_by_one_switch).
        Assert.Equal((string.Empty, string.Empty), (rows["Свинобразы"].Tip, rows["Мехи"].Tip));
        Assert.Equal((Loc.T("Main.Bg.Comps"), Loc.T("Main.Bg.CompCaptionComps")), (vm.CompTabLabel, vm.CompCaption));
        // The service names no slice for comps: source and date.
        Assert.Equal(Loc.F("Main.Bg.MetaUpdated", HearthpulseData.Attribution, at.ToLocalTime()), vm.CompTab.Source);

        // No comps of the service, tribes of the pool: the tribes, best first, without heads and without a date.
        vm.SetMeta(Meta(Tribes));
        vm.SetBgSiteStats(Stats());
        Assert.Equal([Races.Display("MECHANICAL", "enUS"), Loc.T("Main.Bg.Mixed"), Races.Display("BEAST", "enUS")], vm.CompTiers.Select(r => ((MetaCompRow)r).Name));
        var tribe = (MetaCompRow)vm.CompTiers[0];
        Assert.Equal(Loc.F("Main.Bg.TribeSide", 3.5.ToString("0.00", CultureInfo.CurrentCulture), Loc.P("Main.Bg.TribeGames", 1200)), tribe.Side);
        Assert.Equal((string.Empty, Loc.F("Main.Bg.TribeRates", 0.61, 0.17)), (tribe.Core, tribe.Tip));
        Assert.Equal((Loc.T("Main.Bg.Tribes"), Loc.T("Main.Bg.CompCaptionTribes")), (vm.CompTabLabel, vm.CompCaption));
        Assert.Equal((true, HearthpulseData.Attribution), (vm.CompTab.HasRows, vm.CompTab.Source));

        // Neither: the tab says the list is unavailable.
        vm.SetMeta(Meta([]));
        Assert.Empty(vm.CompTiers);
        Assert.Equal((false, Loc.T("Main.Bg.ListUnavailable"), Loc.T("Main.Bg.Comps")), (vm.CompTab.HasRows, vm.CompTab.EmptyText, vm.CompTabLabel));
    });

    [Fact]
    public void The_comp_tips_are_shown_or_not_by_one_switch() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        vm.SetBgSiteStats(Stats(comps: [new("c1", "Свинобразы", string.Empty, "A", [], ["BG_BEAST"], null, null, "medium", "Play spells.")]));

        // Off until the owner decides on these texts (legal L-M1): the constant is the switch, read once. The comp is
        // there without its tip: name, difficulty, core cards.
        Assert.False(MainViewModel.CompTipsByDefault);
        Assert.Equal(MainViewModel.CompTipsByDefault, vm.ShowCompTips);
        var row = vm.CompTiers.OfType<MetaCompRow>().Single();
        Assert.Equal(("Свинобразы", Loc.T("Main.Bg.CompDifficulty.Medium"), Loc.T("Main.Bg.Core") + "Sewer Rat", string.Empty), (row.Name, row.Side, row.Core, row.Tip));
        Assert.DoesNotContain("Play spells.", string.Join("|", vm.CompTiers.Select(r => r.ToString())), StringComparison.Ordinal);

        // On: the tip is on the row, the rest is as it was.
        vm.ShowCompTips = true;
        Assert.Equal(row with { Tip = "Play spells." }, vm.CompTiers.OfType<MetaCompRow>().Single());

        vm.ShowCompTips = false;
        Assert.Equal(row, vm.CompTiers.OfType<MetaCompRow>().Single());
    });

    // ---- AC27: Solo / Duos ----------------------------------------------------------------------

    [Fact]
    public void Duos_shows_the_duos_list_its_top_two_and_the_players_duos_games() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var history = new HistoryStore(TestDataFolder.Scratch("hp-bg-site") + ".db");
        var solo = stats.Heroes!.Entries[0].CardId;
        var duos = stats.HeroesDuos!.Entries[0].CardId;
        history.Add(Game("s1", "Battlegrounds", solo, 2));
        history.Add(Game("s2", "Battlegrounds", solo, 5));
        history.Add(Game("d1", "Duos", duos, 1));
        var vm = Model(history);
        vm.SetBgSiteStats(stats);
        vm.BgTabShown("Heroes");

        // Solo: the solo list, "Top 4", the player's solo games.
        Assert.True(vm.ShowBgMode);
        Assert.Equal((Loc.T("Main.Table.Top4"), Loc.T("Main.Table.Top4Tip")), (vm.HeroTopLabel, vm.HeroTopTip));
        Assert.Equal($"2 · {3.5.ToString("0.00", CultureInfo.CurrentCulture)}", vm.HeroTierRows.OfType<MetaHeroRow>().Single(r => r.CardId == solo).Personal);
        Assert.Contains($" · {Loc.T("Main.Bg.Solo")} · ", vm.HeroTab.Source, StringComparison.Ordinal);

        vm.SetBgMode.Execute("Duos");
        Settle(vm);
        AssertFlat(stats.HeroesDuos, stats.HeroesDuos.Entries, vm.HeroTierRows, r => ((MetaHeroRow)r).CardId, r => ((MetaHeroRow)r).Tier, "Main.Bg.TierHeroes");
        Assert.Equal((Loc.T("Main.Table.Top2"), Loc.T("Main.Table.Top2Tip")), (vm.HeroTopLabel, vm.HeroTopTip));
        Assert.Equal($"1 · {1.0.ToString("0.00", CultureInfo.CurrentCulture)}", vm.HeroTierRows.OfType<MetaHeroRow>().Single(r => r.CardId == duos).Personal);
        Assert.Contains($" · {Loc.T("Main.Mode.Duos")} · ", vm.HeroTab.Source, StringComparison.Ordinal);
        // The top share of a Duos list is the top half of four places, as the service sends it.
        var first = stats.HeroesDuos.Entries[0];
        Assert.Equal(first.Top4Rate!.Value.ToString("P0", CultureInfo.CurrentCulture), vm.HeroTierRows.OfType<MetaHeroRow>().First().Top);

        // The switch is for My stats and Heroes only.
        foreach (var (tab, shown) in new[] { ("Mine", true), ("Heroes", true), ("Comps", false), ("Trinkets", false), ("Cards", false) })
        {
            vm.BgTabShown(tab);
            Assert.Equal(shown, vm.ShowBgMode);
        }
    });

    [Fact]
    public void Without_a_duos_list_heroes_stay_solo_and_the_switch_is_for_my_stats_only() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures() with { HeroesDuos = null };
        var history = new HistoryStore(TestDataFolder.Scratch("hp-bg-site") + ".db");
        var hero = stats.Heroes!.Entries[0].CardId;
        history.Add(Game("s1", "Battlegrounds", hero, 3));
        history.Add(Game("d1", "Duos", hero, 1));
        history.Add(Game("d2", "Duos", hero, 1));
        var vm = Model(history);
        vm.SetBgSiteStats(stats);

        vm.BgTabShown("Mine");
        Assert.True(vm.ShowBgMode);
        vm.SetBgMode.Execute("Duos");
        Settle(vm);
        vm.BgTabShown("Heroes");

        // Duos is chosen on My stats; Heroes has no switch and shows solo: list, head, line and the "You" column.
        Assert.False(vm.ShowBgMode);
        AssertFlat(stats.Heroes, stats.Heroes.Entries, vm.HeroTierRows, r => ((MetaHeroRow)r).CardId, r => ((MetaHeroRow)r).Tier, "Main.Bg.TierHeroes");
        Assert.Equal(Loc.T("Main.Table.Top4"), vm.HeroTopLabel);
        Assert.Contains($" · {Loc.T("Main.Bg.Solo")} · ", vm.HeroTab.Source, StringComparison.Ordinal);
        Assert.Equal($"1 · {3.0.ToString("0.00", CultureInfo.CurrentCulture)}", vm.HeroTierRows.OfType<MetaHeroRow>().Single(r => r.CardId == hero).Personal);
        // Solo's four slices, not the two of Duos.
        Assert.Equal([50, 20, 5, 1], vm.BracketOptions.Select(o => o.Value));
    });

    // ---- AC28: states ---------------------------------------------------------------------------

    [Fact]
    public void Tabs_say_loading_then_unavailable_and_a_missing_part_empties_only_its_own_tab() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        BgTabState[] Tabs() => [vm.HeroTab, vm.CompTab, vm.TrinketTab, vm.CardTab];

        // Before the first load.
        Assert.All(Tabs(), t => Assert.Equal((false, Loc.T("Main.Bg.CommunityLoading"), string.Empty), (t.HasRows, t.EmptyText, t.Source)));
        var loading = vm.HeroTab.EmptyGlyph;
        vm.BgTabShown("Heroes");
        Assert.False(vm.HasBracketChoice);

        // The load had nothing: no network and no cache.
        vm.SetBgSiteStats(null);
        Assert.All(Tabs(), t => Assert.Equal((false, Loc.T("Main.Bg.CommunityUnavailable")), (t.HasRows, t.EmptyText)));
        Assert.NotEqual(loading, vm.HeroTab.EmptyGlyph);
        Assert.False(vm.HasBracketChoice);

        // One part missing: its tab alone says so, without a picker; the others have their rows.
        var stats = Fixtures();
        vm.SetBgSiteStats(stats with { Heroes = null });
        Assert.Equal((false, Loc.T("Main.Bg.ListUnavailable")), (vm.HeroTab.HasRows, vm.HeroTab.EmptyText));
        Assert.False(vm.HasBracketChoice);
        Assert.All(new[] { vm.CompTab, vm.TrinketTab, vm.CardTab }, t => Assert.True(t.HasRows));

        vm.SetBgSiteStats(stats with { Trinkets = null, Minions = null });
        Assert.True(vm.HeroTab.HasRows);
        Assert.Equal(Loc.T("Main.Bg.ListUnavailable"), vm.TrinketTab.EmptyText);
        Assert.Equal(Loc.T("Main.Bg.ListUnavailable"), vm.CardTab.EmptyText);
        vm.BgTabShown("Trinkets");
        Assert.False(vm.HasBracketChoice);

        // And the next load brings it back.
        vm.SetBgSiteStats(stats);
        Assert.All(Tabs(), t => Assert.True(t.HasRows));
        Assert.All(Tabs(), t => Assert.Equal(string.Empty, t.EmptyText));
    });

    [Fact]
    public void Each_tab_names_the_source_the_slice_of_the_shown_list_and_its_date() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        vm.BgClock = () => stats.Updated.AddHours(1);
        vm.SetBgSiteStats(stats);

        string Line(DateTime at, params string[] parts) => Loc.F("Main.Bg.MetaUpdated", string.Join(" · ", parts), at.ToLocalTime());
        var top50 = Loc.F("Main.Bg.SliceMmr", 0.5);
        var patch = Loc.T("Main.Bg.RangePatch");
        Assert.Equal(Line(stats.Heroes!.FetchedAt, HearthpulseData.Attribution, Loc.T("Main.Bg.Solo"), top50, patch), vm.HeroTab.Source);
        // The default trinket list is the top 1 % of the last week: the line says what the list says.
        Assert.Equal(Line(stats.Trinkets!.FetchedAt, HearthpulseData.Attribution, Loc.F("Main.Bg.SliceMmr", 0.01), Loc.P("Main.Bg.RangeDays", 7)), vm.TrinketTab.Source);
        // The minions' list names no slice: the run of the numbers does.
        Assert.Equal(Line(stats.Minions!.FetchedAt, HearthpulseData.Attribution, top50, patch), vm.CardTab.Source);
        Assert.Equal(Line(stats.CompsFetchedAt!.Value, HearthpulseData.Attribution), vm.CompTab.Source);

        // The source is the app's own name for its data, never the pool's line, and never the service's code words.
        Assert.All(new[] { vm.HeroTab, vm.TrinketTab, vm.CardTab, vm.CompTab }, t =>
        {
            Assert.StartsWith("Hearthpulse data", t.Source, StringComparison.Ordinal);
            Assert.DoesNotContain(BattlegroundsMeta.PoolAttribution, t.Source, StringComparison.Ordinal);
            Assert.DoesNotContain("hsreplay", t.Source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("TOP_", t.Source, StringComparison.Ordinal);
            Assert.DoesNotContain("PATCH", t.Source, StringComparison.Ordinal);
        });

        // A slice or a period the app does not know is left out; a list without a date has no "updated".
        vm.SetBgSiteStats(stats with { Heroes = stats.Heroes with { Mmr = "LEGEND_ONLY", TimeRange = "SINCE_FOREVER", FetchedAt = default } });
        Assert.Equal($"{HearthpulseData.Attribution} · {Loc.T("Main.Bg.Solo")}", vm.HeroTab.Source);
    });

    [Fact]
    public void Numbers_from_an_old_cache_show_their_date_and_past_two_weeks_are_marked_out_of_date() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        var fetched = stats.Heroes!.FetchedAt;

        // Thirteen days old: the date alone tells.
        vm.BgClock = () => fetched.AddDays(13);
        vm.SetBgSiteStats(stats);
        var line = vm.HeroTab.Source;
        Assert.Equal(
            Loc.F("Main.Bg.MetaUpdated", $"{HearthpulseData.Attribution} · {Loc.T("Main.Bg.Solo")} · {Loc.F("Main.Bg.SliceMmr", 0.5)} · {Loc.T("Main.Bg.RangePatch")}", fetched.ToLocalTime()),
            line);
        Assert.DoesNotContain(Loc.T("Main.Bg.Stale"), line, StringComparison.Ordinal);

        // Past fourteen days (the service stopped giving the list, the old file answers for good): marked, on every tab with a date.
        vm.BgClock = () => fetched.AddDays(15);
        vm.SetBgSiteStats(stats);
        Assert.Equal(line + " · " + Loc.T("Main.Bg.Stale"), vm.HeroTab.Source);
        Assert.All(new[] { vm.TrinketTab, vm.CardTab, vm.CompTab }, t => Assert.EndsWith(" · " + Loc.T("Main.Bg.Stale"), t.Source, StringComparison.Ordinal));
        Assert.Equal(MainViewModel.BgStaleAfter, TimeSpan.FromDays(14));
    });

    [Fact]
    public void A_language_change_rebuilds_the_lines_heads_and_details_without_a_restart() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        vm.SetBgSiteStats(stats);
        vm.BgTabShown("Heroes");
        var english = (vm.HeroTab.Source, ((TierHeadRow)vm.HeroTierRows[0]).Caption, vm.BracketOptions[0].Label, vm.HeroTopLabel);

        Loc.Instance.SetLanguage(Loc.Russian);
        try
        {
            Settle(vm);
            Assert.Contains("Соло", vm.HeroTab.Source, StringComparison.Ordinal);
            Assert.Contains("по MMR", vm.HeroTab.Source, StringComparison.Ordinal);
            Assert.Contains("обновлено", vm.HeroTab.Source, StringComparison.Ordinal);
            Assert.Equal("В этом тире сейчас пусто.", ((TierHeadRow)vm.HeroTierRows[0]).Caption);
            Assert.Equal("Тир S", ((TierHeadRow)vm.HeroTierRows[0]).Title);
            Assert.StartsWith("Топ", vm.BracketOptions[0].Label, StringComparison.Ordinal);
            Assert.Equal("Топ-4", vm.HeroTopLabel);
            Assert.Contains("за 7 дней", vm.TrinketTab.Source, StringComparison.Ordinal);
            // A card the database lacks takes the service's Russian name in the Russian interface.
            var entry = stats.Trinkets!.Entries[0];
            Assert.Equal(entry.NameRu, vm.TrinketTierRows.OfType<MetaTrinketRow>().First().Name);
            Assert.NotEqual(entry.NameRu, entry.NameEn);

            vm.SetBgSiteStats(null);
            Assert.Equal("Данные сообщества сейчас недоступны. Hearthpulse попробует позже.", vm.HeroTab.EmptyText);
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
        }

        Settle(vm);
        vm.SetBgSiteStats(stats);
        Assert.Equal(english, (vm.HeroTab.Source, ((TierHeadRow)vm.HeroTierRows[0]).Caption, vm.BracketOptions[0].Label, vm.HeroTopLabel));
        Assert.Equal(stats.Trinkets!.Entries[0].NameEn, vm.TrinketTierRows.OfType<MetaTrinketRow>().First().Name);
    });

    // ---- slices on demand -----------------------------------------------------------------------

    [Fact]
    public void The_picker_offers_the_slices_of_the_open_tabs_list_with_its_default_chosen() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        var site = new FakeSlices();
        vm.BgSiteSlices = site.Loader;
        vm.SetBgSiteStats(Fixtures());

        // My stats, Comps and Cards have one slice or none: no picker.
        foreach (var tab in new[] { "Mine", "Comps", "Cards" })
        {
            vm.BgTabShown(tab);
            Assert.False(vm.HasBracketChoice);
            Assert.Empty(vm.BracketOptions);
        }

        // Heroes: four, widest first, the first one is "Top 50 %" and chosen.
        vm.BgTabShown("Heroes");
        Assert.True(vm.HasBracketChoice);
        Assert.Equal([50, 20, 5, 1], vm.BracketOptions.Select(o => o.Value));
        Assert.Equal([true, false, false, false], vm.BracketOptions.Select(o => o.IsSelected));
        Assert.Equal(Loc.F("Main.Bracket.Top", 0.5), vm.BracketOptions[0].Label);
        Assert.Equal(0.2.ToString("P0", CultureInfo.CurrentCulture), vm.BracketOptions[1].Label);

        // Trinkets: the same four, and the site's default is the narrowest one, the last segment.
        vm.BgTabShown("Trinkets");
        Assert.Equal([50, 20, 5, 1], vm.BracketOptions.Select(o => o.Value));
        Assert.Equal([false, false, false, true], vm.BracketOptions.Select(o => o.IsSelected));

        // Nothing was asked of the service: the default slices come with the refresh.
        Assert.Empty(site.Asked);
    });

    [Fact]
    public void A_picked_slice_keeps_the_shown_rows_while_it_loads_then_replaces_them_in_the_same_list() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var top20 = Slice("heroes-solo-top20.json");
        var vm = Model();
        var site = new FakeSlices();
        vm.BgSiteSlices = site.Loader;
        vm.SetBgSiteStats(stats);
        vm.BgTabShown("Heroes");
        var list = vm.HeroTierRows;
        var before = list.ToList();
        var line = vm.HeroTab.Source;

        vm.SetBracket.Execute(20);

        // Asked once, for solo top 20 %; the segment is marked at once; the rows stay as they were and the line still
        // describes them, with "loading top 20 %" in place of the date.
        Assert.Equal(["heroes solo TOP_20_PERCENT"], site.Asked);
        Assert.Equal([false, true, false, false], vm.BracketOptions.Select(o => o.IsSelected));
        Assert.Equal(before, list);
        Assert.True(vm.HeroTab.HasRows);
        Assert.Equal(Loc.F("Main.Bg.SliceLoadingMode", $"{HearthpulseData.Attribution} · {Loc.T("Main.Bg.Solo")} · {Loc.F("Main.Bg.SliceMmr", 0.5)} · {Loc.T("Main.Bg.RangePatch")}", Loc.T("Main.Bg.Solo"), 0.2), vm.HeroTab.Source);
        Assert.NotEqual(line, vm.HeroTab.Source);

        // The slice came: its rows, in the same collection, and the line is the slice's own.
        site.Answer(top20);
        Assert.Same(list, vm.HeroTierRows);
        AssertFlat(top20, top20.Entries, vm.HeroTierRows, r => ((MetaHeroRow)r).CardId, r => ((MetaHeroRow)r).Tier, "Main.Bg.TierHeroes");
        Assert.Contains(Loc.F("Main.Bg.SliceMmr", 0.2), vm.HeroTab.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("…", vm.HeroTab.Source, StringComparison.Ordinal);

        // Back to the default: from memory, at once, with no request.
        vm.SetBracket.Execute(50);
        Assert.Single(site.Asked);
        Assert.Equal(before, list);
        Assert.Equal(line, vm.HeroTab.Source);
    });

    [Fact]
    public void A_slice_without_data_is_an_empty_state_with_the_picker_still_there_and_the_other_tabs_untouched() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        var site = new FakeSlices();
        vm.BgSiteSlices = site.Loader;
        vm.SetBgSiteStats(Fixtures());
        vm.BgTabShown("Heroes");
        var trinkets = vm.TrinketTierRows.ToList();

        vm.SetBracket.Execute(5);
        site.Answer(null);

        // No reason is known (no data, or the network failed): one neutral text, the earlier rows do not stay under
        // another slice's name, and the choice stays marked so the player sees what was asked.
        Assert.Empty(vm.HeroTierRows);
        Assert.Equal((false, Loc.F("Main.Bg.SliceEmpty", 0.05)), (vm.HeroTab.HasRows, vm.HeroTab.EmptyText));
        Assert.True(vm.HasBracketChoice);
        Assert.Equal([false, false, true, false], vm.BracketOptions.Select(o => o.IsSelected));
        Assert.Equal(trinkets, vm.TrinketTierRows);
        Assert.True(vm.CardTab.HasRows);

        // One click back: the default slice needs no network.
        vm.SetBracket.Execute(50);
        Assert.True(vm.HeroTab.HasRows);
        Assert.Single(site.Asked);
    });

    [Fact]
    public void The_last_choice_wins_and_an_earlier_answer_is_dropped() => LocBindingTests.Headless.Run(() =>
    {
        var top20 = Slice("heroes-solo-top20.json");
        var top1 = Slice("heroes-solo-top1.json");
        var vm = Model();
        var site = new FakeSlices();
        vm.BgSiteSlices = site.Loader;
        vm.SetBgSiteStats(Fixtures());
        vm.BgTabShown("Heroes");

        vm.SetBracket.Execute(20);
        vm.SetBracket.Execute(1);
        Assert.Equal(["heroes solo TOP_20_PERCENT", "heroes solo TOP_1_PERCENT"], site.Asked);
        // The line names the slice asked for last.
        Assert.EndsWith(Loc.F("Main.Bg.SliceLoadingMode", "x", Loc.T("Main.Bg.Solo"), 0.01)[1..], vm.HeroTab.Source, StringComparison.Ordinal);

        // The late answer first, then the one that was dropped: only the last choice is on screen.
        site.Answer(top1, request: 1);
        site.Answer(top20, request: 0);
        Assert.Equal(top1.Entries.Select(e => e.CardId), vm.HeroTierRows.OfType<MetaHeroRow>().Select(r => r.CardId));
        Assert.Contains(Loc.F("Main.Bg.SliceMmr", 0.01), vm.HeroTab.Source, StringComparison.Ordinal);
    });

    [Fact]
    public void The_percent_is_shared_by_solo_and_duos_and_falls_to_the_default_where_duos_has_no_such_slice() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var vm = Model();
        var site = new FakeSlices();
        vm.BgSiteSlices = site.Loader;
        vm.SetBgSiteStats(stats);
        vm.BgTabShown("Heroes");

        // Solo top 5 %, then Duos: Duos has 50 and 20 only, so the choice becomes 50 %, shown from memory.
        vm.SetBracket.Execute(5);
        site.Answer(Slice("heroes-solo-top1.json"));
        vm.SetBgMode.Execute("Duos");
        Settle(vm);
        Assert.Equal([50, 20], vm.BracketOptions.Select(o => o.Value));
        Assert.Equal([true, false], vm.BracketOptions.Select(o => o.IsSelected));
        Assert.Equal(stats.HeroesDuos!.Entries.Select(e => e.CardId), vm.HeroTierRows.OfType<MetaHeroRow>().Select(r => r.CardId));
        Assert.Single(site.Asked);

        // Back in solo it stays 50 %.
        vm.SetBgMode.Execute("Battlegrounds");
        Settle(vm);
        Assert.Equal([true, false, false, false], vm.BracketOptions.Select(o => o.IsSelected));
        Assert.Equal(stats.Heroes!.Entries.Select(e => e.CardId), vm.HeroTierRows.OfType<MetaHeroRow>().Select(r => r.CardId));
        Assert.Single(site.Asked);

        // Top 20 % is kept by Duos: its own slice is asked for.
        vm.SetBracket.Execute(20);
        site.Answer(Slice("heroes-solo-top20.json"));
        vm.SetBgMode.Execute("Duos");
        Settle(vm);
        Assert.Equal("heroes duos TOP_20_PERCENT", site.Asked[^1]);
        Assert.Equal([false, true], vm.BracketOptions.Select(o => o.IsSelected));
        // The rows on screen are still solo top 20 %, and the same percent is loading: what loads is named by its mode,
        // or the line would say that the very slice it describes is loading.
        Assert.StartsWith($"{HearthpulseData.Attribution} · {Loc.T("Main.Bg.Solo")} · {Loc.F("Main.Bg.SliceMmr", 0.2)}", vm.HeroTab.Source, StringComparison.Ordinal);
        Assert.EndsWith(Loc.F("Main.Bg.SliceLoadingMode", "x", Loc.T("Main.Mode.Duos"), 0.2)[1..], vm.HeroTab.Source, StringComparison.Ordinal);
        Assert.Contains(Loc.T("Main.Mode.Duos"), Loc.F("Main.Bg.SliceLoadingMode", "x", Loc.T("Main.Mode.Duos"), 0.2), StringComparison.Ordinal);

        // The Duos slice came: its rows, under its own name.
        site.Answer(Slice("heroes-duos.json") with { Mmr = "TOP_20_PERCENT" });
        Assert.StartsWith($"{HearthpulseData.Attribution} · {Loc.T("Main.Mode.Duos")} · {Loc.F("Main.Bg.SliceMmr", 0.2)}", vm.HeroTab.Source, StringComparison.Ordinal);
        Assert.DoesNotContain("…", vm.HeroTab.Source, StringComparison.Ordinal);
    });

    [Fact]
    public void A_refresh_asks_again_for_the_slice_still_chosen_without_a_loading_line() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var top50 = TrinketSlice("trinkets-top50.json");
        var vm = Model();
        var site = new FakeSlices();
        vm.BgSiteSlices = site.Loader;
        vm.SetBgSiteStats(stats);
        vm.BgTabShown("Trinkets");

        vm.SetBracket.Execute(50);
        Assert.Equal(["trinkets TOP_50_PERCENT"], site.Asked);
        site.Answer(top50);
        AssertFlat(top50, top50.Entries, vm.TrinketTierRows, r => ((MetaTrinketRow)r).CardId, r => ((MetaTrinketRow)r).Tier, "Main.Bg.TierTrinkets");
        var rows = vm.TrinketTierRows.ToList();
        var line = vm.TrinketTab.Source;

        // The six-hour refresh: the chosen slice is asked for again, after the load (the host calls this when
        // LoadAsync is done), the rows and the line stay until it answers.
        vm.SetBgSiteStats(stats);
        Assert.Equal(2, site.Asked.Count);
        Assert.Equal(rows, vm.TrinketTierRows);
        Assert.Equal(line, vm.TrinketTab.Source);
        site.Answer(top50);
        Assert.Equal(rows, vm.TrinketTierRows);

        // The heroes tab kept its default slice all along: nothing asked for it.
        Assert.DoesNotContain(site.Asked, a => a.StartsWith("heroes", StringComparison.Ordinal));
    });

    [Fact]
    public void Lists_that_come_back_after_a_load_without_any_say_loading_for_the_slice_still_chosen() => LocBindingTests.Headless.Run(() =>
    {
        var stats = Fixtures();
        var top20 = Slice("heroes-solo-top20.json");
        var vm = Model();
        var site = new FakeSlices();
        vm.BgSiteSlices = site.Loader;
        vm.SetBgSiteStats(stats);
        vm.BgTabShown("Heroes");
        vm.SetBracket.Execute(20);
        site.Answer(top20);
        Assert.True(vm.HeroTab.HasRows);

        // A load that had nothing (no network, no cache): "unavailable", nothing asked.
        vm.SetBgSiteStats(null);
        Assert.Equal((false, Loc.T("Main.Bg.CommunityUnavailable")), (vm.HeroTab.HasRows, vm.HeroTab.EmptyText));
        Assert.Single(site.Asked);

        // The lists are back: the slice still chosen is asked for again, and until it answers the tab has no rows to
        // keep - it says "loading", not that the list is unavailable.
        vm.SetBgSiteStats(stats);
        Assert.Equal(2, site.Asked.Count);
        Assert.Equal((false, Loc.T("Main.Bg.CommunityLoading")), (vm.HeroTab.HasRows, vm.HeroTab.EmptyText));
        Assert.Equal([false, true, false, false], vm.BracketOptions.Select(o => o.IsSelected));
        site.Answer(top20);
        Assert.True(vm.HeroTab.HasRows);
        Assert.Equal(top20.Entries.Select(e => e.CardId), vm.HeroTierRows.OfType<MetaHeroRow>().Select(r => r.CardId));

        // A slice that has no data stays "no data" through a refresh: no flicker of "loading" every six hours.
        vm.SetBracket.Execute(5);
        site.Answer(null);
        var none = vm.HeroTab;
        vm.SetBgSiteStats(stats);
        Assert.Equal(none, vm.HeroTab);
        Assert.Equal(Loc.F("Main.Bg.SliceEmpty", 0.05), vm.HeroTab.EmptyText);
    });

    [Fact]
    public void A_slice_loader_that_throws_leaves_no_data_not_a_tab_that_loads_for_ever() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        vm.BgSiteSlices = new BgSiteSliceLoader((_, _) => Task.FromException<BgSiteTierList?>(new InvalidOperationException("slice")), _ => Task.FromResult<BgSiteTierList?>(null));
        vm.SetBgSiteStats(Fixtures());
        vm.BgTabShown("Heroes");

        vm.SetBracket.Execute(20);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((false, Loc.F("Main.Bg.SliceEmpty", 0.2)), (vm.HeroTab.HasRows, vm.HeroTab.EmptyText));
        vm.SetBracket.Execute(50);
        Assert.True(vm.HeroTab.HasRows);
    });

    [Fact]
    public void The_pools_tribes_do_not_show_on_Comps_before_the_first_load_of_the_services_lists() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // The community stats (with the pool's tribes) usually come first: the tab stays "Comps" and says "loading",
        // so it does not open as "Tribes" and then turn into "Comps" under the player's eyes.
        vm.SetMeta(Meta(Tribes));
        Assert.Empty(vm.CompTiers);
        Assert.Equal((false, Loc.T("Main.Bg.CommunityLoading"), Loc.T("Main.Bg.Comps")), (vm.CompTab.HasRows, vm.CompTab.EmptyText, vm.CompTabLabel));
        Assert.DoesNotContain(nameof(MainViewModel.CompTabLabel), raised);

        // The service has comps: they show, and "Tribes" was never on screen.
        vm.SetBgSiteStats(Stats(comps: [new("c1", "Мехи", string.Empty, "S", [], [], null, null, null, null)]));
        Assert.Equal(["S", "Мехи"], vm.CompTiers.Select(r => r is TierHeadRow h ? h.Tier : ((MetaCompRow)r).Name));
        Assert.DoesNotContain(nameof(MainViewModel.CompTabLabel), raised);

        // The service has none (or could not be loaded at all): now the tribes stand in.
        vm.SetBgSiteStats(null);
        Assert.Equal(3, vm.CompTiers.Count);
        Assert.Equal(Loc.T("Main.Bg.Tribes"), vm.CompTabLabel);
        Assert.Contains(nameof(MainViewModel.CompTabLabel), raised);
    });

    // ---- AC31: the lists change in place --------------------------------------------------------

    [Fact]
    public void A_tavern_tier_or_a_sort_changes_the_rows_inside_the_same_collection() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        vm.SetBgSiteStats(Stats(
            heroes: List(BgSiteList.Heroes,
            [
                Hero("H1", "A", 4.4, top4: 0.50, first: 0.10, pick: 0.30),
                Hero("H2", "A", 4.1, top4: 0.55, first: 0.20, pick: 0.10),
                Hero("H3", "A", null, top4: null, first: null, pick: null),
                Hero("H4", "A", 4.2, top4: 0.52, first: 0.15, pick: 0.60),
                Hero("H5", "B", 4.9, top4: 0.40, first: 0.05, pick: 0.20),
            ], tiers: ["A", "B"]),
            minions: List(BgSiteList.Minions, [Entry("M1", "A", tavern: 1, pick: 0.1), Entry("M2", "A", tavern: 2, pick: 0.1), Entry("M3", "B", tavern: 2, pick: 0.1)])));
        var (heroes, cards) = (vm.HeroTierRows, vm.CardTierRows);
        string[] Order() => vm.HeroTierRows.Select(r => r is TierHeadRow h ? h.Tier : ((MetaHeroRow)r).CardId).ToArray();
        string[] service = ["A", "H1", "H2", "H3", "H4", "B", "H5"];

        // The service's order, and no arrow, until a head is clicked.
        Assert.Equal(service, Order());
        Assert.Equal(new HeroSortArrows(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty), vm.HeroArrows);

        // First click: the natural way (place low to high); heroes without the number go last; ranks stay the service's.
        var b = vm.HeroTierRows[5];
        vm.SetHeroSort.Execute("Placement");
        Assert.Same(heroes, vm.HeroTierRows);
        Assert.Equal(["A", "H2", "H4", "H1", "H3", "B", "H5"], Order());
        Assert.Equal("▲", vm.HeroArrows.Placement);
        Assert.Equal([2, 4, 1, 3, 5], vm.HeroTierRows.OfType<MetaHeroRow>().Select(r => r.Rank));
        // A row that stands where it stood is the same object: its container is kept.
        Assert.Same(b, vm.HeroTierRows[5]);

        // Second click: turned round, the hero without the number still last. Third: the service's order again.
        vm.SetHeroSort.Execute("Placement");
        Assert.Equal(["A", "H1", "H4", "H2", "H3", "B", "H5"], Order());
        Assert.Equal("▼", vm.HeroArrows.Placement);
        vm.SetHeroSort.Execute("Placement");
        Assert.Equal(service, Order());
        Assert.Equal(string.Empty, vm.HeroArrows.Placement);

        // The rates start high to low.
        vm.SetHeroSort.Execute("Pick");
        Assert.Equal(["A", "H4", "H1", "H2", "H3", "B", "H5"], Order());
        Assert.Equal("▼", vm.HeroArrows.Pick);
        vm.SetHeroSort.Execute("Top4");
        Assert.Equal(["A", "H2", "H4", "H1", "H3", "B", "H5"], Order());
        vm.SetHeroSort.Execute("First");
        Assert.Equal(["A", "H2", "H4", "H1", "H3", "B", "H5"], Order());
        vm.SetHeroSort.Execute("Name");
        Assert.Equal(service, Order());
        Assert.Equal("▲", vm.HeroArrows.Name);
        vm.SetHeroSort.Execute("Games");
        Assert.Equal("▲", vm.HeroArrows.Name);

        // A new list keeps the sort.
        vm.SetHeroSort.Execute("Pick");
        vm.SetBgSiteStats(Stats(heroes: List(BgSiteList.Heroes, [Hero("H1", "A", 4.4, pick: 0.30), Hero("H4", "A", 4.2, pick: 0.60)], tiers: ["A"])));
        Assert.Same(heroes, vm.HeroTierRows);
        Assert.Equal(["A", "H4", "H1"], Order());

        // The tavern tier: the same collection, other rows.
        vm.SetBgSiteStats(Stats(minions: List(BgSiteList.Minions, [Entry("M1", "A", tavern: 1, pick: 0.1), Entry("M2", "A", tavern: 2, pick: 0.1), Entry("M3", "B", tavern: 2, pick: 0.1)])));
        var head = vm.CardTierRows[0];
        Assert.Equal(["M1"], vm.CardTierRows.OfType<MetaCardRow>().Select(r => r.CardId));
        vm.SetCardTier.Execute("2");
        Assert.Same(cards, vm.CardTierRows);
        Assert.Equal(["M2", "M3"], vm.CardTierRows.OfType<MetaCardRow>().Select(r => r.CardId));
        Assert.Equal(head, vm.CardTierRows[0]);
    });

    [Fact]
    public void Rows_of_one_trinket_for_different_tribes_stand_together_and_name_their_tribe() => LocBindingTests.Headless.Run(() =>
    {
        BgSiteTierEntry Compass(string tier, double place, string? race) => new("COMPASS", 1, tier, "Компас", "Compass", 0, place, 0.2, null, null, 5, "LARGE", 2, race);
        BgSiteTierEntry Other(string id, string tier, double place) => new(id, 2, tier, id, id, 0, place, 0.1, null, null, 5, "SMALL", 1);
        var vm = Model();
        vm.SetBgSiteStats(Stats(trinkets: List(BgSiteList.Trinkets,
        [
            Other("T1", "S", 2.0), Compass("S", 2.8, "Undead"), Other("T2", "S", 2.85), Compass("S", 2.9, "Mech"), Other("T3", "S", 2.95),
            Compass("A", 3.2, "Naga-ish"), Other("T4", "A", 3.3), Compass("A", 3.5, null),
        ], tiers: ["S", "A"])));

        // Inside a tier the rows of one trinket follow the first of them, in the service's order; the others keep
        // theirs. The tiers are the service's: a trinket's rows in another tier stay there.
        var rows = vm.TrinketTierRows.ToList();
        Assert.Equal(["S", "T1", "COMPASS", "COMPASS", "T2", "T3", "A", "COMPASS", "COMPASS", "T4"], rows.Select(r => r is TierHeadRow h ? h.Tier : ((MetaTrinketRow)r).CardId));
        var compass = rows.OfType<MetaTrinketRow>().Where(r => r.CardId == "COMPASS").ToList();
        // The rank is still the row's place in the service's list.
        Assert.Equal([2, 4, 6, 8], compass.Select(r => r.Rank));
        Assert.Equal(new[] { 2.8, 2.9, 3.2, 3.5 }.Select(p => p.ToString("0.00", CultureInfo.CurrentCulture)), compass.Select(r => r.Placement));

        // The tribe leads the line under the name, in the interface's language ("Mech" is the app's MECHANICAL); a
        // tribe the app does not know is shown as sent; a row without one has size and cost only.
        var rest = $"{Loc.T("Main.Bg.TrinketLarge")} · {Loc.F("Main.Bg.TrinketCost", 2)}";
        Assert.Equal(
            [$"{Races.Display("UNDEAD", "enUS")} · {rest}", $"{Races.Display("MECHANICAL", "enUS")} · {rest}", $"Naga-ish · {rest}", rest],
            compass.Select(r => r.Detail));
        // No two rows of the list read the same.
        Assert.Equal(rows.Count, rows.Select(r => r is MetaTrinketRow t ? t.Name + "|" + t.Detail : r.ToString()).Distinct().Count());

        Loc.Instance.SetLanguage(Loc.Russian);
        try
        {
            Assert.StartsWith("Механизм · ", vm.TrinketTierRows.OfType<MetaTrinketRow>().Single(r => r.Rank == 4).Detail, StringComparison.Ordinal);
        }
        finally
        {
            Loc.Instance.SetLanguage(Loc.English);
        }

        // The service's own answer (fixture): the two rows of its tribe trinket, far apart in tier D, stand together.
        vm.SetBgSiteStats(Fixtures());
        var d = vm.TrinketTierRows.OfType<MetaTrinketRow>().Where(r => r.Tier == "D").ToList();
        Assert.Equal(["BG30_MagicItem_426t", "BG30_MagicItem_426t"], d.Take(2).Select(r => r.CardId));
        Assert.Equal([Races.Display("QUILBOAR", "enUS"), Races.Display("MECHANICAL", "enUS")], d.Take(2).Select(r => r.Detail.Split(" · ")[0]));
        Assert.Equal(4, d.Count);
    });

    [Fact]
    public void Trinkets_show_size_cost_one_decimal_of_pick_rate_and_never_the_lower_bound_of_games() => LocBindingTests.Headless.Run(() =>
    {
        var vm = Model();
        vm.SetBgSiteStats(Stats(trinkets: List(BgSiteList.Trinkets,
        [
            new("T1", 1, "S", "Магнит", "Fridge Magnet", 0, 2.0, 0.055, null, null, 4321, "SMALL", 2),
            new("T2", 2, "S", "Кубок", "Trophy", 0, 3.5, null, null, null, 0, "LARGE", null),
            new("T3", 3, "A", "Камень", "Stone", 0, null, 0.5, null, null, 77, null, null),
        ], mmr: "TOP_1_PERCENT", range: "LAST_7_DAYS", note: "games is the minimum integer sample")));

        var rows = vm.TrinketTierRows.OfType<MetaTrinketRow>().ToList();
        Assert.Equal(["Fridge Magnet", "Trophy", "Stone"], rows.Select(r => r.Name));
        Assert.Equal([$"{Loc.T("Main.Bg.TrinketSmall")} · {Loc.F("Main.Bg.TrinketCost", 2)}", Loc.T("Main.Bg.TrinketLarge"), string.Empty], rows.Select(r => r.Detail));
        Assert.Equal([2.0.ToString("0.00", CultureInfo.CurrentCulture), 3.5.ToString("0.00", CultureInfo.CurrentCulture), "—"], rows.Select(r => r.Placement));
        Assert.Equal([0.055.ToString("P1", CultureInfo.CurrentCulture), "—", 0.5.ToString("P1", CultureInfo.CurrentCulture)], rows.Select(r => r.PickRate));
        // The best place has the full bar, the worst a short one, no number has none.
        Assert.Equal([1, 0.15, 0], rows.Select(r => Math.Round(r.BarFraction, 6)));
        // "Games" of a trinket is a lower bound, not a sample: it is on no row and in no line.
        var shown = string.Join("|", vm.TrinketTierRows.Select(r => r.ToString())) + vm.TrinketTab.Source;
        Assert.DoesNotContain("4321", shown.Replace(",", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("minimum", shown, StringComparison.Ordinal);
    });

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>
    /// The flat list of a tab against the service's list: for each tier of the list in its order a head and then the
    /// tier's entries as sent; a tier that is empty in the service's own list keeps its head, marked empty.
    /// </summary>
    private static void AssertFlat(BgSiteTierList list, IReadOnlyList<BgSiteTierEntry> shown, IReadOnlyList<object> rows, Func<object, string> id, Func<object, string> tier, string countKey)
    {
        var expected = new List<string>();
        foreach (var letter in list.AvailableTiers)
        {
            var entries = shown.Where(e => e.Tier == letter).ToList();
            expected.Add(entries.Count > 0 ? $"head {letter} {Loc.P(countKey, entries.Count)}" : $"head {letter} {Loc.T("Main.Bg.TierEmpty")} empty");
            expected.AddRange(entries.Select(e => $"{e.Tier} {e.CardId}"));
        }

        Assert.NotEmpty(shown);
        Assert.Equal(expected, rows.Select(r => r is TierHeadRow h ? $"head {h.Tier} {h.Caption}{(h.IsEmpty ? " empty" : string.Empty)}" : $"{tier(r)} {id(r)}"));
        Assert.All(rows.OfType<TierHeadRow>(), h => Assert.Equal(Loc.F("Main.Meta.TierTitle", h.Tier), h.Title));
    }

    /// <summary>A view model with a few cards and its first history refresh done (it builds the "You" column).</summary>
    private static MainViewModel Model(HistoryStore? history = null)
    {
        var vm = new MainViewModel(history ?? new HistoryStore(TestDataFolder.Scratch("hp-bg-site") + ".db"), new AppSettings(), TestDataFolder.App);
        vm.SetCards(CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(
            """
            [
              { "id": "BG_BEAST", "dbfId": 1, "name": "Sewer Rat", "type": "MINION", "races": ["BEAST", "MECHANICAL"], "techLevel": 1, "isBattlegroundsPoolMinion": true }
            ]
            """))));
        // The fixtures' dates are fixed: "now" is too, or the lines would turn "out of date" two weeks later.
        vm.BgClock = () => Fetched.AddHours(6);
        Settle(vm);
        return vm;
    }

    /// <summary>
    /// Waits for the history refresh the last call started (a new mode, a new language): its end comes back to this
    /// thread through the dispatcher, so the lists are never touched from two threads.
    /// </summary>
    private static void Settle(MainViewModel vm)
    {
        var refresh = vm.RefreshAsync();
        for (var i = 0; i < 5000 && !refresh.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.True(refresh.IsCompletedSuccessfully);
    }

    private static GameRecord Game(string key, string mode, string hero, int place) =>
        new() { Key = key, Mode = mode, HeroCardId = hero, Placement = place, Result = place <= 4 ? "Win" : "Loss", StartedAt = new DateTime(2026, 11, 1, 12, 0, 0), EndedAt = new DateTime(2026, 11, 1, 12, 20, 0) };

    /// <summary>The service's answers as <see cref="BgSiteStatsTests"/> has them (trimmed real ones), read as a refresh reads them.</summary>
    internal static BgSiteStats Fixtures()
    {
        using var lists = Document("tier-lists.json");
        using var heroes = Document("heroes.json");
        using var duos = Document("heroes-duos.json");
        using var minions = Document("minion-stats.json");
        using var comps = Document("comps.json");
        var source = BgSiteParser.HeroesSource(heroes.RootElement);
        Assert.NotNull(source);
        return BgSiteClient.Build(lists.RootElement, heroes.RootElement, duos.RootElement, source, minions.RootElement, comps.RootElement);
    }

    private static BgSiteTierList Slice(string fixture)
    {
        using var slice = Document(fixture);
        return BgSiteParser.ParseHeroSlice(slice.RootElement, fixture, "hsreplay_battlegrounds_heroes")!;
    }

    private static BgSiteTierList TrinketSlice(string fixture)
    {
        using var slice = Document(fixture);
        return BgSiteParser.ParseList(slice.RootElement, BgSiteList.Trinkets, heroes: null, "trinkets")!;
    }

    private static JsonDocument Document(string fixture) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "BgSite", fixture)));

    internal static BgSiteStats Stats(
        BgSiteTierList? heroes = null,
        BgSiteTierList? duos = null,
        BgSiteTierList? minions = null,
        BgSiteTierList? trinkets = null,
        IReadOnlyList<BgSiteCardStat>? minionStats = null,
        IReadOnlyList<BgSiteComp>? comps = null,
        DateTime? compsAt = null) =>
        new(heroes, duos, minions, null, trinkets,
            (minionStats ?? []).ToDictionary(s => s.CardId),
            new Dictionary<string, BgSiteCardStat>(),
            comps ?? [],
            minionStats is null ? null : new BgSiteRun("TOP_50_PERCENT", "CURRENT_BATTLEGROUNDS_PATCH", Fetched),
            Fetched,
            compsAt);

    private static readonly DateTime Fetched = new(2026, 11, 9, 17, 1, 9, DateTimeKind.Utc);

    internal static BgSiteTierList List(BgSiteList kind, IReadOnlyList<BgSiteTierEntry> entries, IReadOnlyList<string>? tiers = null, string? mmr = null, string? range = null, string? note = null) =>
        new(kind, entries, tiers ?? Letters, Fetched, "hsreplay", mmr, range, note);

    internal static BgSiteTierEntry Hero(string id, string tier, double? place, double? top4 = 0.5, double? first = 0.1, double? pick = 0.2) =>
        new(id, 0, tier, id, id, 0, place, pick, top4, first, null);

    internal static BgSiteTierEntry Entry(string id, string tier, int tavern, double? pick, int? games = null) =>
        new(id, 0, tier, id, id, tavern, null, pick, null, null, games);

    private static readonly PoolTribe[] Tribes =
    [
        new("BEAST", 800, 5.0, 0.38, 0.06),
        new("MECHANICAL", 1200, 3.5, 0.61, 0.17),
        new("MIXED", 300, 4.25, 0.5, 0.1),
    ];

    private static BattlegroundsMeta Meta(IReadOnlyList<PoolTribe> tribes) =>
        new(new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>(), [], [], new Dictionary<string, MetaTrinket>(),
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(), new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc))
        {
            Tribes = tribes,
        };

    /// <summary>The slice loaders of the hosts, answered by hand: what was asked, in order, and each request's answer.</summary>
    private sealed class FakeSlices
    {
        private readonly List<TaskCompletionSource<BgSiteTierList?>> _requests = [];

        public List<string> Asked { get; } = [];

        public BgSiteSliceLoader Loader => new(
            (duos, mmr) => Ask($"heroes {(duos ? "duos" : "solo")} {mmr}"),
            mmr => Ask($"trinkets {mmr}"));

        /// <summary>Answers a request (the last one unless told) and lets the view model take the answer.</summary>
        public void Answer(BgSiteTierList? list, int? request = null)
        {
            _requests[request ?? (_requests.Count - 1)].SetResult(list);
            Dispatcher.UIThread.RunJobs();
        }

        private Task<BgSiteTierList?> Ask(string what)
        {
            Asked.Add(what);
            // The client answers on a pool thread: the continuation must come back by itself.
            var request = new TaskCompletionSource<BgSiteTierList?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(request);
            return request.Task;
        }
    }
}
