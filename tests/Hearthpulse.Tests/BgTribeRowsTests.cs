using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Pool;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// Battlegrounds "Comps" without comps: stats that have the pool's tribes instead show them as the tab's rows, under the
/// tab name "Tribes". The Avalonia view model, and its tab (the WPF twin and the two-UI contract:
/// <c>Hearthpulse.Presentation.Tests/BgTribeRowsContractTests</c>).
/// </summary>
[Collection(nameof(GlobalLanguage))]
public sealed class BgTribeRowsTests
{
    public BgTribeRowsTests() => LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(Loc.English));

    [Fact]
    public void Stats_with_tribes_and_no_comps_list_the_tribes_best_first()
    {
        var vm = Model();
        vm.SetMeta(Meta([], Tribes));

        Assert.True(vm.HasCompTiers);
        Assert.Equal([1, 2, 3], vm.CompTiers.Select(r => r.Rank));
        // Best average placement first; "MIXED" is the app's own word, the others are the game's tribe names.
        Assert.Equal([Races.Display("MECHANICAL", "enUS"), Loc.T("Main.Bg.Mixed"), Races.Display("BEAST", "enUS")], vm.CompTiers.Select(r => r.Name));
        Assert.Equal(new[] { 3.5, 4.25, 5.0 }.Select(p => p.ToString("0.00", CultureInfo.CurrentCulture)), vm.CompTiers.Select(r => r.Placement));
        Assert.Equal(BattlegroundsAdvisor.TierOf(3.5), vm.CompTiers[0].Power);
        Assert.Equal(Loc.P("Main.Bg.TribeGames", 1200), vm.CompTiers[0].Tribes);
        Assert.Equal(Loc.F("Main.Bg.TribeRates", 0.61, 0.17), vm.CompTiers[0].Commit);
        // The best row has the full bar, the worst keeps a visible one.
        Assert.Equal(1, vm.CompTiers[0].BarFraction, 6);
        Assert.Equal(0.15, vm.CompTiers[2].BarFraction, 6);
        Assert.All(vm.CompTiers, r => Assert.Equal(string.Empty, r.KeyCards));

        Assert.Equal(Loc.T("Main.Bg.Tribes"), vm.CompTabLabel);
        Assert.Equal(Loc.T("Main.Bg.CompCaptionTribes"), vm.CompCaption);
    }

    [Fact]
    public void Stats_with_comps_list_the_comps_whatever_tribes_they_have()
    {
        var vm = Model();
        vm.SetMeta(Meta([Comp("beasts", "Beasts", 3.2), Comp("mechs", "Mechs", 2.9)], Tribes));

        Assert.Equal(["Mechs", "Beasts"], vm.CompTiers.Select(r => r.Name));
        Assert.Equal(Loc.T("Main.Bg.Comps"), vm.CompTabLabel);
        Assert.Equal(Loc.T("Main.Bg.CompCaptionComps"), vm.CompCaption);
    }

    [Fact]
    public void The_tab_name_and_hint_follow_the_stats_and_the_language()
    {
        var vm = Model();
        // The view model also raises from its history refresh, off this thread.
        var raised = new ConcurrentQueue<string?>();
        vm.PropertyChanged += (_, e) => raised.Enqueue(e.PropertyName);

        // Nothing loaded, and stats with neither comps nor tribes: the tab is "Comps", with nothing in it.
        Assert.Equal(Loc.T("Main.Bg.Comps"), vm.CompTabLabel);
        vm.SetMeta(Meta([], []));
        Assert.False(vm.HasCompTiers);
        Assert.Equal(Loc.T("Main.Bg.Comps"), vm.CompTabLabel);
        Assert.DoesNotContain(nameof(MainViewModel.CompTabLabel), raised.ToArray());

        vm.SetMeta(Meta([], Tribes));
        Assert.Contains(nameof(MainViewModel.CompTabLabel), raised.ToArray());
        Assert.Contains(nameof(MainViewModel.CompCaption), raised.ToArray());
        Assert.Contains(nameof(MainViewModel.HasCompTiers), raised.ToArray());

        raised.Clear();
        vm.SetMeta(Meta([Comp("mechs", "Mechs", 2.9)], Tribes));
        Assert.Contains(nameof(MainViewModel.CompTabLabel), raised.ToArray());
        Assert.Equal(Loc.T("Main.Bg.Comps"), vm.CompTabLabel);

        vm.SetMeta(Meta([], Tribes));
        LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(Loc.Russian));
        try
        {
            Assert.Equal("Расы", vm.CompTabLabel);
            Assert.Equal(Loc.T("Main.Bg.CompCaptionTribes"), vm.CompCaption);
            Assert.Equal("Смешанные", vm.CompTiers[1].Name);
        }
        finally
        {
            LocBindingTests.Headless.Run(() => Loc.Instance.SetLanguage(Loc.English));
        }
    }

    [Fact]
    public void Avalonia_comps_tab_is_named_after_its_rows() => LocBindingTests.Headless.Run(() =>
    {
        var theme = new StyleInclude(new Uri("avares://Hearthpulse.UI/"))
        {
            Source = new Uri("avares://Hearthpulse.UI/Themes/Theme.axaml"),
        };
        var app = Application.Current!;
        app.Styles.Add(theme);
        var window = new Window { Width = 1260, Height = 840 };
        var vm = Model();
        try
        {
            var view = new MainView { DataContext = vm };
            window.Content = view;
            view.ShowPage("Battlegrounds");
            Dispatcher.UIThread.RunJobs();
            var tab = view.GetLogicalDescendants().OfType<RadioButton>().Single(r => r.Name == "TabComps");
            Assert.Equal(Loc.T("Main.Bg.Comps"), tab.Content);
            Assert.Equal(Loc.T("Main.Bg.CompCaptionComps"), ToolTip.GetTip(tab));

            vm.SetMeta(Meta([], Tribes));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Loc.T("Main.Bg.Tribes"), tab.Content);
            Assert.Equal(Loc.T("Main.Bg.CompCaptionTribes"), ToolTip.GetTip(tab));

            // The rows are on the tab, and the "no data yet" line is not.
            view.ShowBattlegroundsTab("Comps");
            Dispatcher.UIThread.RunJobs();
            var card = view.GetLogicalDescendants().OfType<Border>().Single(b => b.Name == "CompTierCard");
            Assert.True(card.IsVisible);
            Assert.Equal(3, card.GetLogicalDescendants().OfType<ItemsControl>().Single(i => ReferenceEquals(i.ItemsSource, vm.CompTiers)).ItemCount);
            var empty = card.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == vm.CompEmptyText && t.Text.Length > 0);
            Assert.False(empty.IsVisible);

            vm.SetMeta(Meta([], []));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Loc.T("Main.Bg.Comps"), tab.Content);
            Assert.True(empty.IsVisible);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(theme);
            Dispatcher.UIThread.RunJobs();
        }
    });

    /// <summary>Both tab bars take the tab's name and hint from the view model, and both empty lines its "has rows".</summary>
    [Theory]
    [InlineData("src/Hearthpulse.App/Main/Pages/BattlegroundsPage.xaml", "src/Hearthpulse.App/Main/Meta/BattlegroundsTiersView.xaml")]
    [InlineData("src/Hearthpulse.UI/Main/Pages/BattlegroundsPage.axaml", "src/Hearthpulse.UI/Main/Meta/BattlegroundsTiersView.axaml")]
    public void Comps_tab_markup_binds_the_shared_properties(string page, string view)
    {
        var tab = XDocument.Load(Path.Combine(RepoFiles.Root, page)).Root!.Descendants()
            .Single(e => e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == "TabComps"));
        Assert.Equal("{Binding CompTabLabel}", (string?)tab.Attribute("Content"));
        Assert.Equal("{Binding CompCaption}", (string?)(tab.Attribute("ToolTip") ?? tab.Attribute("ToolTip.Tip")));

        var empty = XDocument.Load(Path.Combine(RepoFiles.Root, view)).Root!.Descendants()
            .Single(e => (string?)e.Attribute("Text") == "{Binding CompEmptyText}");
        Assert.StartsWith("{Binding HasCompTiers,", (string?)(empty.Attribute("Visibility") ?? empty.Attribute("IsVisible")), StringComparison.Ordinal);
    }

    private static readonly PoolTribe[] Tribes =
    [
        new("BEAST", 800, 5.0, 0.38, 0.06),
        new("MECHANICAL", 1200, 3.5, 0.61, 0.17),
        new("MIXED", 300, 4.25, 0.5, 0.1),
    ];

    private static MainViewModel Model()
    {
        var vm = new MainViewModel(new HistoryStore(Path.Combine(AppPaths.Root, $"hp-bg-tribes-{Guid.NewGuid():N}.db")), new AppSettings(), TestDataFolder.App);
        // The tier lists are built once the card names are in.
        vm.SetCards(CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(
            """[{ "id": "BG36_731", "dbfId": 132899, "name": "Imp-lusionist", "type": "MINION", "races": ["DEMON"], "techLevel": 4, "isBattlegroundsPoolMinion": true }]"""))));
        return vm;
    }

    private static BattlegroundsMeta Meta(IReadOnlyList<MetaComp> comps, IReadOnlyList<PoolTribe> tribes) =>
        new(new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>(), [], comps, new Dictionary<string, MetaTrinket>(),
            new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(), new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc))
        {
            Tribes = tribes,
        };

    private static MetaComp Comp(string id, string name, double placement) =>
        new(id, name, "A", "Easy", ["MECHANICAL"], [new MetaCompCard("BG36_731", "CORE")], null, null, placement,
            new Dictionary<int, double>(), new Dictionary<string, (int DataPoints, double Placement)>());
}
