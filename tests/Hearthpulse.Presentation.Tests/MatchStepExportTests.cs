using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hearthpulse.App.Infrastructure;
using Hearthpulse.App.Main.Match;
using Hearthpulse.Core;
using Hearthpulse.Core.Cards;
using Hearthpulse.Core.Data;
using Hearthpulse.Core.Replay;
using WpfMain = Hearthpulse.App.Main.MainViewModel;

namespace Hearthpulse.Presentation.Tests;

/// <summary>
/// "Save the step as PNG" in WPF (plan R4): either mode's table card comes out whole at twice its size, drawn (not a
/// blank sheet) with its round corners transparent, also when the card is partly out of the window.
/// </summary>
public sealed class MatchStepExportTests
{
    [Fact]
    public void Wpf_step_picture_is_the_whole_table_card_at_twice_its_size()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new InvalidOperationException("The step picture check failed", failure);
        }
    }

    /// <summary>MatchTimelineTests.Events of Hearthpulse.Tests: a short Constructed game, ten steps.</summary>
    internal static readonly string[] ConstructedEvents =
    [
        "T 1 L 0", "E 1 L PLAY 0 HERO_08 HERO HEALTH=30", "E 2 O PLAY 0 HERO_01 HERO HEALTH=30", "E 6 L PLAY 0 HERO_08bp HERO_POWER",
        "S BEGIN_MULLIGAN", "S MAIN_READY",
        "V L RESOURCES 1", "B", "E 3 L PLAY 1 CS2_120 MINION ATK=2 HEALTH=3", "P 3 -",
        "T 2 O 30", "V O RESOURCES 2", "B", "E 4 O PLAY 1 CS2_182 MINION ATK=4 HEALTH=5", "P 4 -", "A 4 3", "V 3 DAMAGE 4",
        "T 3 L 60", "U 6 4",
        "R WON",
    ];

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
        var store = new ReplayStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N")));
        var battlegrounds = new GameRecord
        {
            Key = "match-step-png",
            StartedAt = new DateTime(2026, 11, 5, 10, 0, 0),
            EndedAt = new DateTime(2026, 11, 5, 10, 12, 0),
            Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5, Turns = 2,
        };
        battlegrounds = battlegrounds with { Replay = store.Save(battlegrounds.Key, MatchTableInPlaceTests.Game5th.ReplaceLineEndings("\n")) };
        var constructed = new GameRecord
        {
            Key = "match-step-png-ranked",
            StartedAt = new DateTime(2026, 11, 5, 11, 0, 0),
            EndedAt = new DateTime(2026, 11, 5, 11, 6, 0),
            Mode = "Ranked", GameType = "GT_RANKED", Format = "FT_STANDARD", Result = "Win", PlayerClass = "MAGE", OpponentClass = "WARRIOR", Turns = 3,
        };
        constructed = constructed with { Replay = store.Save(constructed.Key, ReplayFile.Write(new ReplayHeader(), ConstructedEvents)) };
        var model = new MatchViewModel(new WpfMain(history, new AppSettings(), TestApp.Data));
        var view = new MatchView { DataContext = model };
        // Shorter than the page: the table card is partly below the window's edge.
        var window = new Window
        {
            Content = view, Width = 1200, Height = 700, Left = -20000, Top = -20000, ShowActivated = false,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        try
        {
            foreach (var (game, card) in new[] { (battlegrounds, "BattlegroundsStepCard"), (constructed, "TableCard") })
            {
                model.Show(MatchPage.Load(game, store, CardDb.Empty, null));
                model.GoFrame.Execute(model.FrameMax / 2);
                Settle(window);
                Assert.True(model.CanExportStep);
                var element = view.ExportCard(model);
                Assert.Equal(card, element.Name);
                Assert.True(element.ActualHeight > 0);
                AssertPicture(element, MatchView.Encode(element));
            }
        }
        finally
        {
            model.Clear();
            window.Content = null;
            window.Close();
        }
    }

    /// <summary>Twice the card's size; its fill drawn at the middle and near the bottom (below the window's edge); the round corner left clear.</summary>
    internal static void AssertPicture(double width, double height, byte[] png)
    {
        using var stream = new MemoryStream(png);
        var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((int)Math.Ceiling(width * 2), frame.PixelWidth);
        Assert.Equal((int)Math.Ceiling(height * 2), frame.PixelHeight);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, frame.PixelWidth * 4, 0);
        byte Alpha(int x, int y) => pixels[((y * frame.PixelWidth) + x) * 4 + 3];
        Assert.True(Alpha(frame.PixelWidth / 2, frame.PixelHeight / 2) > 200, "the middle is not drawn");
        Assert.True(Alpha(frame.PixelWidth / 2, frame.PixelHeight - 40) > 200, "the bottom is not drawn");
        Assert.True(Alpha(0, 0) < 30, "the round corner is not clear");
        Assert.True(Alpha(frame.PixelWidth - 1, frame.PixelHeight - 1) < 30, "the round corner is not clear");
    }

    private static void AssertPicture(FrameworkElement card, byte[] png) => AssertPicture(card.ActualWidth, card.ActualHeight, png);

    private static void Settle(Window window)
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
}
