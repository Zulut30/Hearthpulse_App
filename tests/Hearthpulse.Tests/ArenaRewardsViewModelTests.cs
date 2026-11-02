using Hearthpulse.Core.Arena;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Stats;
using Hearthpulse.UI.Main;

namespace Hearthpulse.Tests;

/// <summary>
/// The rewards on My Stats → Arena (<c>docs/arena-rewards.md</c>): the run chip's parts, the editor's checks and its
/// save / clear / cancel, the period's "Rewards" tile. The shared view model, through the Avalonia flavour.
/// </summary>
public sealed class ArenaRewardsViewModelTests
{
    public ArenaRewardsViewModelTests() => Loc.Instance.SetLanguage(Loc.English);

    [Fact]
    public void The_chip_shows_only_rewards_above_zero_in_order()
    {
        var parts = MainViewModel.RewardParts(new ArenaRewards(150, 0, 1, 0, 2));

        Assert.Equal(["\U000F0B38", "\U000F03D7", "\U000F06C7"], parts.Select(p => p.Glyph));
        Assert.Equal(["150", "1", "2"], parts.Select(p => p.Amount));
        Assert.Empty(MainViewModel.RewardParts(null));
        Assert.Equal("150 gold, 1 pack, 2 cards", MainViewModel.RewardsText(new ArenaRewards(150, 0, 1, 0, 2)));
    }

    [Fact]
    public void The_editor_checks_each_field_and_saves_whole_numbers()
    {
        (string Key, ArenaRewards? Rewards)? saved = null;
        var cancelled = false;
        var edit = new ArenaRewardsEdit("deck:1", "Run rewards: Hunter, today", new ArenaRewards(150, 0, 0, 0, 0),
            (e, r) => saved = (e.Key, r), () => cancelled = true);

        Assert.Equal("150", edit.Gold);
        Assert.Equal(string.Empty, edit.Dust);
        Assert.True(edit.CanClear);
        Assert.True(edit.CanSave);

        foreach (var bad in new[] { "-5", "1 0", "abc", "4,0", "100000" })
        {
            edit.Dust = bad;
            Assert.False(edit.CanSave);
            Assert.StartsWith("Dust:", edit.Error, StringComparison.Ordinal);
        }

        edit.Save.Execute(null);
        Assert.Null(saved);

        edit.Dust = " 40 ";
        edit.Packs = "1";
        Assert.Empty(edit.Error);
        edit.Save.Execute(null);
        Assert.Equal(("deck:1", new ArenaRewards(150, 40, 1, 0, 0)), saved);

        edit.Clear.Execute(null);
        Assert.Equal(("deck:1", (ArenaRewards?)null), saved);

        edit.Cancel.Execute(null);
        Assert.True(cancelled);
    }

    [Fact]
    public void A_new_editor_is_empty_and_cannot_clear()
    {
        var edit = new ArenaRewardsEdit("deck:2", "x", null, (_, _) => { }, () => { });

        Assert.False(edit.CanClear);
        Assert.Equal(new ArenaRewards(0, 0, 0, 0, 0), edit.Parse());
        Assert.Equal(string.Empty, edit.Gold);
    }

    [Fact]
    public void The_tile_sums_the_periods_runs_and_says_when_none_has_rewards()
    {
        var start = new DateTime(2026, 11, 1, 20, 0, 0, DateTimeKind.Utc);
        ArenaRun Run(long id) => new("HUNTER", id, start, start, 3, 3, true, []);
        var rewards = new Dictionary<string, ArenaRewards>
        {
            ["deck:1"] = new(150, 40, 1, 0, 0),
            ["deck:2"] = new(1100, 300, 5, 1, 0),
            ["deck:9"] = new(9999, 0, 0, 0, 0),
        };

        var tile = MainViewModel.RewardsTile([Run(1), Run(2), Run(3)], rewards);
        Assert.Equal(Loc.T("Stats.Arena.Rewards.Tile"), tile.Label);
        Assert.Equal(1250.ToString("N0", System.Globalization.CultureInfo.CurrentCulture), tile.Value);
        Assert.Equal("gold · 340 dust · 6 packs · 1 ticket", tile.Detail);

        var none = MainViewModel.RewardsTile([Run(3)], rewards);
        Assert.Equal("—", none.Value);
        Assert.Equal(Loc.T("Stats.Arena.Rewards.TileEmpty"), none.Detail);
    }

    [Fact]
    public void Russian_reward_texts_are_there()
    {
        var ru = Loc.Table(Loc.Russian);
        Assert.Equal("Добавить награды", ru["Stats.Arena.Rewards.Add"]);
        Assert.Equal("пыль {0}", ru["Stats.Arena.Rewards.DustPart"]);
    }
}
