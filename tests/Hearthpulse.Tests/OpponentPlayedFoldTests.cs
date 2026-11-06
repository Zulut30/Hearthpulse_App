using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Tracking;
using Hearthpulse.UI.Overlay;

namespace Hearthpulse.Tests;

/// <summary>
/// D3, A10: in a long game the opponent panel folds its oldest played cards into one "played earlier" line, so at the
/// default spots it ends above the counters panel; the newest stay listed in the view's order, the folded ones are named
/// in the line's tip.
/// </summary>
public sealed class OpponentPlayedFoldTests
{
    [Fact]
    public void A_short_game_lists_every_played_card()
    {
        var model = Update(Played(8), handRows: 3);
        Assert.Equal(8, model.Played.Count);
        Assert.Equal(string.Empty, model.PlayedEarlier);
    }

    [Fact]
    public void A_long_game_folds_the_oldest_and_keeps_the_newest_in_order()
    {
        Loc.Instance.SetLanguage(Loc.English);
        var played = Played(20);
        var model = Update(played, handRows: 2);

        // The budget in all: 2 in hand, the "played earlier" line and the newest played.
        Assert.Equal(OverlayViewModel.OpponentRowBudget - 2 - 1, model.Played.Count);
        var listed = model.Played.Select(p => p.CardId).ToList();
        Assert.Equal(played.Select(p => p.CardId).Where(listed.Contains), listed);
        Assert.All(played.Where(p => p.FirstTurn > 7), p => Assert.Contains(p.CardId, listed));
        // Card 0, the oldest, was played twice: the line counts copies.
        Assert.Equal(Loc.F("Overlay.Opponent.PlayedEarlier", played.Count - model.Played.Count + 1), model.PlayedEarlier);
        Assert.StartsWith("Card 0 ×2", model.PlayedEarlierTip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_full_hand_still_leaves_a_few_played_rows()
    {
        // More hand rows than a hand holds: the floor of played rows still holds.
        var model = Update(Played(20), handRows: 14);
        Assert.Equal(OverlayViewModel.PlayedRowsAtLeast - 1, model.Played.Count);
    }

    /// <summary>Cards in cost order (the view's order) played one a turn from the last turn back, card 0 on turn 1.</summary>
    private static List<PlayedCard> Played(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new PlayedCard($"C{i:00}", $"Card {i}", i % 10, i == 0 ? 2 : 1, i + 1, false))
            .OrderBy(p => p.Cost).ThenBy(p => p.CardId, StringComparer.Ordinal)];

    private static OverlayViewModel Update(List<PlayedCard> played, int handRows)
    {
        var hand = Enumerable.Range(1, handRows).Select(i => new HandSlot(i, 1, null, null, false, false, null)).ToList();
        var view = new ConstructedView(12, null, false, "HERO_08", "MAGE", "HERO_09", "PRIEST", null, [], 10, 3, played, hand, 10, handRows, 0, null, null);
        var model = new OverlayViewModel();
        model.Update(LiveSnapshot.Empty with { Scene = "GAMEPLAY", HearthstoneRunning = true, InGame = true, GameType = "GT_RANKED", Constructed = view },
            CardDb.Empty, new AppSettings(), null, null);
        return model;
    }
}
