using System.Reflection;
using System.Text;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Battlegrounds.Site;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Pool;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Battlegrounds "Comps" in both hosts: when the site's service has no comps and the stats have the pool's tribes, both
/// view models give the same tribe rows, tab name and hint (the tribe rows used to be built in the WPF shell only);
/// with comps of the service, those are the rows in both.
/// </summary>
public sealed class BgTribeRowsContractTests
{
    private static readonly PoolTribe[] Tribes =
    [
        new("BEAST", 800, 5.0, 0.38, 0.06),
        new("MECHANICAL", 1200, 3.5, 0.61, 0.17),
        new("MIXED", 300, 4.25, 0.5, 0.1),
    ];

    [Fact]
    public void Tribe_rows_and_the_tab_name_are_the_same_in_both_UIs()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var wpf = new WpfMain(History(), new AppSettings(), TestApp.Data);
        var ava = new AvaMain(History(), new AppSettings(), TestApp.Data);
        wpf.SetCards(Cards());
        ava.SetCards(Cards());

        // Tribes, and the service's load is not in yet: both say "loading" under the usual name.
        wpf.SetMeta(Meta(Tribes));
        ava.SetMeta(Meta(Tribes));
        AssertSame(wpf, ava);
        Assert.Equal((Loc.T("Main.Bg.Comps"), false, Loc.T("Main.Bg.CommunityLoading")), (ava.CompTabLabel, ava.CompTab.HasRows, ava.CompTab.EmptyText));

        // The load had no comps: the tribes are the rows, best average placement first.
        wpf.SetBgSiteStats(null);
        ava.SetBgSiteStats(null);
        Assert.Equal(3, wpf.CompTiers.Count);
        Assert.Equal([Races.Display("MECHANICAL", "enUS"), Loc.T("Main.Bg.Mixed"), Races.Display("BEAST", "enUS")], wpf.CompTiers.Cast<Hearthpulse.App.Main.MetaCompRow>().Select(r => r.Name));
        AssertSame(wpf, ava);
        Assert.Equal((Loc.T("Main.Bg.Tribes"), Loc.T("Main.Bg.CompCaptionTribes"), true), (ava.CompTabLabel, ava.CompCaption, ava.CompTab.HasRows));

        // Comps of the service: they are the rows, under their tier's head, whatever tribes the pool has.
        var site = new BgSiteStats(null, null, null, null, null, new Dictionary<string, BgSiteCardStat>(), new Dictionary<string, BgSiteCardStat>(),
            [new BgSiteComp("mechs", "Mechs", string.Empty, "A", [], ["BG36_731"], null, null, "easy", null)], null, new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc));
        wpf.SetBgSiteStats(site);
        ava.SetBgSiteStats(site);
        var comp = Assert.Single(wpf.CompTiers.OfType<Hearthpulse.App.Main.MetaCompRow>());
        Assert.Equal(("Mechs", Loc.T("Main.Bg.Core") + "Imp-lusionist"), (comp.Name, comp.Core));
        Assert.Equal(2, wpf.CompTiers.Count);
        AssertSame(wpf, ava);
        Assert.Equal((Loc.T("Main.Bg.Comps"), Loc.T("Main.Bg.CompCaptionComps")), (ava.CompTabLabel, ava.CompCaption));

        // Neither: an empty tab under the usual name, in both.
        wpf.SetBgSiteStats(site with { Comps = [] });
        ava.SetBgSiteStats(site with { Comps = [] });
        wpf.SetMeta(Meta([]));
        ava.SetMeta(Meta([]));
        AssertSame(wpf, ava);
        Assert.Equal((Loc.T("Main.Bg.Comps"), false, Loc.T("Main.Bg.ListUnavailable")), (ava.CompTabLabel, ava.CompTab.HasRows, ava.CompTab.EmptyText));
    }

    private static void AssertSame(WpfMain wpf, AvaMain ava)
    {
        Assert.Equal(wpf.CompTiers.Select(r => r.ToString()), ava.CompTiers.Select(r => r.ToString()));
        Assert.Equal((wpf.CompTabLabel, wpf.CompCaption, wpf.CompTab.ToString()), (ava.CompTabLabel, ava.CompCaption, ava.CompTab.ToString()));
    }

    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    private static CardDb Cards() => CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(
        """[{ "id": "BG36_731", "dbfId": 132899, "name": "Imp-lusionist", "type": "MINION", "races": ["DEMON"], "techLevel": 4, "isBattlegroundsPoolMinion": true }]""")));

    /// <summary>Core builds these from the API and the stats pool; its constructor is internal.</summary>
    private static BattlegroundsMeta Meta(IReadOnlyList<PoolTribe> tribes)
    {
        var meta = (BattlegroundsMeta)Activator.CreateInstance(
            typeof(BattlegroundsMeta), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [
                new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>(), Array.Empty<(int, int)>(), Array.Empty<MetaComp>(),
                new Dictionary<string, MetaTrinket>(), new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(),
                new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
            ],
            null)!;
        typeof(BattlegroundsMeta).GetProperty(nameof(BattlegroundsMeta.Tribes))!.SetValue(meta, tribes);
        return meta;
    }
}
