using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Localization;
using Hearthpulse.Core.Replay;

namespace Hearthpulse.Tests;

/// <summary>
/// Duos on the Avalonia match player (<c>TASK-2026-11-02-duos-replay</c>, AC4): while our partner's table fights, the bottom
/// half shows their board and hero under the «Partner» caption in place of ours; on every other step it is ours, as before.
/// </summary>
public sealed partial class LocBindingTests
{
    [Fact]
    public void Match_battlegrounds_table_shows_the_partners_table_while_it_fights() => Headless.Run(PartnerTable);

    /// <summary>A Duos file: our tavern, a fight of our partner's table (side P), the next tavern and a fight of ours.</summary>
    private static string DuosFile() => ReplayFile.WriteBattlegrounds(
        new ReplayHeader { Summary = new ReplaySummary { Mode = "Duos", HeroCardId = "TB_BaconShop_HERO_50", Placement = 1 } },
        new BattlegroundsReplayData(),
        [
            "T 1 30",
            "E 1 L PLAY 0 TB_BaconShop_HERO_50 HERO HEALTH=30",
            "E 2 L PLAY 1 BGS_004 MINION ATK=2 HEALTH=3",
            "F 1 75",
            "E 5 O PLAY 0 TB_BaconShop_HERO_01 HERO HEALTH=30",
            "E 6 O PLAY 1 BGS_039 MINION ATK=1 HEALTH=2",
            "E 9 P PLAY 0 TB_BaconShop_HERO_08 HERO HEALTH=30",
            "E 8 P PLAY 1 BG25_011 MINION ATK=4 HEALTH=4",
            "M ATTACK 8 6",
            "f WIN 0 3",
            "X 5",
            "X 6",
            "X 8",
            "X 9",
            "T 2 90",
            "F 2 150",
            "E 11 O PLAY 0 TB_BaconShop_HERO_02 HERO HEALTH=30",
            "E 12 O PLAY 1 BGS_039 MINION ATK=1 HEALTH=2",
            "M ATTACK 2 12",
            "f WIN 0 2",
        ]);

    private static void PartnerTable()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"hp-match-duos-{Guid.NewGuid():N}");
        var (view, model, window, theme) = OpenMatch(folder, store =>
        {
            var game = MatchTimelineTests.Game(null) with { Mode = "Duos", GameType = "GT_BATTLEGROUNDS_DUO", Placement = 1 };
            return MatchPage.Load(game with { Replay = store.Save(game.Key, DuosFile()) }, store, CardDb.Empty, null);
        });
        try
        {
            var table = view.FindControl<Grid>("BattlegroundsTable")!;
            Assert.True(model.ShowBattlegroundsTable && table.IsVisible);
            var partnerPanel = view.FindControl<StackPanel>("BattlegroundsPartner")!;
            var caption = Loc.T("Match.Player.Table.Partner");
            IReadOnlyList<MatchBgCardView>? partnerBoard = null;
            int partnerSteps = 0, combatSteps = 0, ourSteps = 0;
            for (var frame = 0; frame <= model.FrameMax; frame++)
            {
                model.GoFrame.Execute(frame);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var step = model.BattlegroundsStep!;
                var lists = table.GetVisualDescendants().OfType<ItemsControl>().Where(i => i.IsEffectivelyVisible).Select(i => i.ItemsSource).ToList();
                var heroes = table.GetVisualDescendants().OfType<ContentControl>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content).ToList();
                var texts = partnerPanel.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
                Assert.Equal(step.PartnerFighting, partnerPanel.IsEffectivelyVisible);
                if (step.PartnerFighting)
                {
                    partnerSteps++;
                    Assert.True(step.InCombat, step.Title);
                    Assert.Contains(lists, l => ReferenceEquals(l, step.PartnerBoard));
                    Assert.DoesNotContain(lists, l => ReferenceEquals(l, step.Board));
                    Assert.DoesNotContain(heroes, h => h is not null && ReferenceEquals(h, step.Hero));
                    Assert.Contains(caption, texts);
                    Assert.Contains(step.PartnerBoard, c => c.CardId == "BG25_011");
                    // The partner's board is one list the view keeps from step to step, synced in place like ours.
                    partnerBoard ??= step.PartnerBoard;
                    Assert.Same(partnerBoard, step.PartnerBoard);
                }
                else
                {
                    ourSteps++;
                    combatSteps += step.InCombat ? 1 : 0;
                    Assert.Contains(lists, l => ReferenceEquals(l, step.Board));
                    Assert.DoesNotContain(lists, l => ReferenceEquals(l, step.PartnerBoard));
                    Assert.Empty(step.PartnerBoard);
                    if (step.Hero is not null)
                    {
                        Assert.Contains(heroes, h => ReferenceEquals(h, step.Hero));
                    }
                }

                // The opponent's half stays theirs in every combat.
                if (step.InCombat)
                {
                    Assert.Contains(lists, l => ReferenceEquals(l, step.OpponentBoard));
                }
            }

            Assert.True(partnerSteps > 0, "no step showed the partner's table");
            Assert.True(ourSteps > 0 && combatSteps > 0, "no combat of our own table was shown");
        }
        finally
        {
            CloseMatch(model, window, theme, folder);
        }
    }
}
