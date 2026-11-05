using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using AvaMain = Hearthpulse.UI.Main.MainViewModel;
using AvaMatch = Hearthpulse.UI.Main.Match.MatchViewModel;
using AvaMatchView = Hearthpulse.UI.Main.Match.MatchView;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// "Save the step as PNG" in Avalonia (plan R4): on a headless platform that really draws (Skia, as <c>--render-ui</c>),
/// either mode's table card comes out as a PNG at twice its size, drawn, with its round corners clear, also when the
/// card is partly out of the window. Hearthpulse.Tests' headless platform draws nothing, so the check lives here.
/// </summary>
public sealed class MatchStepExportAvaloniaTests
{
    [Fact]
    public void Avalonia_step_picture_is_the_whole_table_card_at_twice_its_size()
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<Hearthpulse.UI.HearthpulseApp>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                    .WithInterFont()
                    .SetupWithoutStarting();
                Check();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Avalonia step picture",
        };
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static void Check()
    {
        var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
        var store = new ReplayStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N")));
        var battlegrounds = new GameRecord
        {
            Key = "match-step-png-ava",
            StartedAt = new DateTime(2026, 11, 5, 10, 0, 0),
            EndedAt = new DateTime(2026, 11, 5, 10, 12, 0),
            Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5, Turns = 2,
        };
        battlegrounds = battlegrounds with { Replay = store.Save(battlegrounds.Key, MatchTableInPlaceTests.Game5th.ReplaceLineEndings("\n")) };
        var constructed = new GameRecord
        {
            Key = "match-step-png-ava-ranked",
            StartedAt = new DateTime(2026, 11, 5, 11, 0, 0),
            EndedAt = new DateTime(2026, 11, 5, 11, 6, 0),
            Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Win", PlayerClass = "MAGE", OpponentClass = "WARRIOR", Turns = 3,
        };
        constructed = constructed with { Replay = store.Save(constructed.Key, ReplayFile.Write(new ReplayHeader(), MatchStepExportTests.ConstructedEvents)) };

        var model = new AvaMatch(new AvaMain(history, new AppSettings(), TestApp.Data));
        var view = new AvaMatchView { DataContext = model };
        // Shorter than the page: the table card is partly below the window's edge.
        var window = new Window { Width = 1200, Height = 700, Content = view };
        window.Show();
        try
        {
            foreach (var (game, name) in new[] { (battlegrounds, "BattlegroundsCard"), (constructed, "TableCard") })
            {
                model.Show(MatchPage.Load(game, store, CardDb.Empty, null));
                model.GoFrame.Execute(model.FrameMax / 2);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Assert.True(model.CanExportStep);
                var card = view.FindControl<Border>(name) ?? throw new InvalidOperationException(name + " is not in the view");
                Assert.True(card.IsEffectivelyVisible && card.Bounds.Height > 0, name);
                MatchStepExportTests.AssertPicture(card.Bounds.Width, card.Bounds.Height, AvaMatchView.Encode(card));
            }
        }
        finally
        {
            model.Clear();
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
