using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Constructed;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

public sealed partial class LocBindingTests
{
    /// <summary>
    /// The "Likely deck" panel's model: archetype and chance, the meta's rank and period in the source line, "2" / "1/2" /
    /// "★" marks, all-played rows done, "Not in the list" with "+N more" past four; hidden with the setting off or
    /// without a view. The Avalonia panel loads over it (its bindings are compiled).
    /// </summary>
    [Fact]
    public void Likely_deck_panel_binds_marks_source_and_off_list() => Headless.Run(LikelyDeckPanelBindings);

    private static void LikelyDeckPanelBindings()
    {
        Loc.Instance.SetLanguage(Loc.English);
        LikelyDeckRow Row(string id, int cost, string rarity, int copies, int played) => new(id, id, cost, rarity, copies, played);
        var deck = new LikelyDeckView("Control Priest", 0.62,
            [Row("A", 1, "COMMON", 2, 0), Row("B", 2, "COMMON", 2, 1), Row("C", 3, "RARE", 2, 2), Row("D", 4, "EPIC", 1, 0), Row("L", 5, "LEGENDARY", 1, 1)],
            [Row("X1", 1, "COMMON", 2, 2), Row("X2", 2, "COMMON", 1, 1), Row("X3", 3, "COMMON", 1, 1), Row("X4", 4, "COMMON", 1, 1), Row("X5", 5, "COMMON", 1, 1)],
            "legend", "past_week", 900);
        ConstructedView View(LikelyDeckView? d) => new(5, null, false, "HERO_09", "PRIEST", "HERO_09", "PRIEST", null, [], 20, 4, [], [], 18, 5, 0, null, null) { OpponentDeck = d };
        var model = new OverlayViewModel();

        model.Update(LiveSnapshot.Empty with { Constructed = View(deck), InGame = true, HearthstoneRunning = true }, CardDb.Empty, new AppSettings(), null, null);
        Assert.False(model.ShowLikelyDeck);

        var settings = new AppSettings { ShowLikelyDeck = true };
        model.Update(LiveSnapshot.Empty with { Constructed = View(deck), InGame = true, HearthstoneRunning = true }, CardDb.Empty, settings, null, null);

        Assert.True(model.ShowLikelyDeck);
        Assert.Equal("62%", model.LikelyDeckChance.Replace(" ", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal));
        Assert.Equal(Loc.F("OpponentDeck.Source", ConstructedMeta.Label(ConstructedMeta.Ranks, "legend"), ConstructedMeta.Label(ConstructedMeta.Periods, "past_week")), model.LikelyDeckSource);
        Assert.Contains("Legend", model.LikelyDeckSource, StringComparison.Ordinal);
        Assert.Empty(model.LikelyDeckCoreNote);
        Assert.Equal([("A", "2", false), ("B", "1/2", false), ("C", "2/2", true), ("D", "", false), ("L", "★", true)],
            model.LikelyDeckRows.Select(r => (r.CardId, r.Mark, r.Done)));
        Assert.Equal(Loc.F("OpponentDeck.OffList", "5"), model.LikelyDeckOffTitle);
        Assert.Equal(["×2", "", "", ""], model.LikelyDeckOffList.Select(r => r.Mark));
        Assert.Equal(Loc.F("OpponentDeck.More", 1), model.LikelyDeckOffMore);

        var app = Avalonia.Application.Current!;
        var theme = Theme();
        try
        {
            Assert.NotNull(new LikelyDeckPanel { DataContext = model });
        }
        finally
        {
            app.Styles.Remove(theme);
        }

        // Only core cards known: the note says so; no off-list cards: no section.
        model.Update(LiveSnapshot.Empty with { Constructed = View(deck with { Games = 0, OffList = [] }), InGame = true, HearthstoneRunning = true }, CardDb.Empty, settings, null, null);
        Assert.Equal(Loc.T("OpponentDeck.CoreOnly"), model.LikelyDeckCoreNote);
        Assert.Empty(model.LikelyDeckOffTitle);
        Assert.Empty(model.LikelyDeckOffList);

        // m6: at a larger overlay scale the panel is shorter, so it still ends above End Turn (466 in 1080 screen units).
        Assert.Equal(OverlayViewModel.LikelyDeckHeight, model.LikelyDeckMaxHeight);
        foreach (var scale in new[] { 1.25, 1.6 })
        {
            settings.OverlayScale = scale;
            model.Update(LiveSnapshot.Empty with { Constructed = View(deck), InGame = true, HearthstoneRunning = true }, CardDb.Empty, settings, null, null);
            Assert.True(model.LikelyDeckMaxHeight < OverlayViewModel.LikelyDeckHeight);
            Assert.True((76 + model.LikelyDeckMaxHeight) * scale <= 466, $"scale {scale}: {model.LikelyDeckMaxHeight}");
        }

        model.Update(LiveSnapshot.Empty with { Constructed = View(null), InGame = true, HearthstoneRunning = true }, CardDb.Empty, settings, null, null);
        Assert.False(model.ShowLikelyDeck);
    }
}
