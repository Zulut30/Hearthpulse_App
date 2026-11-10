using System.Collections.Specialized;
using System.Text.Json;
using Hearthpulse.Core;
using Hearthpulse.Core.Api;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The tier lists of the main window, laid out as on hearthpulse.net: the site's win-rate tiers, the 95 % interval,
/// the matchup matrix, and the shared view model's flat tier list (a tier's head, then its archetypes), sorting,
/// filters changed in place, the side card built after the list, and the heat map (checked on the Avalonia build; the
/// Windows suite compares both builds).
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class MetaTierListTests : IDisposable
{
    private readonly string _db = TestDataFolder.Scratch("hp-meta-tiers") + ".db";
    private readonly HistoryStore _history;

    public MetaTierListTests()
    {
        _history = new HistoryStore(_db);
        Loc.Instance.SetLanguage(Loc.English);
    }

    public void Dispose() => TestDataFolder.TryDelete(_db);

    [Theory]
    [InlineData(0.611, "S")]
    [InlineData(0.53, "S")]
    [InlineData(0.5299, "A")]
    [InlineData(0.51, "A")]
    [InlineData(0.50, "B")]
    [InlineData(0.49, "B")]
    [InlineData(0.47, "C")]
    [InlineData(0.4699, "D")]
    public void Tiers_follow_the_site_scale(double winRate, string tier) => Assert.Equal(tier, MetaTiers.Of(winRate));

    [Fact]
    public void Tier_bounds_read_as_the_site_writes_them()
    {
        Assert.Equal(0.53, MetaTiers.Floor("S"));
        Assert.Equal(0.47, MetaTiers.Floor("C"));
        Assert.Equal(0.47, MetaTiers.Floor("D"));
        Assert.True(MetaTiers.IsLast("D"));
        Assert.False(MetaTiers.IsLast("C"));
        Assert.Equal(3.75, BattlegroundsAdvisor.TierBound("S"));
        Assert.Equal(4.65, BattlegroundsAdvisor.TierBound("D"));
    }

    [Fact]
    public void The_win_rate_interval_matches_the_site()
    {
        // hearthpulse.net: Zoolock 61.1 % over 320,540 games, "95% интервал 60,9–61,3%".
        var (low, high) = MetaTiers.Interval(0.611, 320_540)!.Value;
        Assert.Equal(0.609, low, 3);
        Assert.Equal(0.613, high, 3);
        Assert.Null(MetaTiers.Interval(0.5, 0));
        var (small, large) = MetaTiers.Interval(0.6, 50)!.Value;
        Assert.True(large - small > 0.2);
    }

    [Fact]
    public void Matchup_matrix_parses_with_rates_as_fractions_most_played_first()
    {
        using var doc = JsonDocument.Parse("""
            {"total_games": 1645364,
             "archetypes": [{"archetype": "C'Thun DH", "class": "DEMONHUNTER", "games": 161881, "winrate": 50.4, "popularity_pct": 9.84},
                            {"archetype": "Aggro Warlock", "class": "WARLOCK", "games": 310982, "winrate": 56.4, "popularity_pct": 18.9},
                            {"archetype": "", "games": 5}],
             "matchups": [{"archetype": "Aggro Warlock", "opponent": "C'Thun DH", "winrate": 55.2, "games": 35101},
                          {"archetype": "C'Thun DH", "opponent": "Aggro Warlock", "winrate": null}]}
            """);

        var (archetypes, cells, games) = HearthpulseData.ParseHsguruMatchups(doc.RootElement)!.Value;

        Assert.Equal(1645364, games);
        Assert.Equal(["Aggro Warlock", "C'Thun DH"], archetypes.Select(a => a.Key));
        Assert.Equal(0.564, archetypes[0].WinRate, 3);
        Assert.Equal(0.189, archetypes[0].Popularity, 3);
        var cell = Assert.Single(cells);
        Assert.Equal(0.552, cell.WinRate, 3);
        Assert.Equal(35101, cell.Games);
    }

    [Theory]
    [InlineData("legend", "legend")]
    [InlineData("top_5k", "top_5k")]
    [InlineData("top_500", "top_legend")]
    [InlineData("top_100", "top_legend")]
    [InlineData("diamond_to_legend", "diamond_to_legend")]
    [InlineData("anything", "all")]
    public void Matchup_ranks_are_the_ones_the_endpoint_takes(string rank, string expected) =>
        Assert.Equal(expected, HearthpulseData.MatchupRank(rank));

    [Fact]
    public void Tier_list_is_one_flat_list_a_head_then_its_archetypes_and_an_empty_tier_says_so()
    {
        var vm = Shown(Meta());

        // S to D: every tier's head, then its archetypes; tier A has none, so the next head follows it at once.
        Assert.Equal(
            ["head S", "Zoolock", "head A", "head B", "Control Warrior", "head C", "Quest Priest", "head D", "Big Rogue"],
            vm.MetaTierRows.Select(r => r switch { TierHeadRow h => "head " + h.Tier, MetaArchetypeRow a => a.Name, _ => r.ToString() }));
        var heads = vm.MetaTierRows.OfType<TierHeadRow>().ToList();
        Assert.Equal([false, true, false, false, false], heads.Select(h => h.IsEmpty));
        Assert.Equal("Tier S", heads[0].Title);
        Assert.StartsWith("from 53", heads[0].Caption, StringComparison.Ordinal);
        Assert.StartsWith("below 47", heads[4].Caption, StringComparison.Ordinal);
        Assert.Equal(Loc.T("Main.Meta.TierEmpty"), heads[1].Caption);

        // The two views show the same row objects, so a choice marks the archetype in both.
        Assert.All(vm.MetaTierRows.OfType<MetaArchetypeRow>(), row => Assert.Contains(vm.MetaArchetypes, r => ReferenceEquals(r, row)));
        Assert.False(vm.MetaLoading);
        Assert.False(vm.MetaEmpty);
        Assert.StartsWith("95% interval", vm.MetaArchetypes[0].WinRateTip, StringComparison.Ordinal);
        Assert.Equal(1, vm.MetaArchetypes[0].WinSign);
        Assert.Equal(-1, vm.MetaArchetypes.Single(r => r.Name == "Big Rogue").WinSign);
        Assert.Equal(0, vm.MetaArchetypes.Single(r => r.Name == "Control Warrior").WinSign);
    }

    [Fact]
    public void A_column_head_sorts_and_a_second_click_turns_the_order_round()
    {
        var vm = Shown(Meta());
        Assert.Equal("▼", vm.MetaArrows.WinRate);

        vm.SetMetaSort.Execute("Popularity");
        Assert.Equal(["Big Rogue", "Zoolock", "Control Warrior", "Quest Priest"], vm.MetaArchetypes.Select(r => r.Name));
        Assert.Equal([1, 2, 3, 4], vm.MetaArchetypes.Select(r => r.Rank));
        Assert.Equal("▼", vm.MetaArrows.Popularity);
        Assert.Equal(string.Empty, vm.MetaArrows.WinRate);
        Assert.Equal(1, vm.MetaArchetypes[0].PopularityFraction);

        vm.SetMetaSort.Execute("Popularity");
        Assert.Equal("Quest Priest", vm.MetaArchetypes[0].Name);
        Assert.Equal("▲", vm.MetaArrows.Popularity);

        vm.SetMetaSort.Execute("Name");
        Assert.Equal(["Big Rogue", "Control Warrior", "Quest Priest", "Zoolock"], vm.MetaArchetypes.Select(r => r.Name));
    }

    [Fact]
    public void Minimum_games_and_class_filter_the_list_without_a_new_download()
    {
        var requests = 0;
        var vm = Shown(Meta());
        vm.ConstructedMetaRequested += _ => requests++;

        vm.SetMetaMinGames.Execute("1000");
        Assert.Equal(["Big Rogue", "Zoolock"], vm.MetaArchetypes.Select(r => r.Name).Order());
        Assert.True(vm.MetaMinGames.Single(c => c.Key == "1000").Selected);

        vm.SetMetaClass.Execute("PRIEST");
        Assert.Empty(vm.MetaArchetypes);
        Assert.True(vm.MetaEmpty);
        Assert.Equal(5, vm.MetaTierRows.Count);
        Assert.All(vm.MetaTierRows, r => Assert.True(Assert.IsType<TierHeadRow>(r).IsEmpty));
        Assert.Equal(0, requests);

        vm.SetMetaRank.Execute("legend");
        Assert.Equal(1, requests);
        Assert.True(vm.MetaLoading);
    }

    [Fact]
    public void Choosing_an_archetype_marks_its_row_and_fills_the_side_card()
    {
        var vm = Shown(Meta());
        var rogue = vm.MetaArchetypes.Single(r => r.Name == "Big Rogue");

        vm.SelectArchetype.Execute(rogue);

        Assert.Same(rogue, vm.SelectedArchetype);
        Assert.True(rogue.IsSelected);
        Assert.Single(vm.MetaArchetypes, r => r.IsSelected);
        Assert.Equal("Big Rogue", vm.SelectedArchetypeTitle);

        // A rebuild (another sort) keeps the choice on the new rows.
        vm.SetMetaSort.Execute("Games");
        Assert.Equal("Big Rogue", vm.SelectedArchetype?.Name);
        Assert.True(vm.SelectedArchetype!.IsSelected);
    }

    [Fact]
    public void Sort_class_and_minimum_games_change_the_same_lists_in_place_and_keep_the_rows_that_stay()
    {
        var vm = Shown(Meta());
        var tiers = vm.MetaTierRows;
        var table = vm.MetaArchetypes;
        var actions = new List<NotifyCollectionChangedAction>();
        tiers.CollectionChanged += (_, e) => actions.Add(e.Action);
        table.CollectionChanged += (_, e) => actions.Add(e.Action);
        var zoolock = table.Single(r => r.Name == "Zoolock");
        var headS = tiers[0];
        Assert.Same(zoolock, vm.SelectedArchetype);

        // Fewer archetypes: Zoolock is still first with the same numbers, so its row is the same object where it stood
        // (its container stays), and so is the head of its tier; it is still the chosen one.
        vm.SetMetaMinGames.Execute("1000");
        Assert.Same(tiers, vm.MetaTierRows);
        Assert.Same(table, vm.MetaArchetypes);
        Assert.Same(zoolock, table[0]);
        Assert.Same(headS, tiers[0]);
        Assert.Same(zoolock, tiers[1]);
        Assert.Same(zoolock, vm.SelectedArchetype);
        Assert.True(zoolock.IsSelected);
        Assert.Equal(["Zoolock", "Big Rogue"], table.Select(r => r.Name));
        Assert.Equal(2, table[1].Rank);

        // Another order: the rows that changed place are other rows now, the heads stay; one row is chosen, the same archetype.
        vm.SetMetaSort.Execute("Popularity");
        Assert.Equal(["Big Rogue", "Zoolock"], table.Select(r => r.Name));
        Assert.Same(headS, tiers[0]);
        Assert.Equal("Zoolock", vm.SelectedArchetype?.Name);
        Assert.Single(table, r => r.IsSelected);
        Assert.Same(vm.SelectedArchetype, tiers[1]);

        vm.SetMetaClass.Execute("ROGUE");
        Assert.Equal(["Big Rogue"], table.Select(r => r.Name));
        Assert.Equal("Big Rogue", vm.SelectedArchetype?.Name);

        // Never a new list or a reset: the view keeps the visuals of every row that did not change.
        Assert.NotEmpty(actions);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void The_side_card_shows_the_name_and_summary_at_once_and_its_sections_when_the_view_runs_them()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        var queued = new List<Action>();
        vm.ArchetypeDetailScheduler = queued.Add;
        vm.ShowConstructedFormat("FT_STANDARD");
        vm.SetConstructedMeta(Meta());

        // The first archetype is chosen by itself: its name and summary are there, the sections wait for the view.
        Assert.Equal("Zoolock", vm.SelectedArchetypeTitle);
        Assert.Contains("56", vm.ArchetypeSummary, StringComparison.Ordinal);
        Assert.False(vm.ArchetypeDetailReady);
        Assert.Null(vm.DetailArchetype);
        Assert.Empty(vm.Mulligan);
        Assert.Empty(vm.ArchetypeDecks);

        Run();
        Assert.True(vm.ArchetypeDetailReady);
        Assert.Same(vm.SelectedArchetype, vm.DetailArchetype);
        Assert.Equal(["CS2_065"], vm.Mulligan.Select(m => m.CardId));
        Assert.Equal(["Zoolock"], vm.ArchetypeDecks.Select(d => d.Name));

        // Another choice (twice before the view got to it): the row, the name and the summary move at once, the previous
        // archetype's sections stay until the one build that replaces them.
        var zoolock = vm.SelectedArchetype;
        vm.SelectArchetype.Execute(vm.MetaArchetypes.Single(r => r.Name == "Control Warrior"));
        vm.SelectArchetype.Execute(vm.MetaArchetypes.Single(r => r.Name == "Big Rogue"));
        Assert.Equal("Big Rogue", vm.SelectedArchetypeTitle);
        Assert.Contains("46", vm.ArchetypeSummary, StringComparison.Ordinal);
        Assert.Same(zoolock, vm.DetailArchetype);
        Assert.Equal(["CS2_065"], vm.Mulligan.Select(m => m.CardId));
        Assert.Single(queued);

        Run();
        Assert.Equal("Big Rogue", vm.DetailArchetype?.Name);
        Assert.Equal(["EX1_134"], vm.Mulligan.Select(m => m.CardId));
        Assert.Empty(vm.ArchetypeDecks);
        Assert.True(vm.ArchetypeDetailReady);

        void Run()
        {
            var jobs = queued.ToList();
            queued.Clear();
            jobs.ForEach(job => job());
        }
    }

    [Fact]
    public void Without_a_view_the_side_card_is_built_at_once_and_a_row_reads_its_play_split_in_one_cell()
    {
        var vm = Shown(Meta());

        Assert.True(vm.ArchetypeDetailReady);
        Assert.Same(vm.SelectedArchetype, vm.DetailArchetype);
        Assert.Equal(["CS2_065"], vm.Mulligan.Select(m => m.CardId));

        // Zoolock has both numbers, the others neither: a dash, and the column shows because one row has them.
        Assert.Equal(vm.MetaArchetypes[0].First + " · " + vm.MetaArchetypes[0].Coin, vm.MetaArchetypes[0].PlaySplit);
        Assert.Equal("—", vm.MetaArchetypes[1].PlaySplit);
        Assert.True(vm.MetaHasPlaySplit);

        // Every row carries the list's flag, and its bars their lengths (of 56 and 44), computed once.
        Assert.All(vm.MetaArchetypes, r => Assert.True(r.ShowsPlaySplit));
        Assert.All(vm.MetaArchetypes, r => Assert.Equal((r.BarFraction * 56, r.PopularityFraction * 44), (r.WinBar, r.PopularityBar)));
        Assert.Equal(44, vm.MetaArchetypes.Max(r => r.PopularityBar));

        // A class without the split: its rows are other rows now, without the cell.
        vm.SetMetaClass.Execute("ROGUE");
        Assert.False(vm.MetaHasPlaySplit);
        Assert.All(vm.MetaArchetypes, r => Assert.False(r.ShowsPlaySplit));
    }

    [Fact]
    public void The_render_scene_has_the_states_the_data_copy_rarely_has()
    {
        var vm = Shown(MetaTierScene.States());

        // A tier without archetypes, the split by who went first with a row that has none, two archetypes in tier D.
        Assert.Equal(["A"], vm.MetaTierRows.OfType<TierHeadRow>().Where(h => h.IsEmpty).Select(h => h.Tier));
        Assert.True(vm.MetaHasPlaySplit);
        Assert.Contains(vm.MetaArchetypes, r => r.PlaySplit == "—");
        Assert.Contains(vm.MetaArchetypes, r => r.PlaySplit.Contains(" · ", StringComparison.Ordinal));
        Assert.Equal(2, vm.MetaArchetypes.Count(r => r.Tier == "D"));
        Assert.Equal(["S", "B", "C", "D"], vm.MetaArchetypes.Select(r => r.Tier).Distinct());
    }

    [Fact]
    public void Heat_map_has_a_row_and_column_per_archetype_and_blank_cells_without_games()
    {
        var vm = Shown(Meta());

        Assert.Equal(2, vm.MatchupHeads.Count);
        Assert.Equal(2, vm.MatchupMatrix.Count);
        var zoo = vm.MatchupMatrix[0];
        Assert.Equal("Zoolock", zoo.Name);
        Assert.Equal(["—", "64"], zoo.Cells.Select(c => c.Text));
        Assert.Equal(0, zoo.Cells[0].Heat);
        Assert.True(zoo.Cells[1].Edge > 0);
        Assert.InRange(zoo.Cells[1].Heat, 0.5, 0.6);
        Assert.Contains("210 games", zoo.Cells[1].Tip, StringComparison.Ordinal);
        Assert.True(vm.MatchupMatrix[1].Cells[0].Edge < 0);
        Assert.True(vm.HasMatchupMatrix);
    }

    [Fact]
    public void The_page_waits_with_loading_until_its_own_format_arrives()
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        string? asked = null;
        vm.ConstructedMetaRequested += f => asked = f;

        vm.ShowConstructedFormat("FT_WILD");
        Assert.Equal("wild", asked);
        Assert.True(vm.MetaLoading);
        Assert.False(vm.MetaEmpty);

        vm.SetConstructedMeta(Meta("standard"));
        Assert.Empty(vm.MetaArchetypes);
        Assert.True(vm.MetaLoading);

        vm.SetConstructedMeta(null);
        Assert.False(vm.MetaLoading);
        Assert.Equal(Loc.T("Main.Meta.Unavailable"), vm.ConstructedMetaStatus);
    }

    private MainViewModel Shown(ConstructedMeta meta)
    {
        var vm = new MainViewModel(_history, new AppSettings(), TestDataFolder.App);
        vm.ShowConstructedFormat("FT_STANDARD");
        vm.SetConstructedMeta(meta);
        return vm;
    }

    private static ConstructedMeta Meta(string format = "standard")
    {
        MetaArchetype Archetype(int id, string name, string heroClass, int games, double winRate, double share, string? keep = null) =>
            new(id, name, name, heroClass, games, (int)(games * winRate), winRate, share, 0, 0, 0, 0, [],
                keep is null ? [] : [new MetaMulliganCard(keep, 900, 0.8, 700, winRate + 0.03)]);

        var archetypes = new[]
        {
            // Zoolock alone has the split by who went first, a kept card and a list.
            Archetype(1, "Zoolock", "WARLOCK", 5000, 0.56, 0.20, "CS2_065") with { FirstGames = 2600, FirstWinRate = 0.58, CoinGames = 2400, CoinWinRate = 0.54 },
            Archetype(2, "Control Warrior", "WARRIOR", 800, 0.50, 0.10),
            Archetype(3, "Big Rogue", "ROGUE", 6000, 0.46, 0.30, "EX1_134"),
            Archetype(4, "Quest Priest", "PRIEST", 300, 0.48, 0.05),
        };
        var decks = new[] { new MetaDeck("AAECAf0GAA==", 1, "Zoolock", "WARLOCK", 900, 520, 0.578, new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc)) };
        var matchups = new MetaMatchups(
            [new MatchupArchetype("Zoolock", "Zoolock", "WARLOCK", 5000, 0.56, 0.2), new MatchupArchetype("Big Rogue", "Big Rogue", "ROGUE", 6000, 0.46, 0.3)],
            [new MatchupCell("Zoolock", "Big Rogue", 0.64, 210), new MatchupCell("Big Rogue", "Zoolock", 0.36, 210)],
            11000);
        return new ConstructedMeta(format, "legend", "past_week", new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), 12100, archetypes, decks) { Matchups = matchups };
    }
}

