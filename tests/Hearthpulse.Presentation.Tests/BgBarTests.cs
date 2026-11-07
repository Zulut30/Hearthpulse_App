using Hearthpulse.Core;
using Hearthpulse.Core.Battlegrounds;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using WpfOverlay = Hearthpulse.App.Overlay.OverlayViewModel;
using AvaOverlay = Hearthpulse.UI.Overlay.OverlayViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// The Battlegrounds bar ("bgbar") and the combat odds ("odds") in both hosts: the lobby's tribes as a line of text,
/// the tribe filter in the tier list, and the odds cells with damage ranges and a bottom line only when needed.
/// </summary>
public sealed class BgBarTests : IDisposable
{
    public void Dispose() => Loc.Instance.SetLanguage(Loc.English);

    private static BattlegroundsView View(IReadOnlyList<string> races, CombatOdds? odds = null) =>
        new(11, 5, 6, "TB_BaconShop_HERO_72", 5, 2, 6, false, true, false, [], races, true, [], [], [], [], null, 0, [], [])
        { CombatOdds = odds };

    private static dynamic Model(bool wpf, BattlegroundsView view)
    {
        dynamic model = wpf ? new WpfOverlay() : new AvaOverlay();
        model.Update(LiveSnapshot.Empty with { Battlegrounds = view, InGame = true }, CardDb.Empty,
            new AppSettings { ShowTurnTimer = false, ShowCombatOdds = true }, null, null);
        return model;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_bar_lists_the_lobby_tribes_as_text_and_says_when_they_are_still_read(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        Assert.Equal("Tribes: Beast, Dragon", (string)Model(wpf, View(["BEAST", "DRAGON"])).TribesLine);
        Assert.Equal("Tribes: reading the lobby…", (string)Model(wpf, View([])).TribesLine);

        Loc.Instance.SetLanguage(Loc.Russian);
        Assert.StartsWith("Расы: ", (string)Model(wpf, View(["BEAST", "DRAGON"])).TribesLine, StringComparison.Ordinal);
        Assert.Equal("Расы: лобби считывается…", (string)Model(wpf, View([])).TribesLine);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_tier_button_opens_the_list_whose_tribe_chips_filter_it(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = Model(wpf, View(["BEAST", "DRAGON"]));

        model.SelectTier.Execute(3);
        Assert.Equal(3, (int)model.SelectedTier);
        Assert.Equal("Tier 3", (string)model.BrowserTitle);
        Assert.Equal(new[] { "BEAST", "DRAGON" }, ((IEnumerable<dynamic>)model.TribeChips).Select(c => (string)c.Key));

        model.SelectTribe.Execute("DRAGON");
        Assert.Equal("Dragon", (string)model.BrowserScope);
        Assert.Equal(3, (int)model.SelectedTier);
        // The same chip again lifts the filter.
        model.SelectTribe.Execute("DRAGON");
        Assert.DoesNotContain((IEnumerable<dynamic>)model.TribeChips, c => (bool)c.Selected);
        Assert.Equal("minions in this lobby", (string)model.BrowserScope);
    }

    private static CombatOdds Odds(int unsimulated = 1, double lethal = 0.311, double kill = 0.197, int cap = 0) =>
        new(0.355, 0.324, 0.321, unsimulated)
        {
            Runs = 4000,
            DamageDealt = new DamageSpread(12, 8, 16, 20, [1]),
            DamageTaken = new DamageSpread(13, 9, 17, 21, [1]),
            LethalTaken = lethal,
            LethalDealt = kill,
            DamageCap = cap,
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Odds_show_damage_ranges_both_ways_and_an_estimate_line(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.Russian);
        var model = Model(wpf, View(["BEAST"], Odds()));

        Assert.Equal("Нанесёте 8–16", (string)model.OddsDamageWin);
        Assert.Equal("Получите 9–17", (string)model.OddsDamageLoss);
        Assert.Equal("Оценка: 1 эффект не учтён", (string)model.OddsNote);
        // The cells carry the label, the value is just the share.
        Assert.Equal(Hearthpulse.App.Overlay.OverlayFormat.Percent(0.311, 1), (string)model.OddsLethal);
        Assert.Equal(Hearthpulse.App.Overlay.OverlayFormat.Percent(0.197, 1), (string)model.OddsKill);
        Assert.True((bool)model.LethalDanger);

        Loc.Instance.SetLanguage(Loc.English);
        model = Model(wpf, View(["BEAST"], Odds()));
        Assert.Equal(("You deal 8–16", "You take 9–17"), ((string)model.OddsDamageWin, (string)model.OddsDamageLoss));
        Assert.Equal("Estimate: 1 effect not simulated", (string)model.OddsNote);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Odds_drop_empty_lethal_cells_and_the_bottom_line_when_nothing_is_left_out(bool wpf)
    {
        Loc.Instance.SetLanguage(Loc.English);
        var model = Model(wpf, View(["BEAST"], Odds(unsimulated: 0, lethal: 0, kill: 0)));
        Assert.Equal(string.Empty, (string)model.OddsLethal);
        Assert.Equal(string.Empty, (string)model.OddsKill);
        Assert.Equal(string.Empty, (string)model.OddsNote);
        Assert.False((bool)model.LethalDanger);

        Assert.Equal("Hero damage capped at 15", (string)Model(wpf, View(["BEAST"], Odds(unsimulated: 0, cap: 15))).OddsNote);
        Assert.Equal("Estimate: 2 effects not simulated · hero damage capped at 15", (string)Model(wpf, View(["BEAST"], Odds(unsimulated: 2, cap: 15))).OddsNote);
    }
}
