using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The "graveyard" panel's model: the opponent's dead minions above yours, "×2" for repeats, "+N more" past the row limit,
    /// "None died yet" for an empty side; hidden with the setting off or before the first death. The Avalonia panel
    /// loads over it (its bindings are compiled).
    /// </summary>
    [Fact]
    public void Graveyard_panel_binds_both_sides_repeats_and_the_rest() => Headless.Run(GraveyardPanelBindings);

    private static void GraveyardPanelBindings()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var cards = CardDb.Parse("enUS", new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "[" + string.Join(',', Enumerable.Range(1, 12).Select(i => $$"""{"id":"M{{i}}","dbfId":{{i}},"name":"Minion {{i:00}}","type":"MINION","cost":{{i % 5}}}""")) + "]")));
        var graveyard = new GraveyardView([], 0, GraveyardView.Rows(["M1", "M1", .. Enumerable.Range(2, 11).Select(i => $"M{i}")], cards), 13);
        ConstructedView View(GraveyardView? g) => new(5, null, false, "HERO_09", "PRIEST", "HERO_08", "MAGE", null, [], 20, 4, [], [], 18, 5, 0, null, null) { Graveyard = g };
        var model = new OverlayViewModel();

        model.Update(LiveSnapshot.Empty with { Constructed = View(graveyard), InGame = true, HearthstoneRunning = true }, cards, new AppSettings(), null, null);
        Assert.False(model.ShowGraveyard);

        var settings = new AppSettings { ShowGraveyard = true };
        model.Update(LiveSnapshot.Empty with { Constructed = View(graveyard), InGame = true, HearthstoneRunning = true }, cards, settings, null, null);

        Assert.True(model.ShowGraveyard);
        Assert.Equal(Loc.F("Related.Graveyard.Enemy", "13"), model.GraveyardOpponentTitle);
        Assert.Equal(Loc.F("Related.Graveyard.Mine", "0"), model.GraveyardLocalTitle);
        Assert.Equal(OverlayViewModel.GraveyardRows, model.GraveyardOpponent.Count);
        Assert.Equal("×2", model.GraveyardOpponent.Single(r => r.CardId == "M1").CountText);
        Assert.Equal(Loc.F("Related.Graveyard.More", 12 - OverlayViewModel.GraveyardRows), model.GraveyardOpponentMore);
        Assert.Equal(Loc.T("Related.Graveyard.Empty"), model.GraveyardLocalMore);
        var app = Avalonia.Application.Current!;
        var theme = Theme();
        try
        {
            Assert.NotNull(new GraveyardPanel { DataContext = model });
        }
        finally
        {
            app.Styles.Remove(theme);
        }

        model.Update(LiveSnapshot.Empty with { Constructed = View(null), InGame = true, HearthstoneRunning = true }, cards, settings, null, null);
        Assert.False(model.ShowGraveyard);
    }
}
