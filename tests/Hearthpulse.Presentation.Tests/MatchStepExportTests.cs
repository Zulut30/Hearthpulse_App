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
/// "Save the step as PNG" in WPF (plan R4): the Battlegrounds table card comes out whole at twice its size, drawn (not a
/// blank sheet), also when the page is scrolled so the card is partly out of the window.
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

    private static void Check()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(ThemeEngine.Build(ThemePalette.Resolve("Tavern", 0, 0, 1)));
        var history = new HistoryStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N") + ".db"));
        var store = new ReplayStore(Path.Combine(AppPaths.Root, Guid.NewGuid().ToString("N")));
        var game = new GameRecord
        {
            Key = "match-step-png",
            StartedAt = new DateTime(2026, 11, 5, 10, 0, 0),
            EndedAt = new DateTime(2026, 11, 5, 10, 12, 0),
            Mode = "Battlegrounds", GameType = "GT_BATTLEGROUNDS", Placement = 5, Turns = 2,
        };
        game = game with { Replay = store.Save(game.Key, MatchTableInPlaceTests.Game5th.ReplaceLineEndings("\n")) };
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
            model.Show(MatchPage.Load(game, store, CardDb.Empty, null));
            model.GoFrame.Execute(model.FrameMax / 2);
            Settle(window);
            Assert.True(model.CanExportStep);
            var card = view.ExportCard(model);
            Assert.Equal("BattlegroundsStepCard", card.Name);
            Assert.True(card.ActualHeight > 0);

            var encoder = MatchView.Encode(card);
            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;
            var frame = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Assert.Equal((int)Math.Ceiling(card.ActualWidth * 2), frame.PixelWidth);
            Assert.Equal((int)Math.Ceiling(card.ActualHeight * 2), frame.PixelHeight);

            // The card's fill at its middle and near its bottom (below the window's edge): drawn, not transparent.
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0).CopyPixels(pixels, frame.PixelWidth * 4, 0);
            Assert.True(Alpha(frame.PixelWidth / 2, frame.PixelHeight / 2) > 200);
            Assert.True(Alpha(frame.PixelWidth / 2, frame.PixelHeight - 40) > 200);

            byte Alpha(int x, int y) => pixels[((y * frame.PixelWidth) + x) * 4 + 3];
        }
        finally
        {
            model.Clear();
            window.Content = null;
            window.Close();
        }
    }

    private static void Settle(Window window)
    {
        var frame = new DispatcherFrame();
        _ = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        window.UpdateLayout();
    }
}