/// <summary>Archetype names in Russian are the ones hearthpulse.net shows, for every archetype its catalogues list.</summary>
public sealed class SiteArchetypeLabelTests
{
    [Theory]
    [InlineData("Zee C'Thun Shaman", "Граб'Зи К'тун Шаман")]
    [InlineData("C'Thun Warlock", "К'тун Чернокнижник")]
    [InlineData("C’Thun DH", "К'тун Охотник на демонов")]
    [InlineData("zoolock", "Зоолок")]
    [InlineData("Thief Priest", "Жрец на воровстве")]
    public void Known_archetypes_take_the_site_label(string english, string russian)
    {
        Assert.Equal(russian, ArchetypeNames.SiteLabel(english));
        Assert.Equal(russian, ArchetypeTranslations.Empty.RussianLabel(english));
    }

    [Fact]
    public void The_site_label_wins_over_the_dictionary_and_the_built_in_one()
    {
        // The dictionary and the old built-in list call it "Зи Шаман"; the site shows "Граб'Зи Шаман".
        var names = ArchetypeTranslations.Parse("""[{"name_en": "Zee Shaman", "name_ru": "Зи Шаман"}]""");
        Assert.Equal(ArchetypeNames.SiteLabel("Zee Shaman"), names.RussianLabel("Zee Shaman"));
        Assert.NotEqual("Зи Шаман", names.RussianLabel("Zee Shaman"));
        Assert.Equal(ArchetypeNames.SiteLabel("Zee Shaman"), ArchetypeNames.RussianLabel("Zee Shaman"));
    }

    [Fact]
    public void No_archetype_the_site_knows_keeps_its_English_name_in_Russian()
    {
        var english = new[]
        {
            "Zoolock", "C'Thun DH", "C'Thun Warlock", "Zee C'Thun Shaman", "Zee Shaman", "Handbuff DK", "Thief Priest", "Quest DH",
            "Aggro Paladin", "Harold DH",
        };
        foreach (var name in english)
        {
            var label = ArchetypeTranslations.Empty.RussianLabel(name);
            Assert.True(label.Any(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё'), $"{name} -> {label}");
            Assert.DoesNotContain(name, label, StringComparison.OrdinalIgnoreCase);
        }

        // Every site label is Russian text, not an English name passed through.
        var site = english.Select(ArchetypeNames.SiteLabel).ToList();
        Assert.All(site, Assert.NotNull);
    }
}
