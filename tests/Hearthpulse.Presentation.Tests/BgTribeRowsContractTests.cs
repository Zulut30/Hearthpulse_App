using System.Reflection;
using System.Text;
using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Pool;
using WpfMain = Hearthpulse.App.Main.MainViewModel;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// Battlegrounds "Comps" in both hosts: stats with the pool's tribes and no comps give the same tribe rows, tab name and
/// hint in the WPF and the Avalonia view model (the tribe rows used to be built in the WPF shell only).
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

        // Tribes and no comps: the tribes are the rows.
        wpf.SetMeta(Meta([], Tribes));
        ava.SetMeta(Meta([], Tribes));
        Assert.Equal(3, wpf.CompTiers.Count);
        Assert.Equal([Races.Display("MECHANICAL", "enUS"), Loc.T("Main.Bg.Mixed"), Races.Display("BEAST", "enUS")], wpf.CompTiers.Select(r => r.Name));
        AssertSame(wpf, ava);
        Assert.Equal((Loc.T("Main.Bg.Tribes"), Loc.T("Main.Bg.CompCaptionTribes"), true), (ava.CompTabLabel, ava.CompCaption, ava.HasCompTiers));

        // Comps: they are the rows, as before, whatever tribes came with them.
        var comp = new MetaComp("mechs", "Mechs", "A", "Easy", ["MECHANICAL"], [new MetaCompCard("BG36_731", "CORE")], null, null, 2.9,
            new Dictionary<int, double>(), new Dictionary<string, (int DataPoints, double Placement)>());
        wpf.SetMeta(Meta([comp], Tribes));
        ava.SetMeta(Meta([comp], Tribes));
        Assert.Equal(["Mechs"], wpf.CompTiers.Select(r => r.Name));
        Assert.Equal("Imp-lusionist", wpf.CompTiers[0].KeyCards);
        AssertSame(wpf, ava);
        Assert.Equal((Loc.T("Main.Bg.Comps"), Loc.T("Main.Bg.CompCaptionComps")), (ava.CompTabLabel, ava.CompCaption));

        // Neither: an empty tab under the usual name, in both.
        wpf.SetMeta(Meta([], []));
        ava.SetMeta(Meta([], []));
        AssertSame(wpf, ava);
        Assert.Equal((Loc.T("Main.Bg.Comps"), false), (ava.CompTabLabel, ava.HasCompTiers));
    }

    private static void AssertSame(WpfMain wpf, AvaMain ava)
    {
        Assert.Equal(
            wpf.CompTiers.Select(r => (r.Rank, r.Name, r.Tribes, r.Power, r.Difficulty, r.Placement, r.BarFraction, r.Commit, r.KeyCards)),
            ava.CompTiers.Select(r => (r.Rank, r.Name, r.Tribes, r.Power, r.Difficulty, r.Placement, r.BarFraction, r.Commit, r.KeyCards)));
        Assert.Equal((wpf.CompTabLabel, wpf.CompCaption, wpf.HasCompTiers, wpf.CompEmptyText), (ava.CompTabLabel, ava.CompCaption, ava.HasCompTiers, ava.CompEmptyText));
    }

    private static HistoryStore History() => new(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));

    private static CardDb Cards() => CardDb.Parse("enUS", new MemoryStream(Encoding.UTF8.GetBytes(
        """[{ "id": "BG36_731", "dbfId": 132899, "name": "Imp-lusionist", "type": "MINION", "races": ["DEMON"], "techLevel": 4, "isBattlegroundsPoolMinion": true }]""")));

    /// <summary>Core builds these from the API and the stats pool; its constructor is internal.</summary>
    private static BattlegroundsMeta Meta(IReadOnlyList<MetaComp> comps, IReadOnlyList<PoolTribe> tribes)
    {
        var meta = (BattlegroundsMeta)Activator.CreateInstance(
            typeof(BattlegroundsMeta), BindingFlags.Instance | BindingFlags.NonPublic, null,
            [
                new Dictionary<int, IReadOnlyDictionary<string, MetaHero>>(), Array.Empty<(int, int)>(), comps,
                new Dictionary<string, MetaTrinket>(), new Dictionary<int, IReadOnlyDictionary<string, MetaCard>>(),
                new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc),
            ],
            null)!;
        typeof(BattlegroundsMeta).GetProperty(nameof(BattlegroundsMeta.Tribes))!.SetValue(meta, tribes);
        return meta;
    }
}
